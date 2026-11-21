using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    public interface IIntegrityRunner
    {
        /// <summary>
        /// Starts a verify-all pass in the background and returns its run record. When a pass is
        /// already running, returns that run instead of starting a second one.
        /// </summary>
        Task<IntegrityRun> StartAsync(string? startedBy);

        /// <summary>Completes when the pass currently running (if any) has finished. For tests and shutdown.</summary>
        Task WaitForCurrentAsync();
    }

    /// <summary>
    /// Walks every archived email in id order, verifies it with <see cref="IIntegrityService"/> and
    /// records the run: counters per status after each batch, and a finding for every result that is
    /// not Valid. One pass at a time. Read-only with respect to the archive.
    /// </summary>
    public sealed class IntegrityRunner : IIntegrityRunner
    {
        private const int BatchSize = 200;

        private readonly IServiceScopeFactory _scopes;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ILogger<IntegrityRunner> _logger;
        private readonly object _gate = new();
        private Task? _current;
        private int _currentRunId;

        public IntegrityRunner(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<IntegrityRunner> logger)
        {
            _scopes = scopes;
            _lifetime = lifetime;
            _logger = logger;
        }

        public async Task<IntegrityRun> StartAsync(string? startedBy)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();

            lock (_gate)
            {
                if (_current is { IsCompleted: false })
                {
                    return db.IntegrityRuns.AsNoTracking().First(r => r.Id == _currentRunId);
                }
            }

            // A run still marked Running with no pass active in this process was interrupted (restart, crash).
            await db.IntegrityRuns
                .Where(r => r.Status == IntegrityRun.Running)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, IntegrityRun.Failed)
                    .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                    .SetProperty(r => r.Error, "Interrupted before it finished (the application stopped)."));

            var run = new IntegrityRun
            {
                StartedAt = DateTime.UtcNow,
                Total = await db.ArchivedEmails.CountAsync(),
                StartedBy = startedBy
            };
            db.IntegrityRuns.Add(run);
            await db.SaveChangesAsync();

            lock (_gate)
            {
                _currentRunId = run.Id;
                _current = Task.Run(() => RunAsync(run.Id, _lifetime.ApplicationStopping));
            }

            _logger.LogInformation("Integrity verify-all run {RunId} started by {StartedBy}: {Total} emails", run.Id, startedBy, run.Total);
            return run;
        }

        public Task WaitForCurrentAsync()
        {
            Task? current;
            lock (_gate) current = _current;
            return current ?? Task.CompletedTask;
        }

        private async Task RunAsync(int runId, CancellationToken cancellationToken)
        {
            var status = IntegrityRun.Completed;
            string? error = null;
            var lastId = 0;

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var scope = _scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                    var integrity = scope.ServiceProvider.GetRequiredService<IIntegrityService>();

                    var ids = await db.ArchivedEmails.AsNoTracking()
                        .Where(e => e.Id > lastId).OrderBy(e => e.Id).Select(e => e.Id).Take(BatchSize)
                        .ToListAsync(cancellationToken);
                    if (ids.Count == 0) break;

                    int valid = 0, invalid = 0, missing = 0, notStored = 0, inconsistent = 0, errors = 0;
                    var findings = new List<IntegrityFinding>();

                    foreach (var id in ids)
                    {
                        try
                        {
                            var report = await integrity.VerifyAsync(id, cancellationToken);
                            switch (report.Status)
                            {
                                case IntegrityStatus.Valid: valid++; break;
                                case IntegrityStatus.Invalid: invalid++; break;
                                case IntegrityStatus.Missing: missing++; break;
                                case IntegrityStatus.NotStored: notStored++; break;
                                case IntegrityStatus.Inconsistent: inconsistent++; break;
                            }

                            if (report.Status != IntegrityStatus.Valid)
                            {
                                findings.Add(new IntegrityFinding
                                {
                                    RunId = runId, EmailId = id, Status = report.Status.ToString(),
                                    Details = string.Join(" ", report.Findings)
                                });
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            errors++;
                            findings.Add(new IntegrityFinding { RunId = runId, EmailId = id, Status = "Error", Details = ex.Message });
                            _logger.LogWarning(ex, "Integrity verification of email {EmailId} failed unexpectedly", id);
                        }
                    }

                    lastId = ids[^1];

                    db.IntegrityFindings.AddRange(findings);
                    await db.SaveChangesAsync(cancellationToken);
                    var count = ids.Count;
                    await db.IntegrityRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u
                        .SetProperty(r => r.Checked, r => r.Checked + count)
                        .SetProperty(r => r.Valid, r => r.Valid + valid)
                        .SetProperty(r => r.Invalid, r => r.Invalid + invalid)
                        .SetProperty(r => r.Missing, r => r.Missing + missing)
                        .SetProperty(r => r.NotStored, r => r.NotStored + notStored)
                        .SetProperty(r => r.Inconsistent, r => r.Inconsistent + inconsistent)
                        .SetProperty(r => r.Errors, r => r.Errors + errors), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                status = IntegrityRun.Cancelled;
                error = "Cancelled because the application is stopping.";
            }
            catch (Exception ex)
            {
                status = IntegrityRun.Failed;
                error = ex.Message;
                _logger.LogError(ex, "Integrity verify-all run {RunId} failed", runId);
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                await db.IntegrityRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                    .SetProperty(r => r.Error, error));
                _logger.LogInformation("Integrity verify-all run {RunId} finished: {Status}", runId, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not record the end of integrity run {RunId}", runId);
            }
        }
    }

    /// <summary>Starts a verify-all run every <c>Integrity:VerifyAllIntervalHours</c> hours (off by default).</summary>
    public sealed class IntegrityScheduleService : BackgroundService
    {
        private readonly IIntegrityRunner _runner;
        private readonly IntegrityOptions _options;
        private readonly ILogger<IntegrityScheduleService> _logger;

        public IntegrityScheduleService(IIntegrityRunner runner, IOptions<IntegrityOptions> options, ILogger<IntegrityScheduleService> logger)
        {
            _runner = runner;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.VerifyAllIntervalHours <= 0) return;

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, _options.ScheduleStartupDelayMinutes)), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    try { await _runner.StartAsync("schedule"); }
                    catch (Exception ex) { _logger.LogError(ex, "Could not start the scheduled integrity run"); }

                    await Task.Delay(TimeSpan.FromHours(_options.VerifyAllIntervalHours), stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }
}
