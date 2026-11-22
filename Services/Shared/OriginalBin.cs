using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    public sealed record OriginalBinStatus(
        bool Enabled, bool Applicable, string? NotApplicableReason, int BinRetentionDays, int IntervalHours, int MaxFilesPerRun,
        int InBin, long InBinBytes, DateTime? EarliestPurgeAt, long TotalBinned, long TotalRestored, long TotalPurged, OriginalCleanupRun? LastRun);

    public sealed record OriginalBinItem(string Sha256, DateTime BinnedAt, DateTime PurgeAfter, long Size);

    public sealed record OriginalRestoreResult(bool Restored, string Message);

    public sealed record ErasureStatus(int EmailId, string State, string Message, string? Sha256, int ReferencedByRows, DateTime? BinnedAt, DateTime? PurgeAfter);

    public interface IOriginalBin
    {
        /// <summary>
        /// Starts one pass in the background and returns its run record; returns the running one when a pass is
        /// already active. Throws InvalidOperationException (with the reason) when the bin is switched off or does
        /// not apply to this archive. Accepts no path: it only ever works on hash-named files under the originals folder.
        /// </summary>
        Task<OriginalCleanupRun> StartAsync(string? startedBy);

        /// <summary>Runs one pass and waits for it (scheduler, tests). Same rules as <see cref="StartAsync"/>.</summary>
        Task<OriginalCleanupRun> RunOnceAsync(string? startedBy, CancellationToken cancellationToken = default);

        Task WaitForCurrentAsync();
        Task<OriginalBinStatus> GetStatusAsync();
        Task<IReadOnlyList<OriginalBinItem>> ListBinAsync(int limit);

        /// <summary>Moves one binned original (by SHA-256, never by path) back to its live place. It stays live for at least a day so it can be re-imported.</summary>
        Task<OriginalRestoreResult> RestoreAsync(string sha256, string? actor);

        /// <summary>Read-only: where the original of one ALREADY DELETED email is, and when it can be purged. Changes nothing.</summary>
        Task<ErasureStatus> DescribeEmailAsync(int emailId);
    }

    /// <summary>
    /// Recycle bin for hash-named originals (review F-11). Hash-named files are shared by byte-identical emails, so a
    /// file is only ever touched when NO RawMessages row refers to it, re-checked immediately before each move and after
    /// it. Unreferenced files are MOVED to <c>.bin/</c> (same layout, atomic rename), kept for
    /// <see cref="OriginalCleanupOptions.BinRetentionDays"/> days, restored when a row refers to them again, and purged
    /// after that. Every move, restore and purge is written to the audit chain first (a purge is irreversible, so it is
    /// never done without its record) and summarised in the access log. One pass at a time per process.
    /// <para>Ownership: the older <see cref="RawMessageOrphanCollector"/> owns id-named files (<c>1234.eml.gz</c>) and ignores
    /// everything else, including this bin; this collector owns hash-named files and the bin and ignores id-named files.</para>
    /// </summary>
    public sealed class OriginalBin : IOriginalBin
    {
        private static readonly Regex LivePath = new(@"^([0-9a-f]{2})/([0-9a-f]{2})/([0-9a-f]{64})\.eml\.gz$", RegexOptions.Compiled);
        private static readonly Regex Sha = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

        /// <summary>A file written or restored within this window is left alone: an import that reused it may not have committed its row yet.</summary>
        internal static readonly TimeSpan RecentWrite = TimeSpan.FromHours(24);

        private const int Batch = 1000;
        private const long MaxDecompressed = 4L * 1024 * 1024 * 1024;

        private readonly IServiceScopeFactory _scopes;
        private readonly IHostApplicationLifetime _lifetime;
        private readonly IOptions<RawMessageOptions> _raw;
        private readonly IOptions<IntegrityOptions> _integrity;
        private readonly ILogger<OriginalBin> _logger;
        private readonly object _gate = new();
        private Task? _current;
        private int _currentRunId;

        public OriginalBin(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, IOptions<RawMessageOptions> raw,
            IOptions<IntegrityOptions> integrity, ILogger<OriginalBin> logger)
        {
            _scopes = scopes; _lifetime = lifetime; _raw = raw; _integrity = integrity; _logger = logger;
        }

        private OriginalCleanupOptions Opt => _raw.Value.OriginalCleanup;

        // ----- naming and path safety ------------------------------------------------------------------------

        internal static bool TryParseLive(string relative, out string sha)
        {
            sha = string.Empty;
            var m = LivePath.Match(relative ?? string.Empty);
            if (!m.Success) return false;
            sha = m.Groups[3].Value;
            return sha.StartsWith(m.Groups[1].Value, StringComparison.Ordinal) && sha.Substring(2, 2) == m.Groups[2].Value;
        }

        internal static string LiveRel(string sha) => $"{sha[..2]}/{sha.Substring(2, 2)}/{sha}.eml.gz";
        internal static string BinRel(string sha) => $"{OriginalCleanupOptions.BinFolder}/{LiveRel(sha)}";

        /// <summary>False for symlinks (the file, or any directory between it and the base) and for anything outside the base.</summary>
        internal static bool IsSafe(string absolute, string baseFull)
        {
            var full = Path.GetFullPath(absolute);
            if (!full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
            try
            {
                if (File.Exists(full) && new FileInfo(full).LinkTarget != null) return false;
                for (var d = Path.GetDirectoryName(full); !string.IsNullOrEmpty(d) && d.Length > baseFull.Length; d = Path.GetDirectoryName(d))
                    if (Directory.Exists(d) && new DirectoryInfo(d).LinkTarget != null) return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
            return true;
        }

        /// <summary>SHA-256 of the DECOMPRESSED content of a stored file (what the file name promises), or null when unreadable.</summary>
        internal static string? HashFile(string absolute)
        {
            try
            {
                using var fs = File.OpenRead(absolute);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920]; long total = 0; int n;
                while ((n = gz.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > MaxDecompressed) return null;
                    h.AppendData(buf, 0, n);
                }
                return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is EndOfStreamException) { return null; }
        }

        // ----- starting a run --------------------------------------------------------------------------------

        private bool CanRun(out string reason)
        {
            if (!Opt.Enabled) { reason = "The original bin is switched off (RawMessages__OriginalCleanup__Enabled=false)."; return false; }
            if (!OriginalCleanupOptions.IsApplicable(_raw.Value, out var why)) { reason = "The original bin does not apply to this archive: " + why; return false; }
            reason = string.Empty;
            return true;
        }

        public async Task<OriginalCleanupRun> StartAsync(string? startedBy)
        {
            if (!CanRun(out var reason)) throw new InvalidOperationException(reason);

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();

            lock (_gate)
            {
                if (_current is { IsCompleted: false })
                    return db.OriginalCleanupRuns.AsNoTracking().First(r => r.Id == _currentRunId);
            }

            // A run still marked Running with no pass active in this process was interrupted (restart, crash).
            await db.OriginalCleanupRuns.Where(r => r.Status == OriginalCleanupRun.Running)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, OriginalCleanupRun.Failed)
                    .SetProperty(r => r.FinishedAt, DateTime.UtcNow).SetProperty(r => r.Error, "Interrupted (restart or crash)."));

            var run = new OriginalCleanupRun { BinRetentionDays = Opt.BinRetentionDays, StartedBy = startedBy, StartedAt = DateTime.UtcNow };
            db.OriginalCleanupRuns.Add(run);
            await db.SaveChangesAsync();

            lock (_gate)
            {
                if (_current is { IsCompleted: false })
                {
                    // Lost a race with another caller: theirs is the run.
                    db.OriginalCleanupRuns.Remove(run);
                    db.SaveChanges();
                    return db.OriginalCleanupRuns.AsNoTracking().First(r => r.Id == _currentRunId);
                }
                _currentRunId = run.Id;
                _current = Task.Run(() => ExecuteAsync(run.Id, startedBy));
            }
            return run;
        }

        public async Task<OriginalCleanupRun> RunOnceAsync(string? startedBy, CancellationToken cancellationToken = default)
        {
            var run = await StartAsync(startedBy);
            await WaitForCurrentAsync().WaitAsync(cancellationToken);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            return await db.OriginalCleanupRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id, cancellationToken);
        }

        public Task WaitForCurrentAsync() { lock (_gate) return _current ?? Task.CompletedTask; }

        // ----- the pass --------------------------------------------------------------------------------------

        private sealed class Counters { public int Scanned, Referenced, Binned, Restored, Purged, Deferred, Errors; }

        private async Task ExecuteAsync(int runId, string? startedBy)
        {
            var c = new Counters();
            var status = OriginalCleanupRun.Completed;
            string? error = null;
            var ct = _lifetime.ApplicationStopping;
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<MailArchiverDbContext>();
            try
            {
                var paths = sp.GetRequiredService<RawMessagePathResolver>();
                var baseFull = Path.GetFullPath(paths.BasePath);
                if (Directory.Exists(baseFull))
                {
                    var ctx = new Ctx(db, paths, baseFull, sp.GetService<IAuditChain>(), c);
                    await RestorePhaseAsync(ctx, ct);
                    await BinPhaseAsync(ctx, ct);
                    await PurgePhaseAsync(ctx, ct);
                }
            }
            catch (OperationCanceledException) { status = OriginalCleanupRun.Failed; error = "Stopped (application shutting down)."; }
            catch (Exception ex)
            {
                status = OriginalCleanupRun.Failed; error = ex.Message;
                _logger.LogError(ex, "Original bin run {RunId} failed", runId);
            }

            try
            {
                await db.OriginalCleanupRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, status).SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                    .SetProperty(r => r.Scanned, c.Scanned).SetProperty(r => r.Referenced, c.Referenced)
                    .SetProperty(r => r.Binned, c.Binned).SetProperty(r => r.Restored, c.Restored)
                    .SetProperty(r => r.Purged, c.Purged).SetProperty(r => r.Deferred, c.Deferred)
                    .SetProperty(r => r.Errors, c.Errors).SetProperty(r => r.Error, error));
                if (c.Binned + c.Restored + c.Purged + c.Errors > 0)
                    _logger.LogInformation("Original bin run {RunId}: {Binned} binned, {Restored} restored, {Purged} purged, {Deferred} deferred, {Errors} errors", runId, c.Binned, c.Restored, c.Purged, c.Deferred, c.Errors);
                var access = sp.GetService<IAccessLogService>();
                if (access != null && c.Binned + c.Restored + c.Purged + c.Errors > 0)
                    await access.LogAccessAsync(startedBy ?? "system", AccessLogType.Integrity,
                        searchParameters: $"original bin run {runId}: {c.Binned} binned, {c.Restored} restored, {c.Purged} purged, {c.Errors} errors");
            }
            catch (Exception ex) { _logger.LogError(ex, "Could not record the end of original bin run {RunId}", runId); }
        }

        private sealed record Ctx(MailArchiverDbContext Db, RawMessagePathResolver Paths, string BaseFull, IAuditChain? Chain, Counters C);

        private static Task<bool> IsReferencedAsync(MailArchiverDbContext db, string liveRel, CancellationToken ct)
            => db.RawMessages.AsNoTracking().AnyAsync(r => r.StoragePath == liveRel, ct);

        /// <summary>
        /// Writes the audit record BEFORE the action it describes. With the chain switched off there is nothing to write
        /// (true); with it on, a failed append means the action must not happen.
        /// </summary>
        private async Task<bool> RecordAsync(Ctx x, string kind, string sha, object payload, CancellationToken ct)
        {
            if (!_integrity.Value.AuditChainEnabled || x.Chain == null) return true;
            return await x.Chain.AppendAsync(kind, null, sha, payload, ct) != null;
        }

        private string Abs(Ctx x, string rel) => x.Paths.GetAbsolutePath(rel);

        // ----- phase 1: bring back what is referenced again --------------------------------------------------

        private async Task RestorePhaseAsync(Ctx x, CancellationToken ct)
        {
            var after = string.Empty;
            while (true)
            {
                var batch = await x.Db.OriginalBinEntries.AsNoTracking().Where(e => string.Compare(e.StoragePath, after) > 0)
                    .OrderBy(e => e.StoragePath).Take(Batch).ToListAsync(ct);
                if (batch.Count == 0) break;
                after = batch[^1].StoragePath;
                foreach (var e in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await IsReferencedAsync(x.Db, e.StoragePath, ct))
                        await RestoreEntryAsync(x, e, "an email refers to it again", false, ct);
                }
            }
        }

        private async Task<bool> RestoreEntryAsync(Ctx x, OriginalBinEntry e, string reason, bool hold, CancellationToken ct)
        {
            try
            {
                if (!TryParseLive(e.StoragePath, out var sha) || sha != e.Sha256 || e.BinPath != BinRel(sha)) { x.C.Errors++; _logger.LogError("Bin entry {Sha} has inconsistent paths; left alone", e.Sha256); return false; }
                var live = Abs(x, e.StoragePath); var bin = Abs(x, e.BinPath);
                if (!IsSafe(live, x.BaseFull) || !IsSafe(bin, x.BaseFull)) { x.C.Errors++; _logger.LogError("Refusing to restore {Sha}: unsafe path", sha); return false; }

                var liveExists = File.Exists(live); var binExists = File.Exists(bin);
                if (!liveExists && !binExists) { x.C.Errors++; _logger.LogError("Bin entry {Sha}: the file is in neither place", sha); return false; }
                if (liveExists && HashFile(live) != sha) { x.C.Errors++; _logger.LogError("Bin entry {Sha}: a live file with this name does not match its hash; both left alone", sha); return false; }

                if (!await RecordAsync(x, "original.restored", sha, new { storagePath = e.StoragePath, binPath = e.BinPath, reason, liveCopyExisted = liveExists }, ct)) { x.C.Errors++; return false; }

                if (!liveExists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(live)!);
                    File.Move(bin, live, overwrite: false);
                    if (HashFile(live) != sha) { x.C.Errors++; _logger.LogError("Restored file {Sha} does not verify", sha); return false; }
                    if (hold) File.SetLastWriteTimeUtc(live, DateTime.UtcNow);
                }
                else if (binExists)
                {
                    File.Delete(bin); // identical bytes are live again; the bin copy is a duplicate
                }
                x.Db.OriginalBinEntries.Remove(await x.Db.OriginalBinEntries.FirstAsync(b => b.StoragePath == e.StoragePath, ct));
                await x.Db.SaveChangesAsync(ct);
                x.C.Restored++;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                x.C.Errors++; _logger.LogError(ex, "Could not restore {Path} from the bin", e.StoragePath); return false;
            }
        }

        // ----- phase 2: move unreferenced live files into the bin --------------------------------------------

        private async Task BinPhaseAsync(Ctx x, CancellationToken ct)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };
            var pending = new List<string>(Batch);
            var stop = false;

            async Task FlushAsync()
            {
                if (pending.Count == 0 || stop) { pending.Clear(); return; }
                var refs = (await x.Db.RawMessages.AsNoTracking().Where(r => pending.Contains(r.StoragePath)).Select(r => r.StoragePath).ToListAsync(ct)).ToHashSet();
                foreach (var rel in pending)
                {
                    if (refs.Contains(rel)) { x.C.Referenced++; continue; }
                    if (x.C.Binned >= Opt.MaxFilesPerRun) { x.C.Deferred++; stop = true; continue; }
                    await BinOneAsync(x, rel, ct);
                }
                pending.Clear();
            }

            foreach (var file in Directory.EnumerateFiles(x.BaseFull, "*.eml.gz", options))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(x.BaseFull, file).Replace('\\', '/');
                if (rel.StartsWith(OriginalCleanupOptions.BinFolder + "/", StringComparison.Ordinal)) continue;   // the bin is not live
                if (!TryParseLive(rel, out _)) continue;                                                         // id-named or anything else: not ours
                x.C.Scanned++;
                pending.Add(rel);
                if (pending.Count >= Batch) await FlushAsync();
                if (stop) break;
            }
            await FlushAsync();
        }

        private async Task BinOneAsync(Ctx x, string rel, CancellationToken ct)
        {
            try
            {
                if (!TryParseLive(rel, out var sha)) return;
                var live = Abs(x, rel);
                if (!IsSafe(live, x.BaseFull)) { x.C.Errors++; _logger.LogError("Refusing to bin {Path}: unsafe path", rel); return; }
                if (!File.Exists(live)) return;
                if (File.GetLastWriteTimeUtc(live) > DateTime.UtcNow - RecentWrite) { x.C.Deferred++; return; }

                // Re-check right before the move, never trust the batch.
                if (await IsReferencedAsync(x.Db, rel, ct)) { x.C.Referenced++; return; }

                var binRel = BinRel(sha); var bin = Abs(x, binRel);
                if (!IsSafe(bin, x.BaseFull)) { x.C.Errors++; _logger.LogError("Refusing to bin {Sha}: unsafe bin path", sha); return; }
                var size = new FileInfo(live).Length;

                if (File.Exists(bin))
                {
                    // Identical bytes are already in the bin (the same email came and went again): keep that copy, drop this one.
                    if (HashFile(bin) != sha) { x.C.Errors++; _logger.LogError("Bin copy of {Sha} does not match its name; live file left alone", sha); return; }
                    if (!await RecordAsync(x, "original.binned", sha, new { storagePath = rel, binPath = binRel, reason = "an identical copy is already in the bin", retentionDays = Opt.BinRetentionDays }, ct)) { x.C.Errors++; return; }
                    File.Delete(live);
                    await UpsertEntryAsync(x, rel, sha, binRel, size, ct);
                    x.C.Binned++;
                    return;
                }

                if (HashFile(live) != sha) { x.C.Errors++; _logger.LogError("Original {Sha} does not match its own name; not binned", sha); return; }

                Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
                File.Move(live, bin, overwrite: false);

                bool ok = !File.Exists(live) && File.Exists(bin) && new FileInfo(bin).Length == size && HashFile(bin) == sha;
                if (ok && await IsReferencedAsync(x.Db, rel, ct))
                {
                    // A row appeared while we moved it: undo, it is live again.
                    ok = false; x.C.Referenced++;
                    UndoMove(bin, live);
                    return;
                }
                if (!ok) { x.C.Errors++; UndoMove(bin, live); _logger.LogError("Moving {Sha} to the bin did not verify; undone", sha); return; }

                if (!await RecordAsync(x, "original.binned", sha, new { storagePath = rel, binPath = binRel, reason = "no email refers to it", retentionDays = Opt.BinRetentionDays }, ct))
                {
                    UndoMove(bin, live); x.C.Errors++; return;
                }
                await UpsertEntryAsync(x, rel, sha, binRel, size, ct);
                x.C.Binned++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                x.C.Errors++; _logger.LogError(ex, "Could not bin {Path}", rel);
            }
        }

        private static void UndoMove(string from, string to)
        {
            try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to, overwrite: false); } catch { /* best effort; the entry-less bin file is still on disk and recorded by the next run's error */ }
        }

        private static async Task UpsertEntryAsync(Ctx x, string rel, string sha, string binRel, long size, CancellationToken ct)
        {
            var existing = await x.Db.OriginalBinEntries.FirstOrDefaultAsync(e => e.StoragePath == rel, ct);
            if (existing == null)
                x.Db.OriginalBinEntries.Add(new OriginalBinEntry { StoragePath = rel, Sha256 = sha, BinPath = binRel, BinnedAt = AuditChainHash.TruncateToMicroseconds(DateTime.UtcNow), Size = size });
            await x.Db.SaveChangesAsync(ct);
        }

        // ----- phase 3: purge what has been in the bin long enough -------------------------------------------

        private async Task PurgePhaseAsync(Ctx x, CancellationToken ct)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(Opt.BinRetentionDays);
            var due = await x.Db.OriginalBinEntries.AsNoTracking().Where(e => e.BinnedAt <= cutoff)
                .OrderBy(e => e.BinnedAt).ThenBy(e => e.StoragePath).Take(Opt.MaxFilesPerRun).ToListAsync(ct);
            foreach (var e in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!TryParseLive(e.StoragePath, out var sha) || sha != e.Sha256 || e.BinPath != BinRel(sha)) { x.C.Errors++; _logger.LogError("Bin entry {Sha} has inconsistent paths; not purged", e.Sha256); continue; }

                    // Referenced again after all: restore instead of purging.
                    if (await IsReferencedAsync(x.Db, e.StoragePath, ct)) { await RestoreEntryAsync(x, e, "an email refers to it again (found at purge time)", false, ct); continue; }

                    var bin = Abs(x, e.BinPath);
                    if (!IsSafe(bin, x.BaseFull)) { x.C.Errors++; _logger.LogError("Refusing to purge {Sha}: unsafe path", sha); continue; }

                    // Irreversible: the record comes first. No record, no purge.
                    if (!await RecordAsync(x, "original.purged", sha, new { storagePath = e.StoragePath, binPath = e.BinPath, reason = "retention period over", retentionDays = Opt.BinRetentionDays, binnedAt = e.BinnedAt.ToString("O") }, ct)) { x.C.Errors++; continue; }

                    try { if (File.Exists(bin)) File.Delete(bin); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        x.C.Errors++; _logger.LogError(ex, "Could not delete {Path} from the bin", e.BinPath);
                        await RecordAsync(x, "original.purge-failed", sha, new { storagePath = e.StoragePath, binPath = e.BinPath }, ct);
                        continue;
                    }
                    x.Db.OriginalBinEntries.Remove(await x.Db.OriginalBinEntries.FirstAsync(b => b.StoragePath == e.StoragePath, ct));
                    await x.Db.SaveChangesAsync(ct);
                    x.C.Purged++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    x.C.Errors++; _logger.LogError(ex, "Could not purge {Path}", e.StoragePath);
                }
            }
        }

        // ----- read side and the admin actions ---------------------------------------------------------------

        public async Task<OriginalBinStatus> GetStatusAsync()
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            var applicable = OriginalCleanupOptions.IsApplicable(_raw.Value, out var why);
            var days = TimeSpan.FromDays(Opt.BinRetentionDays);
            var inBin = await db.OriginalBinEntries.CountAsync();
            var bytes = inBin == 0 ? 0 : await db.OriginalBinEntries.SumAsync(e => e.Size);
            DateTime? earliest = inBin == 0 ? null : (await db.OriginalBinEntries.MinAsync(e => e.BinnedAt)) + days;
            var last = await db.OriginalCleanupRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstOrDefaultAsync();
            return new OriginalBinStatus(Opt.Enabled, applicable, applicable ? null : why, Opt.BinRetentionDays, Opt.IntervalHours, Opt.MaxFilesPerRun,
                inBin, bytes, earliest,
                await db.OriginalCleanupRuns.SumAsync(r => (long)r.Binned), await db.OriginalCleanupRuns.SumAsync(r => (long)r.Restored),
                await db.OriginalCleanupRuns.SumAsync(r => (long)r.Purged), last);
        }

        public async Task<IReadOnlyList<OriginalBinItem>> ListBinAsync(int limit)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            var days = TimeSpan.FromDays(Opt.BinRetentionDays);
            var rows = await db.OriginalBinEntries.AsNoTracking().OrderBy(e => e.BinnedAt).Take(Math.Clamp(limit, 1, 1000)).ToListAsync();
            return rows.Select(e => new OriginalBinItem(e.Sha256, e.BinnedAt, e.BinnedAt + days, e.Size)).ToList();
        }

        public async Task<OriginalRestoreResult> RestoreAsync(string sha256, string? actor)
        {
            if (!Sha.IsMatch(sha256 ?? string.Empty)) return new OriginalRestoreResult(false, "Expected a lowercase hex SHA-256 (64 characters).");
            if (!OriginalCleanupOptions.IsApplicable(_raw.Value, out var why)) return new OriginalRestoreResult(false, "The original bin does not apply to this archive: " + why);

            lock (_gate) { if (_current is { IsCompleted: false }) return new OriginalRestoreResult(false, "A bin run is in progress; try again when it has finished."); }

            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<MailArchiverDbContext>();
            var paths = sp.GetRequiredService<RawMessagePathResolver>();
            var baseFull = Path.GetFullPath(paths.BasePath);
            var entry = await db.OriginalBinEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Sha256 == sha256);
            if (entry == null) return new OriginalRestoreResult(false, "That original is not in the bin.");

            var c = new Counters();
            var ok = await RestoreEntryAsync(new Ctx(db, paths, baseFull, sp.GetService<IAuditChain>(), c), entry, $"restored by {actor ?? "an administrator"}", hold: true, CancellationToken.None);
            if (ok)
            {
                var access = sp.GetService<IAccessLogService>();
                if (access != null) await access.LogAccessAsync(actor ?? "unknown", AccessLogType.Integrity, searchParameters: $"original restored from the bin: {sha256[..12]}");
            }
            return ok
                ? new OriginalRestoreResult(true, "Restored. It stays live for at least a day so it can be re-imported; if no email refers to it after that, it goes back to the bin with a fresh retention period.")
                : new OriginalRestoreResult(false, "It could not be restored (see the server log).");
        }

        public async Task<ErasureStatus> DescribeEmailAsync(int emailId)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            if (await db.ArchivedEmails.AsNoTracking().AnyAsync(e => e.Id == emailId))
                return new ErasureStatus(emailId, "EmailStillExists", "This email has not been deleted.", null, 0, null, null);

            var sha = await db.AuditChain.AsNoTracking().Where(c => c.Kind == "email.deleted" && c.EmailId == emailId)
                .OrderByDescending(c => c.Seq).Select(c => c.Sha256).FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(sha) || !Sha.IsMatch(sha))
                return new ErasureStatus(emailId, "NoDeletionRecord", "No deletion record with an original hash exists for this email id.", null, 0, null, null);

            var live = LiveRel(sha);
            var refs = await db.RawMessages.AsNoTracking().CountAsync(r => r.StoragePath == live);
            if (refs > 0)
                return new ErasureStatus(emailId, "StillReferenced", $"{refs} other email(s) share this original; it stays until none of them remain.", sha, refs, null, null);

            var binned = await db.OriginalBinEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Sha256 == sha);
            if (binned != null)
                return new ErasureStatus(emailId, "InBin", "The original is in the bin and will be deleted permanently on the date shown.", sha, 0, binned.BinnedAt, binned.BinnedAt + TimeSpan.FromDays(Opt.BinRetentionDays));

            using var s2 = _scopes.CreateScope();
            var paths = s2.ServiceProvider.GetRequiredService<RawMessagePathResolver>();
            if (File.Exists(paths.GetAbsolutePath(live)))
                return new ErasureStatus(emailId, "LiveUnreferenced", "The original is still in place and no email refers to it; the next bin run moves it to the bin.", sha, 0, null, null);

            var purged = await db.AuditChain.AsNoTracking().AnyAsync(c => c.Kind == "original.purged" && c.Sha256 == sha);
            return purged
                ? new ErasureStatus(emailId, "Purged", "The original was deleted permanently from the bin.", sha, 0, null, null)
                : new ErasureStatus(emailId, "NoFile", "No file for this original exists (it may never have been stored on the filesystem).", sha, 0, null, null);
        }
    }

    /// <summary>Runs the bin every <c>RawMessages:OriginalCleanup:IntervalHours</c> hours while it is on and applies to this archive.</summary>
    public sealed class OriginalBinService : BackgroundService
    {
        private readonly IOriginalBin _bin;
        private readonly IOptions<RawMessageOptions> _raw;
        private readonly ILogger<OriginalBinService> _logger;

        public OriginalBinService(IOriginalBin bin, IOptions<RawMessageOptions> raw, ILogger<OriginalBinService> logger)
        { _bin = bin; _raw = raw; _logger = logger; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var o = _raw.Value.OriginalCleanup;
            if (!o.Enabled) { _logger.LogInformation("Original bin is switched off"); return; }
            if (!OriginalCleanupOptions.IsApplicable(_raw.Value, out var why)) { _logger.LogInformation("Original bin is on but does not apply to this archive: {Reason}", why); return; }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, o.StartupDelayMinutes)), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    try { await _bin.RunOnceAsync("schedule", stoppingToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _logger.LogError(ex, "Scheduled original bin run failed"); }
                    await Task.Delay(TimeSpan.FromHours(Math.Max(1, o.IntervalHours)), stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }
}
