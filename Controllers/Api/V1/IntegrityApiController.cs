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

    public IntegrityApiController(MailArchiverDbContext context, IIntegrityRunner runner, IAuthenticationService authService)
    {
        _context = context;
        _runner = runner;
        _authService = authService;
    }

    private static object Dto(IntegrityRun r) => new
    {
        id = r.Id, status = r.Status, startedAt = r.StartedAt, finishedAt = r.FinishedAt, startedBy = r.StartedBy,
        total = r.Total, @checked = r.Checked, valid = r.Valid, invalid = r.Invalid, missing = r.Missing,
        notStored = r.NotStored, inconsistent = r.Inconsistent, errors = r.Errors, error = r.Error
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
