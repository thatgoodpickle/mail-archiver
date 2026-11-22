using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Controllers.Api.V1;

/// <summary>
/// Verify-all runs over the whole archive. Admin API key required (a missing key is rejected with
/// 401 by the API key middleware, a valid non-admin key with 403 by <see cref="AdminOnlyAttribute"/>).
/// </summary>
[Route("api/v1/integrity")]
[AdminOnly]
public class IntegrityApiController : ApiControllerBase
{
    private readonly MailArchiverDbContext _context;
    private readonly IIntegrityRunner _runner;
    private readonly IAuthenticationService _authService;

    private readonly IAuditChainVerifier? _chain;
    private readonly IManifestService? _manifests;
    private readonly IAccessLogService? _accessLog;
    private readonly IOriginalBin? _bin;

    public IntegrityApiController(MailArchiverDbContext context, IIntegrityRunner runner, IAuthenticationService authService,
        IAuditChainVerifier? chain = null, IManifestService? manifests = null, IAccessLogService? accessLog = null,
        IOriginalBin? bin = null)
    {
        _bin = bin;
        _accessLog = accessLog;
        _manifests = manifests;
        _chain = chain;
        _context = context;
        _runner = runner;
        _authService = authService;
    }

    // F-24: integrity and audit actions leave an access-log entry (and so a chain entry) with who did what.
    private Task LogAsync(string what)
        => _accessLog == null ? Task.CompletedTask
            : _accessLog.LogAccessAsync(_authService.GetCurrentUserDisplayName(HttpContext) ?? "unknown", AccessLogType.Integrity, searchParameters: what);

    // ----- original bin (review F-11): recycle bin for hash-named originals nothing refers to -----------------------

    public sealed record RestoreRequest(string Sha256);

    /// <summary>Is it on, does it apply, what is in the bin, what ran last. Read-only.</summary>
    [HttpGet("original-cleanup/status")]
    public async Task<IActionResult> BinStatus()
    {
        if (_bin == null) return Problem(statusCode: 501, title: "The Trash is not available.");
        var s = await _bin.GetStatusAsync();
        return Ok(new
        {
            enabled = s.Enabled, applicable = s.Applicable, notApplicableReason = s.NotApplicableReason,
            binRetentionDays = s.BinRetentionDays, intervalHours = s.IntervalHours, maxFilesPerRun = s.MaxFilesPerRun,
            inBin = s.InBin, inBinBytes = s.InBinBytes, earliestPurgeAt = s.EarliestPurgeAt,
            totalBinned = s.TotalBinned, totalRestored = s.TotalRestored, totalPurged = s.TotalPurged,
            lastRun = s.LastRun == null ? null : BinRunDto(s.LastRun)
        });
    }

    private static object BinRunDto(OriginalCleanupRun r) => new
    {
        id = r.Id, status = r.Status, startedAt = r.StartedAt, finishedAt = r.FinishedAt, startedBy = r.StartedBy,
        scanned = r.Scanned, referenced = r.Referenced, binned = r.Binned, restored = r.Restored, purged = r.Purged,
        deferred = r.Deferred, errors = r.Errors, error = r.Error
    };

    /// <summary>Runs one bin pass now in the background (202). Takes no path; works only on hash-named originals.</summary>
    [HttpPost("original-cleanup/run")]
    public async Task<IActionResult> BinRun()
    {
        if (_bin == null) return Problem(statusCode: 501, title: "The Trash is not available.");
        try
        {
            var run = await _bin.StartAsync(_authService.GetCurrentUserDisplayName(HttpContext));
            await LogAsync("Trash run started");
            return Accepted(BinRunDto(run));
        }
        catch (InvalidOperationException ex) { return Problem(statusCode: 409, title: ex.Message); }
    }

    /// <summary>What is in the bin: sha256, when it was binned, when it will be deleted permanently, size. No paths.</summary>
    [HttpGet("original-cleanup/bin")]
    public async Task<IActionResult> BinList([FromQuery] int limit = 200)
    {
        if (_bin == null) return Problem(statusCode: 501, title: "The Trash is not available.");
        var items = await _bin.ListBinAsync(limit);
        return Ok(items.Select(i => new { sha256 = i.Sha256, binnedAt = i.BinnedAt, purgeAfter = i.PurgeAfter, size = i.Size }));
    }

    /// <summary>Moves one original back out of the bin, by SHA-256 only (never a path).</summary>
    [HttpPost("original-cleanup/restore")]
    public async Task<IActionResult> BinRestore([FromBody] RestoreRequest request)
    {
        if (_bin == null) return Problem(statusCode: 501, title: "The Trash is not available.");
        var r = await _bin.RestoreAsync(request?.Sha256 ?? string.Empty, _authService.GetCurrentUserDisplayName(HttpContext));
        return r.Restored ? Ok(new { restored = true, message = r.Message }) : Problem(statusCode: 409, title: r.Message);
    }

    /// <summary>For ONE already deleted email: is its original still shared, in the bin (and until when), or gone. Changes nothing.</summary>
    [HttpPost("original-cleanup/erasure-status/{emailId:int}")]
    public async Task<IActionResult> ErasureStatusFor(int emailId)
    {
        if (_bin == null) return Problem(statusCode: 501, title: "The Trash is not available.");
        var s = await _bin.DescribeEmailAsync(emailId);
        return Ok(new { emailId = s.EmailId, state = s.State, message = s.Message, sha256 = s.Sha256, referencedByRows = s.ReferencedByRows, binnedAt = s.BinnedAt, purgeAfter = s.PurgeAfter });
    }

    private static object Dto(IntegrityRun r) => new
    {
        id = r.Id, status = r.Status, startedAt = r.StartedAt, finishedAt = r.FinishedAt, startedBy = r.StartedBy,
        total = r.Total, @checked = r.Checked, valid = r.Valid, invalid = r.Invalid, missing = r.Missing,
        notStored = r.NotStored, inconsistent = r.Inconsistent, chainMismatch = r.ChainMismatch, unchained = r.Unchained, errors = r.Errors, error = r.Error
    };

    /// <summary>
    /// Starts a verify-all run in the background (202 with the run). When a run is already in
    /// progress its record is returned instead of starting a second one.
    /// </summary>
    [HttpPost("runs")]
    public async Task<IActionResult> Start()
    {
        var run = await _runner.StartAsync(_authService.GetCurrentUserDisplayName(HttpContext));
        await LogAsync($"verify-all run {run.Id} started");
        return Accepted(Dto(run));
    }

    [HttpGet("runs")]
    public async Task<IActionResult> List([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 200);
        var runs = await _context.IntegrityRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(limit).ToListAsync();
        return Ok(runs.Select(Dto));
    }

    /// <summary>
    /// Current end of the audit chain: sequence number and hash. This pair is what to copy somewhere
    /// the archive machine cannot reach; any later change to an earlier entry changes every hash after it.
    /// </summary>
    [HttpGet("chain/head")]
    public async Task<IActionResult> ChainHead()
    {
        if (_chain == null) return Problem(statusCode: 501, title: "The audit chain is not available.");
        var head = await _chain.GetHeadAsync();
        // failedAppends > 0 means audit entries could not be written since the application started (a gap in the trail).
        var failed = AuditChain.FailedAppends;
        return head == null ? Ok(new { entries = 0, head = (object?)null, failedAppends = failed }) : Ok(new { seq = head.Seq, hash = head.Hash, timestamp = head.Timestamp, failedAppends = failed });
    }

    /// <summary>
    /// The chain head as one plain-text line, made to be collected by a machine the archive host cannot
    /// reach (see tools/pull-anchor.sh): <c>time seq=N hash=H pubkey=F</c>. <c>pubkey</c> is the SHA-256 of the
    /// manifest public key, or "-" before the first manifest. "seq=0 hash=0...0" when nothing is chained yet.
    /// </summary>
    [HttpGet("chain/anchor")]
    [Produces("text/plain")]
    public async Task<IActionResult> ChainAnchor()
    {
        if (_chain == null) return Problem(statusCode: 501, title: "The audit chain is not available.");
        var head = await _chain.GetHeadAsync();
        var fp = _manifests?.GetPublicKeyFingerprint() ?? "-";
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} seq={head?.Seq ?? 0} hash={head?.Hash ?? AuditChainEntry.GenesisHash} pubkey={fp}\n";
        return Content(line, "text/plain");
    }

    /// <summary>
    /// The hash of one chain entry. A collector that stored an earlier head compares it here: a rewritten
    /// chain can verify perfectly on its own and still hold a different hash at an old sequence number.
    /// </summary>
    [HttpGet("chain/entries/{seq:long}")]
    public async Task<IActionResult> ChainEntry(long seq)
    {
        var e = await _context.AuditChain.AsNoTracking().Where(x => x.Seq == seq)
            .Select(x => new { seq = x.Seq, hash = x.EntryHash, timestamp = x.Timestamp }).FirstOrDefaultAsync();
        return e == null ? NotFound() : Ok(e);
    }

    /// <summary>Walks the whole chain and reports the first break, hash changes and unchained emails.</summary>
    [HttpGet("chain/verify")]
    public async Task<IActionResult> ChainVerify()
    {
        if (_chain == null) return Problem(statusCode: 501, title: "The audit chain is not available.");
        var r = await _chain.VerifyAsync();
        await LogAsync("audit chain verified");
        return Ok(new
        {
            ok = r.Ok, entries = r.Entries,
            head = r.Head == null ? null : new { seq = r.Head.Seq, hash = r.Head.Hash },
            firstBreak = r.FirstBreak == null ? null : new { seq = r.FirstBreak.Seq, reason = r.FirstBreak.Reason },
            warnings = r.Warnings,
            emailsWithoutArchiveEntry = r.EmailsWithoutArchiveEntry,
            emailsDeletedWithRecord = r.EmailsDeletedWithRecord,
            originalsBinned = r.OriginalsBinned, originalsRestored = r.OriginalsRestored, originalsPurged = r.OriginalsPurged,
            failedAppends = AuditChain.FailedAppends
        });
    }

    /// <summary>Writes and signs a manifest of every stored original plus the chain head.</summary>
    [HttpPost("manifests")]
    public async Task<IActionResult> CreateManifest()
    {
        if (_manifests == null) return Problem(statusCode: 501, title: "Manifests are not available.");
        var m = await _manifests.CreateAsync(_authService.GetCurrentUserDisplayName(HttpContext));
        await LogAsync($"manifest {m.Name} created");
        return Ok(m);
    }

    [HttpGet("manifests")]
    public async Task<IActionResult> ListManifests([FromQuery] int limit = 50)
    {
        if (_manifests == null) return Problem(statusCode: 501, title: "Manifests are not available.");
        return Ok(await _manifests.ListAsync(Math.Clamp(limit, 1, 500)));
    }

    /// <summary>Checks a manifest: signature, entries file, chain head, and emails changed since.</summary>
    [HttpGet("manifests/{name}/verify")]
    public async Task<IActionResult> VerifyManifest(string name)
    {
        if (_manifests == null) return Problem(statusCode: 501, title: "Manifests are not available.");
        var check = await _manifests.VerifyAsync(name);
        if (check != null) await LogAsync($"manifest {check.Name} verified");
        return check == null ? NotFound() : Ok(check);
    }

    /// <summary>One run with its findings (the first 1000, oldest first).</summary>
    [HttpGet("runs/{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var run = await _context.IntegrityRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (run == null) return NotFound();

        var findings = await _context.IntegrityFindings.AsNoTracking()
            .Where(f => f.RunId == id).OrderBy(f => f.Id).Take(1000)
            .Select(f => new { emailId = f.EmailId, status = f.Status, details = f.Details })
            .ToListAsync();

        return Ok(new { run = Dto(run), findings });
    }
}
