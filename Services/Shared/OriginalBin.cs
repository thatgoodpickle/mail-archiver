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
        int InBin, long InBinBytes, DateTime? EarliestPurgeAt, long TotalBinned, long TotalRestored, long TotalPurged, OriginalCleanupRun? LastRun,
        ArchiveIdentityCheck? Identity = null);

    public sealed record OriginalBinItem(string Sha256, DateTime BinnedAt, DateTime PurgeAfter, long Size);

    /// <summary>A live original that no email refers to and that has NO delete request: listed for an administrator, never moved by a run.</summary>
    public sealed record UnclaimedOriginal(string Sha256, long Size, DateTime Modified);

    public sealed record OriginalRestoreResult(bool Restored, string Message);

    public sealed record ErasureStatus(int EmailId, string State, string Message, string? Sha256, int ReferencedByRows, DateTime? BinnedAt, DateTime? PurgeAfter);

    public interface IOriginalBin
    {
        /// <summary>
        /// Starts one pass in the background and returns its run record; returns the running one when a pass is
        /// already active. Throws InvalidOperationException (with the reason) when the bin is switched off or does
        /// not apply to this archive. Accepts no path: it only ever works on hash-named files under the originals folder.
        /// </summary>
        Task<OriginalCleanupRun> StartAsync(string? startedBy, bool overrideBreaker = false);

        /// <summary>Runs one pass and waits for it (scheduler, tests). Same rules as <see cref="StartAsync"/>.</summary>
        Task<OriginalCleanupRun> RunOnceAsync(string? startedBy, CancellationToken cancellationToken = default, bool overrideBreaker = false);

        /// <summary>Whether the database and the originals folder belong together (the Trash does not run otherwise).</summary>
        Task<ArchiveIdentityCheck> GetIdentityAsync();

        /// <summary>The explicit admin step: declares this database the owner of the originals folder. Audited.</summary>
        Task<ArchiveIdentityCheck> AdoptAsync(string? actor);

        /// <summary>Live originals that no email refers to and that have no delete request (read-only; scans the folder).</summary>
        Task<IReadOnlyList<UnclaimedOriginal>> ListUnclaimedAsync(int limit);

        /// <summary>Explicitly moves ONE unclaimed original (by SHA-256) to the Trash. Audited. Refuses a referenced file.</summary>
        Task<OriginalRestoreResult> MoveToTrashAsync(string sha256, string? actor);

        Task WaitForCurrentAsync();
        Task<OriginalBinStatus> GetStatusAsync();
        Task<IReadOnlyList<OriginalBinItem>> ListBinAsync(int limit);

        /// <summary>Moves one binned original (by SHA-256, never by path) back to its live place. It stays live for at least a day so it can be re-imported.</summary>
        Task<OriginalRestoreResult> RestoreAsync(string sha256, string? actor);

        /// <summary>Read-only: where the original of one ALREADY DELETED email is, and when it can be purged. Changes nothing.</summary>
        Task<ErasureStatus> DescribeEmailAsync(int emailId);
    }

    /// <summary>
    /// Garbage collector with a recycle bin for hash-named originals (review F-11, second review N-01). Hash-named files are
    /// shared by byte-identical emails, so a file is only ever touched when (1) a delete request for its hash is in the audit
    /// chain (an 'email.deleted' entry), (2) NO RawMessages row refers to it (re-checked immediately before each move and after
    /// it), (3) the database and the originals folder belong together (archive identity), and (4) the run passes the circuit
    /// breaker. Files that fail (1) are only listed. Collected files are MOVED to <c>.bin/</c> (same layout, atomic rename), kept for
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
        private readonly IOptionsMonitor<RawMessageOptions> _raw;
        private readonly IOptionsMonitor<IntegrityOptions> _integrity;
        private readonly ILogger<OriginalBin> _logger;
        private readonly object _gate = new();
        /// <summary>One operation at a time across scheduled runs, admin runs, restores and manual moves (second review N-08).</summary>
        private readonly SemaphoreSlim _opGate = new(1, 1);
        private Task? _current;
        private int _currentRunId;

        public OriginalBin(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, IOptionsMonitor<RawMessageOptions> raw,
            IOptionsMonitor<IntegrityOptions> integrity, ILogger<OriginalBin> logger)
        {
            _scopes = scopes; _lifetime = lifetime; _raw = raw; _integrity = integrity; _logger = logger;
        }

        private OriginalCleanupOptions Opt => _raw.CurrentValue.OriginalCleanup;

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
            if (!Opt.Enabled) { reason = "The Trash is switched off (RawMessages__OriginalCleanup__Enabled=false; the name still says Bin for compatibility)."; return false; }
            if (!OriginalCleanupOptions.IsApplicable(_raw.CurrentValue, out var why)) { reason = "The Trash does not apply to this archive: " + why; return false; }
            reason = string.Empty;
            return true;
        }

        public async Task<OriginalCleanupRun> StartAsync(string? startedBy, bool overrideBreaker = false)
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
                _current = Task.Run(() => ExecuteAsync(run.Id, startedBy, overrideBreaker));
            }
            return run;
        }

        public async Task<OriginalCleanupRun> RunOnceAsync(string? startedBy, CancellationToken cancellationToken = default, bool overrideBreaker = false)
        {
            var run = await StartAsync(startedBy, overrideBreaker);
            await WaitForCurrentAsync().WaitAsync(cancellationToken);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            return await db.OriginalCleanupRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id, cancellationToken);
        }

        public Task WaitForCurrentAsync() { lock (_gate) return _current ?? Task.CompletedTask; }

        // ----- the pass --------------------------------------------------------------------------------------

        private sealed class Counters { public int Scanned, Referenced, Binned, Restored, Purged, Deferred, Errors, Unclaimed, Adopted, TempCleaned, TextsCleaned; public string? Alarm; }

        private async Task ExecuteAsync(int runId, string? startedBy, bool overrideBreaker)
        {
            var c = new Counters();
            var status = OriginalCleanupRun.Completed;
            string? error = null;
            var ct = _lifetime.ApplicationStopping;
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<MailArchiverDbContext>();
            var gateHeld = false;
            try
            {
                await _opGate.WaitAsync(ct); gateHeld = true;
                var paths = sp.GetRequiredService<RawMessagePathResolver>();
                var baseFull = Path.GetFullPath(paths.BasePath);
                if (Directory.Exists(baseFull))
                {
                    // Second review N-01(c): the database must be the one these originals belong to, or nothing is touched.
                    var identity = await sp.GetRequiredService<IArchiveIdentityService>().CheckAsync(baseFull, ct);
                    if (!identity.Ok)
                    {
                        c.Alarm = identity.Message;
                        _logger.LogError("Original Trash refused to run: {Message}", identity.Message);
                    }
                    else
                    {
                        var ctx = new Ctx(db, paths, baseFull, sp.GetService<IAuditChain>(), c);
                        await ReconcilePhaseAsync(ctx, ct);
                        await RestorePhaseAsync(ctx, ct);
                        await BinPhaseAsync(ctx, overrideBreaker, ct);
                        await PurgePhaseAsync(ctx, ct);
                        CleanTempFiles(ctx);
                        await CleanAttachmentTextsAsync(ctx, ct);
                    }
                    if (c.Alarm != null) status = OriginalCleanupRun.Refused;
                }
            }
            catch (OperationCanceledException) { status = OriginalCleanupRun.Failed; error = "Stopped (application shutting down)."; }
            catch (Exception ex)
            {
                status = OriginalCleanupRun.Failed; error = ex.Message;
                _logger.LogError(ex, "Original bin run {RunId} failed", runId);
            }
            finally { if (gateHeld) _opGate.Release(); }

            try
            {
                await db.OriginalCleanupRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, status).SetProperty(r => r.FinishedAt, DateTime.UtcNow)
                    .SetProperty(r => r.Scanned, c.Scanned).SetProperty(r => r.Referenced, c.Referenced)
                    .SetProperty(r => r.Binned, c.Binned).SetProperty(r => r.Restored, c.Restored)
                    .SetProperty(r => r.Purged, c.Purged).SetProperty(r => r.Deferred, c.Deferred)
                    .SetProperty(r => r.Errors, c.Errors).SetProperty(r => r.Error, error)
                    .SetProperty(r => r.Unclaimed, c.Unclaimed).SetProperty(r => r.Alarm, c.Alarm));
                if (c.Binned + c.Restored + c.Purged + c.Errors + c.Adopted > 0 || c.Alarm != null)
                    _logger.LogInformation("Original bin run {RunId}: {Binned} binned, {Restored} restored, {Purged} purged, {Deferred} deferred, {Unclaimed} unclaimed, {Adopted} adopted, {Errors} errors{Alarm}",
                        runId, c.Binned, c.Restored, c.Purged, c.Deferred, c.Unclaimed, c.Adopted, c.Errors, c.Alarm == null ? "" : " (REFUSED: " + c.Alarm + ")");
                var access = sp.GetService<IAccessLogService>();
                if (access != null && c.Binned + c.Restored + c.Purged + c.Errors + c.Adopted > 0 || (access != null && c.Alarm != null))
                    await access!.LogAccessAsync(startedBy ?? "system", AccessLogType.Integrity,
                        searchParameters: c.Alarm != null
                            ? $"Trash run {runId} refused: {c.Alarm}"
                            : $"Trash run {runId}: {c.Binned} moved to the Trash, {c.Restored} restored, {c.Purged} erased for good, {c.Errors} errors");
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
            if (!_integrity.CurrentValue.AuditChainEnabled || x.Chain == null) return true;
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
                    // N-08: mark it "in use" BEFORE it becomes visible at the live path (a rename keeps the time), so a run that
                    // starts right after cannot see an old unreferenced file and bin it again.
                    if (hold) { try { File.SetLastWriteTimeUtc(bin, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { } }
                    File.Move(bin, live, overwrite: false);
                    if (HashFile(live) != sha) { x.C.Errors++; _logger.LogError("Restored file {Sha} does not verify", sha); return false; }
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

        // ----- phase 0: reconcile the bin folder with its entries (second review N-03) ----------------------------

        /// <summary>
        /// A crash between the steps of a move can leave an entry without its file in the Trash (the file is still live) or a file
        /// in <c>.bin/</c> without an entry. This pass tidies both before anything else: stale entries are cleared, and unlisted
        /// files found in the Trash are adopted (verified, recorded, and then restored or purged by the normal phases).
        /// </summary>
        private async Task ReconcilePhaseAsync(Ctx x, CancellationToken ct)
        {
            // 1. entries whose Trash file is gone while the live file is there: the move never happened.
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
                    if (!TryParseLive(e.StoragePath, out var sha) || sha != e.Sha256 || e.BinPath != BinRel(sha)) continue;
                    var live = Abs(x, e.StoragePath); var bin = Abs(x, e.BinPath);
                    if (!IsSafe(live, x.BaseFull) || !IsSafe(bin, x.BaseFull)) continue;
                    if (File.Exists(bin) || !File.Exists(live)) continue;
                    if (!await RecordAsync(x, "original.restored", sha, new { storagePath = e.StoragePath, binPath = e.BinPath, reason = "the move to the Trash never completed (found at the next run)", liveCopyExisted = true }, ct)) { x.C.Errors++; continue; }
                    await x.Db.OriginalBinEntries.Where(b => b.StoragePath == e.StoragePath).ExecuteDeleteAsync(ct);
                }
            }

            // 2. files in the Trash that no entry lists.
            var binRoot = Path.Combine(x.BaseFull, OriginalCleanupOptions.BinFolder);
            if (!Directory.Exists(binRoot) || !IsSafe(binRoot + Path.DirectorySeparatorChar + "x", x.BaseFull)) return;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            foreach (var file in Directory.EnumerateFiles(binRoot, "*.eml.gz", options))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(binRoot, file).Replace('\\', '/');
                if (!TryParseLive(rel, out var sha)) continue;
                var storagePath = LiveRel(sha);
                if (await x.Db.OriginalBinEntries.AsNoTracking().AnyAsync(e => e.StoragePath == storagePath, ct)) continue;
                if (!IsSafe(file, x.BaseFull)) { x.C.Errors++; continue; }
                if (HashFile(file) != sha) { x.C.Errors++; _logger.LogError("A file in the Trash does not match its own name and has no entry; left alone: {Sha}", sha); continue; }
                if (!await RecordAsync(x, "original.binned", sha, new { storagePath, binPath = BinRel(sha), reason = "found in the Trash without a record; adopted", retentionDays = Opt.BinRetentionDays }, ct)) { x.C.Errors++; continue; }
                x.Db.OriginalBinEntries.Add(new OriginalBinEntry { StoragePath = storagePath, Sha256 = sha, BinPath = BinRel(sha), BinnedAt = AuditChainHash.TruncateToMicroseconds(DateTime.UtcNow), Size = new FileInfo(file).Length });
                await x.Db.SaveChangesAsync(ct);
                x.C.Adopted++;
            }
        }

        // ----- phase 2: move unreferenced live files into the bin --------------------------------------------

        private static string ShaOf(string liveRel) => liveRel.Substring(liveRel.LastIndexOf('/') + 1, 64);

        /// <summary>All live hash-named files below the originals folder (relative paths); the bin and anything not ours are skipped.</summary>
        private static List<string> ListLive(string baseFull, CancellationToken ct)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            var live = new List<string>();
            foreach (var file in Directory.EnumerateFiles(baseFull, "*.eml.gz", options))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(baseFull, file).Replace('\\', '/');
                if (rel.StartsWith(OriginalCleanupOptions.BinFolder + "/", StringComparison.Ordinal)) continue;   // the bin is not live
                if (!TryParseLive(rel, out _)) continue;                                                         // id-named or anything else: not ours
                live.Add(rel);
            }
            return live;
        }

        /// <summary>The live files no row refers to, split into those with a delete request in the chain and those without.</summary>
        private async Task<(List<string> Eligible, List<string> Unclaimed, int Referenced)> ClassifyAsync(MailArchiverDbContext db, List<string> live, CancellationToken ct)
        {
            var unreferenced = new List<string>(); var referenced = 0;
            for (var i = 0; i < live.Count; i += Batch)
            {
                var chunk = live.Skip(i).Take(Batch).ToList();
                var refs = (await db.RawMessages.AsNoTracking().Where(r => chunk.Contains(r.StoragePath)).Select(r => r.StoragePath).ToListAsync(ct)).ToHashSet();
                foreach (var rel in chunk) { if (refs.Contains(rel)) referenced++; else unreferenced.Add(rel); }
            }
            var deleted = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < unreferenced.Count; i += Batch)
            {
                var shas = unreferenced.Skip(i).Take(Batch).Select(ShaOf).ToList();
                var hit = await db.AuditChain.AsNoTracking().Where(c => c.Kind == "email.deleted" && c.Sha256 != null && shas.Contains(c.Sha256)).Select(c => c.Sha256!).Distinct().ToListAsync(ct);
                foreach (var h in hit) deleted.Add(h);
            }
            var eligible = unreferenced.Where(r => deleted.Contains(ShaOf(r))).ToList();
            var unclaimed = unreferenced.Where(r => !deleted.Contains(ShaOf(r))).ToList();
            return (eligible, unclaimed, referenced);
        }

        private async Task BinPhaseAsync(Ctx x, bool overrideBreaker, CancellationToken ct)
        {
            var live = ListLive(x.BaseFull, ct);
            x.C.Scanned = live.Count;
            if (live.Count == 0) return;

            // Second review N-01(d): an empty database next to a full folder is never a deletion.
            if (!await x.Db.RawMessages.AsNoTracking().AnyAsync(ct))
            {
                x.C.Alarm = $"The database holds no stored originals but the originals folder holds {live.Count}. This looks like an empty or wrong database, so nothing was moved.";
                _logger.LogError("Original Trash refused: {Alarm}", x.C.Alarm);
                return;
            }

            var (eligible, unclaimed, referenced) = await ClassifyAsync(x.Db, live, ct);
            x.C.Referenced = referenced; x.C.Unclaimed = unclaimed.Count;

            // Circuit breaker: moving more than a small share of all originals at once is not what a deletion looks like.
            var limit = Math.Max(Opt.BreakerMinFiles, (int)Math.Ceiling(live.Count * Opt.BreakerPercent / 100.0));
            if (!overrideBreaker && eligible.Count > limit)
            {
                x.C.Alarm = $"{eligible.Count} of {live.Count} originals have a recorded delete request and no email, which is more than the safety limit of {limit} ({Opt.BreakerPercent}% of the originals, at least {Opt.BreakerMinFiles}). Nothing was moved. If these deletions are expected (for example a removed account), run the Trash again with \"Run anyway\".";
                _logger.LogError("Original Trash refused: {Alarm}", x.C.Alarm);
                return;
            }

            foreach (var rel in eligible)
            {
                ct.ThrowIfCancellationRequested();
                if (x.C.Binned >= Opt.MaxFilesPerRun) { x.C.Deferred++; continue; }
                await BinOneAsync(x, rel, "its email was deleted (a delete request is recorded) and no email refers to it", ct);
            }
        }

        private async Task BinOneAsync(Ctx x, string rel, string reason, CancellationToken ct)
        {
            var entryAdded = false; string? live = null, bin = null;
            try
            {
                if (!TryParseLive(rel, out var sha)) return;
                live = Abs(x, rel);
                if (!IsSafe(live, x.BaseFull)) { x.C.Errors++; _logger.LogError("Refusing to bin {Path}: unsafe path", rel); return; }
                if (!File.Exists(live)) return;
                if (File.GetLastWriteTimeUtc(live) > DateTime.UtcNow - RecentWrite) { x.C.Deferred++; return; }

                // Re-check right before the move, never trust the batch.
                if (await IsReferencedAsync(x.Db, rel, ct)) { x.C.Referenced++; return; }

                var binRel = BinRel(sha); bin = Abs(x, binRel);
                if (!IsSafe(bin, x.BaseFull)) { x.C.Errors++; _logger.LogError("Refusing to bin {Sha}: unsafe bin path", sha); return; }
                var size = new FileInfo(live).Length;

                if (File.Exists(bin))
                {
                    // Identical bytes are already in the bin (the same email came and went again): keep that copy, drop this one.
                    if (HashFile(bin) != sha) { x.C.Errors++; _logger.LogError("Bin copy of {Sha} does not match its name; live file left alone", sha); return; }
                    if (!await RecordAsync(x, "original.binned", sha, new { storagePath = rel, binPath = binRel, reason = "an identical copy is already in the bin", retentionDays = Opt.BinRetentionDays }, ct)) { x.C.Errors++; return; }
                    await UpsertEntryAsync(x, rel, sha, binRel, size, ct);
                    File.Delete(live);
                    x.C.Binned++;
                    return;
                }

                if (HashFile(live) != sha) { x.C.Errors++; _logger.LogError("Original {Sha} does not match its own name; not binned", sha); return; }

                // N-03: the record and the entry come BEFORE the move, so a crash can never leave an unlisted file in the Trash. If the
                // process dies after them, the next run's reconcile pass sees an entry whose file never arrived and clears it.
                if (!await RecordAsync(x, "original.binned", sha, new { storagePath = rel, binPath = binRel, reason, retentionDays = Opt.BinRetentionDays }, ct)) { x.C.Errors++; return; }
                await UpsertEntryAsync(x, rel, sha, binRel, size, ct); entryAdded = true;

                // N-06: look again at the very last moment (an import that reused this file touches it and then commits its row).
                if (File.GetLastWriteTimeUtc(live) > DateTime.UtcNow - RecentWrite || await IsReferencedAsync(x.Db, rel, ct))
                { await AbandonMoveAsync(x, rel, sha, binRel, "an email started using it again", ct); entryAdded = false; x.C.Referenced++; return; }

                Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
                File.Move(live, bin, overwrite: false);

                var ok = !File.Exists(live) && File.Exists(bin) && new FileInfo(bin).Length == size && HashFile(bin) == sha;
                if (ok && (await IsReferencedAsync(x.Db, rel, ct)))
                {
                    // A row appeared while we moved it: undo, it is live again.
                    UndoMove(bin, live);
                    await AbandonMoveAsync(x, rel, sha, binRel, "an email started using it while it was being moved", ct); entryAdded = false; x.C.Referenced++;
                    return;
                }
                if (!ok) { UndoMove(bin, live); await AbandonMoveAsync(x, rel, sha, binRel, "the move did not verify", ct); entryAdded = false; x.C.Errors++; _logger.LogError("Moving {Sha} to the bin did not verify; undone", sha); return; }
                x.C.Binned++;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                x.C.Errors++; _logger.LogError(ex, "Could not bin {Path}", rel);
                if (entryAdded && live != null && bin != null)
                {
                    UndoMove(bin, live);
                    try { if (TryParseLive(rel, out var s2)) await AbandonMoveAsync(x, rel, s2, BinRel(s2), "the move failed", ct); } catch { /* the next run's reconcile pass clears it */ }
                }
            }
        }

        /// <summary>Takes back a started move: the entry goes, and the chain says the original is live again (so 'binned' is not left standing).</summary>
        private async Task AbandonMoveAsync(Ctx x, string rel, string sha, string binRel, string why, CancellationToken ct)
        {
            await x.Db.OriginalBinEntries.Where(b => b.StoragePath == rel).ExecuteDeleteAsync(ct);
            await RecordAsync(x, "original.restored", sha, new { storagePath = rel, binPath = binRel, reason = "the move to the Trash was undone: " + why, liveCopyExisted = true }, ct);
        }

        private static void UndoMove(string from, string to)
        {
            try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to, overwrite: false); } catch { /* best effort; the next run's reconcile pass adopts a file left in the Trash */ }
        }

        private static async Task UpsertEntryAsync(Ctx x, string rel, string sha, string binRel, long size, CancellationToken ct)
        {
            var existing = await x.Db.OriginalBinEntries.FirstOrDefaultAsync(e => e.StoragePath == rel, ct);
            if (existing == null)
                x.Db.OriginalBinEntries.Add(new OriginalBinEntry { StoragePath = rel, Sha256 = sha, BinPath = binRel, BinnedAt = AuditChainHash.TruncateToMicroseconds(DateTime.UtcNow), Size = size });
            await x.Db.SaveChangesAsync(ct);
        }

        // ----- housekeeping ----------------------------------------------------------------------------------

        private static readonly Regex TempName = new(@"^[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{64}\.eml\.gz\.[0-9a-f]{32}\.tmp$", RegexOptions.Compiled);

        /// <summary>Second review N-15: half-written files of a crashed hash-named write, older than a day, are deleted (they hold full message content).</summary>
        private void CleanTempFiles(Ctx x)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            foreach (var file in Directory.EnumerateFiles(x.BaseFull, "*.tmp", options))
            {
                var rel = Path.GetRelativePath(x.BaseFull, file).Replace('\\', '/');
                if (!TempName.IsMatch(rel) || !IsSafe(file, x.BaseFull)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > DateTime.UtcNow - RecentWrite) continue;
                    File.Delete(file); x.C.TempCleaned++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { x.C.Errors++; _logger.LogWarning(ex, "Could not delete the leftover {Path}", rel); }
            }
            if (x.C.TempCleaned > 0) _logger.LogInformation("Original Trash: removed {Count} leftover temporary files of interrupted writes", x.C.TempCleaned);
        }

        /// <summary>
        /// Second review N-07: text extracted from an attachment (and its OCR text) is keyed by the attachment's hash. When no
        /// attachment refers to a hash any more, its text is deleted (after a day, so one being ingested is never caught).
        /// </summary>
        private async Task CleanAttachmentTextsAsync(Ctx x, CancellationToken ct)
        {
            try
            {
                x.C.TextsCleaned = await x.Db.Database.ExecuteSqlRawAsync(@"
                    DELETE FROM mail_archiver.""AttachmentTexts"" t
                    WHERE t.""ExtractedAt"" < (now() at time zone 'utc') - interval '1 day'
                      AND NOT EXISTS (SELECT 1 FROM mail_archiver.""EmailAttachments"" a WHERE a.""Sha256"" = t.""Sha256"")
                      AND NOT EXISTS (SELECT 1 FROM mail_archiver.""AttachmentContents"" c WHERE c.""Hash"" = t.""Sha256"")", ct);
                if (x.C.TextsCleaned > 0) _logger.LogInformation("Original Trash: removed the extracted text of {Count} attachments no email has any more", x.C.TextsCleaned);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Could not clean up attachment texts"); }
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
            var applicable = OriginalCleanupOptions.IsApplicable(_raw.CurrentValue, out var why);
            var days = TimeSpan.FromDays(Opt.BinRetentionDays);
            var inBin = await db.OriginalBinEntries.CountAsync();
            var bytes = inBin == 0 ? 0 : await db.OriginalBinEntries.SumAsync(e => e.Size);
            DateTime? earliest = inBin == 0 ? null : (await db.OriginalBinEntries.MinAsync(e => e.BinnedAt)) + days;
            var last = await db.OriginalCleanupRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstOrDefaultAsync();
            return new OriginalBinStatus(Opt.Enabled, applicable, applicable ? null : why, Opt.BinRetentionDays, Opt.IntervalHours, Opt.MaxFilesPerRun,
                inBin, bytes, earliest,
                await db.OriginalCleanupRuns.SumAsync(r => (long)r.Binned), await db.OriginalCleanupRuns.SumAsync(r => (long)r.Restored),
                await db.OriginalCleanupRuns.SumAsync(r => (long)r.Purged), last,
                applicable ? await IdentityOrNullAsync(scope.ServiceProvider) : null);
        }

        private static async Task<ArchiveIdentityCheck?> IdentityOrNullAsync(IServiceProvider sp)
        {
            try
            {
                var baseFull = Path.GetFullPath(sp.GetRequiredService<RawMessagePathResolver>().BasePath);
                if (!Directory.Exists(baseFull)) return null;
                return await sp.GetRequiredService<IArchiveIdentityService>().CheckAsync(baseFull);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
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
            if (!OriginalCleanupOptions.IsApplicable(_raw.CurrentValue, out var why)) return new OriginalRestoreResult(false, "The Trash does not apply to this archive: " + why);

            // N-08: one operation at a time; a restore never overlaps a run.
            if (!await _opGate.WaitAsync(TimeSpan.FromSeconds(2))) return new OriginalRestoreResult(false, "A Trash run is in progress; try again when it has finished.");
            try
            {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<MailArchiverDbContext>();
            var paths = sp.GetRequiredService<RawMessagePathResolver>();
            var baseFull = Path.GetFullPath(paths.BasePath);
            var entry = await db.OriginalBinEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Sha256 == sha256);
            if (entry == null) return new OriginalRestoreResult(false, "That original is not in the Trash.");

            var c = new Counters();
            var ok = await RestoreEntryAsync(new Ctx(db, paths, baseFull, sp.GetService<IAuditChain>(), c), entry, $"restored by {actor ?? "an administrator"}", hold: true, CancellationToken.None);
            if (ok)
            {
                var access = sp.GetService<IAccessLogService>();
                if (access != null) await access.LogAccessAsync(actor ?? "unknown", AccessLogType.Integrity, searchParameters: $"original restored from the Trash: {sha256[..12]}");
            }
            return ok
                ? new OriginalRestoreResult(true, "Restored. It stays live for at least a day so it can be re-imported; if no email refers to it after that, it goes back to the Trash with a fresh retention period.")
                : new OriginalRestoreResult(false, "It could not be restored (see the server log).");
            }
            finally { _opGate.Release(); }
        }

        public async Task<ArchiveIdentityCheck> GetIdentityAsync()
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var baseFull = Path.GetFullPath(sp.GetRequiredService<RawMessagePathResolver>().BasePath);
            if (!Directory.Exists(baseFull)) return new ArchiveIdentityCheck(true, ArchiveIdentityCheck.Matches, "The originals folder does not exist yet.", null, null);
            return await sp.GetRequiredService<IArchiveIdentityService>().CheckAsync(baseFull);
        }

        public async Task<ArchiveIdentityCheck> AdoptAsync(string? actor)
        {
            await _opGate.WaitAsync();
            try
            {
                using var scope = _scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var baseFull = Path.GetFullPath(sp.GetRequiredService<RawMessagePathResolver>().BasePath);
                Directory.CreateDirectory(baseFull);
                var before = await sp.GetRequiredService<IArchiveIdentityService>().CheckAsync(baseFull);
                var after = await sp.GetRequiredService<IArchiveIdentityService>().AdoptAsync(baseFull);
                var chain = sp.GetService<IAuditChain>();
                if (chain != null && _integrity.CurrentValue.AuditChainEnabled)
                    await chain.AppendAsync("archive.identity.adopted", null, null, new { actor = actor ?? "an administrator", before = before.Code, after = after.Code, archiveId = after.DatabaseId });
                var access = sp.GetService<IAccessLogService>();
                if (access != null) await access.LogAccessAsync(actor ?? "unknown", AccessLogType.Integrity, searchParameters: $"originals folder adopted as belonging to this database (was: {before.Code})");
                return after;
            }
            finally { _opGate.Release(); }
        }

        public async Task<IReadOnlyList<UnclaimedOriginal>> ListUnclaimedAsync(int limit)
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<MailArchiverDbContext>();
            var paths = sp.GetRequiredService<RawMessagePathResolver>();
            var baseFull = Path.GetFullPath(paths.BasePath);
            if (!Directory.Exists(baseFull)) return Array.Empty<UnclaimedOriginal>();
            var live = ListLive(baseFull, CancellationToken.None);
            var (_, unclaimed, _) = await ClassifyAsync(db, live, CancellationToken.None);
            var items = new List<UnclaimedOriginal>();
            foreach (var rel in unclaimed.Take(Math.Clamp(limit, 1, 1000)))
            {
                var fi = new FileInfo(paths.GetAbsolutePath(rel));
                if (fi.Exists) items.Add(new UnclaimedOriginal(ShaOf(rel), fi.Length, fi.LastWriteTimeUtc));
            }
            return items;
        }

        public async Task<OriginalRestoreResult> MoveToTrashAsync(string sha256, string? actor)
        {
            if (!Sha.IsMatch(sha256 ?? string.Empty)) return new OriginalRestoreResult(false, "Expected a lowercase hex SHA-256 (64 characters).");
            if (!OriginalCleanupOptions.IsApplicable(_raw.CurrentValue, out var why)) return new OriginalRestoreResult(false, "The Trash does not apply to this archive: " + why);
            if (!await _opGate.WaitAsync(TimeSpan.FromSeconds(2))) return new OriginalRestoreResult(false, "A Trash run is in progress; try again when it has finished.");
            try
            {
                using var scope = _scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<MailArchiverDbContext>();
                var paths = sp.GetRequiredService<RawMessagePathResolver>();
                var baseFull = Path.GetFullPath(paths.BasePath);
                var identity = await sp.GetRequiredService<IArchiveIdentityService>().CheckAsync(baseFull);
                if (!identity.Ok) return new OriginalRestoreResult(false, identity.Message);
                var rel = LiveRel(sha256);
                if (!File.Exists(paths.GetAbsolutePath(rel))) return new OriginalRestoreResult(false, "That original is not in the originals folder.");
                if (await IsReferencedAsync(db, rel, CancellationToken.None)) return new OriginalRestoreResult(false, "An email still refers to that original, so it stays.");
                var c = new Counters();
                var x = new Ctx(db, paths, baseFull, sp.GetService<IAuditChain>(), c);
                await BinOneAsync(x, rel, $"moved to the Trash by {actor ?? "an administrator"}: no delete request was recorded for it", CancellationToken.None);
                if (c.Binned == 1)
                {
                    var access = sp.GetService<IAccessLogService>();
                    if (access != null) await access.LogAccessAsync(actor ?? "unknown", AccessLogType.Integrity, searchParameters: $"original moved to the Trash by an administrator: {sha256[..12]}");
                    return new OriginalRestoreResult(true, "Moved to the Trash. It can be restored for the retention period.");
                }
                return new OriginalRestoreResult(false, c.Deferred > 0 ? "It was written or used in the last 24 hours, so it was left alone. Try again later." : "It could not be moved (see the server log).");
            }
            finally { _opGate.Release(); }
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
                return new ErasureStatus(emailId, "InBin", "The original is in the Trash and will be deleted permanently on the date shown.", sha, 0, binned.BinnedAt, binned.BinnedAt + TimeSpan.FromDays(Opt.BinRetentionDays));

            using var s2 = _scopes.CreateScope();
            var paths = s2.ServiceProvider.GetRequiredService<RawMessagePathResolver>();
            if (File.Exists(paths.GetAbsolutePath(live)))
                return new ErasureStatus(emailId, "LiveUnreferenced", "The original is still in place and no email refers to it; the next Trash run moves it to the Trash.", sha, 0, null, null);

            var purged = await db.AuditChain.AsNoTracking().AnyAsync(c => c.Kind == "original.purged" && c.Sha256 == sha);
            return purged
                ? new ErasureStatus(emailId, "Purged", "The original was deleted permanently from the Trash.", sha, 0, null, null)
                : new ErasureStatus(emailId, "NoFile", "No file for this original exists (it may never have been stored on the filesystem).", sha, 0, null, null);
        }
    }

    /// <summary>
    /// Runs the bin every <c>RawMessages:OriginalCleanup:IntervalHours</c> hours while it is on and applies to this archive.
    /// The settings are read again on every tick (options monitor), so switching the bin on or off, or changing its interval,
    /// on the Integrity &amp; Storage page applies within a minute. The startup delay is waited once.
    /// </summary>
    public sealed class OriginalBinService : BackgroundService
    {
        internal static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(30);

        private readonly IOriginalBin _bin;
        private readonly IOptionsMonitor<RawMessageOptions> _raw;
        private readonly ILogger<OriginalBinService> _logger;
        private DateTime? _lastRun;
        private string? _lastIdleReason;

        public OriginalBinService(IOriginalBin bin, IOptionsMonitor<RawMessageOptions> raw, ILogger<OriginalBinService> logger)
        { _bin = bin; _raw = raw; _logger = logger; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, _raw.CurrentValue.OriginalCleanup.StartupDelayMinutes)), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await TickAsync(DateTime.UtcNow, stoppingToken);
                    await Task.Delay(TickEvery, stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }

        /// <summary>One look at the schedule (also used by tests with a controlled clock).</summary>
        internal async Task TickAsync(DateTime nowUtc, CancellationToken stoppingToken)
        {
            var raw = _raw.CurrentValue;
            var o = raw.OriginalCleanup;
            string? idle = null;
            if (!o.Enabled) idle = "The Trash is switched off";
            else if (!OriginalCleanupOptions.IsApplicable(raw, out var why)) idle = "The Trash is on but does not apply to this archive: " + why;
            if (idle != null)
            {
                if (idle != _lastIdleReason) _logger.LogInformation("{Reason}", idle);
                _lastIdleReason = idle; _lastRun = null;
                return;
            }
            _lastIdleReason = null;

            if (_lastRun != null && nowUtc - _lastRun.Value < TimeSpan.FromHours(Math.Max(1, o.IntervalHours))) return;
            _lastRun = nowUtc;
            try { await _bin.RunOnceAsync("schedule", stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Scheduled original bin run failed"); }
        }
    }
}
