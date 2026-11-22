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

    public IntegrityApiController(MailArchiverDbContext context, IIntegrityRunner runner, IAuthenticationService authService,
        IAuditChainVerifier? chain = null, IManifestService? manifests = null)
    {
        _manifests = manifests;
        _chain = chain;
        _context = context;
        _runner = runner;
        _authService = authService;
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
        return Ok(new
        {
            ok = r.Ok, entries = r.Entries,
            head = r.Head == null ? null : new { seq = r.Head.Seq, hash = r.Head.Hash },
            firstBreak = r.FirstBreak == null ? null : new { seq = r.FirstBreak.Seq, reason = r.FirstBreak.Reason },
            warnings = r.Warnings,
            emailsWithoutArchiveEntry = r.EmailsWithoutArchiveEntry,
            emailsDeletedWithRecord = r.EmailsDeletedWithRecord,
            failedAppends = AuditChain.FailedAppends
        });
    }

    /// <summary>Writes and signs a manifest of every stored original plus the chain head.</summary>
    [HttpPost("manifests")]
    public async Task<IActionResult> CreateManifest()
    {
        if (_manifests == null) return Problem(statusCode: 501, title: "Manifests are not available.");
        var m = await _manifests.CreateAsync(_authService.GetCurrentUserDisplayName(HttpContext));
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
