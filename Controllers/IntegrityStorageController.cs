using MailArchiver.Attributes;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;
using MailArchiver.Services;
using MailArchiver.Services.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Controllers
{
    /// <summary>
    /// Admin-only "Integrity &amp; Storage" page (fork): status and actions for verify-all, the audit chain, signed manifests, the
    /// original bin and attachment text/OCR, plus the settings (locked ones read-only, editable ones audited). It uses the web
    /// login (cookie), not the API keys, and calls the same services as the API. Every action is covered by
    /// <see cref="AdminRequiredAttribute"/> on the class; every POST validates the anti-forgery token.
    /// </summary>
    [AdminRequired]
    public class IntegrityStorageController : Controller
    {
        private readonly MailArchiverDbContext _db;
        private readonly ISettingsService _settings;
        private readonly IIntegrityRunner _runner;
        private readonly IAuditChainVerifier _chain;
        private readonly IManifestService _manifests;
        private readonly IOriginalBin _bin;
        private readonly IAttachmentTextProcessor _text;
        private readonly MailArchiver.Services.IAuthenticationService _auth;
        private readonly IAccessLogService _accessLog;
        private readonly ILogger<IntegrityStorageController> _logger;
        private readonly ISyncProblemService? _problems;

        public IntegrityStorageController(MailArchiverDbContext db, ISettingsService settings, IIntegrityRunner runner,
            IAuditChainVerifier chain, IManifestService manifests, IOriginalBin bin, IAttachmentTextProcessor text,
            MailArchiver.Services.IAuthenticationService auth, IAccessLogService accessLog, ILogger<IntegrityStorageController> logger,
            ISyncProblemService? problems = null)
        {
            _problems = problems;
            _db = db; _settings = settings; _runner = runner; _chain = chain; _manifests = manifests; _bin = bin;
            _text = text; _auth = auth; _accessLog = accessLog; _logger = logger;
        }

        private string Who => _auth.GetCurrentUserDisplayName(HttpContext) ?? "unknown";

        private Task LogAsync(string what) => _accessLog.LogAccessAsync(Who, AccessLogType.Integrity, searchParameters: what);

        private static string Short(Exception ex) => ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;

        // ----------------------------------------------------------------------------------------- page

        [HttpGet]
        public async Task<IActionResult> Index() => View(await BuildViewModelAsync());

        private async Task<IntegrityStorageViewModel> BuildViewModelAsync()
        {
            var vm = new IntegrityStorageViewModel();

            async Task Section(string name, Func<Task> load)
            {
                try { await load(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Integrity & Storage page: section {Section} could not be loaded", name);
                    vm.Errors[name] = Short(ex);
                }
            }

            await Section("overview", async () =>
            {
                vm.LastRun = await _db.IntegrityRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstOrDefaultAsync();
                var head = await _chain.GetHeadAsync();
                vm.PublicKeyFingerprint = _manifests.GetPublicKeyFingerprint();
                vm.AnchorLine = AnchorLine(head, vm.PublicKeyFingerprint);
            });
            await Section("manifests", async () => vm.Manifests = await _manifests.ListAsync(20));
            await Section("bin", async () =>
            {
                vm.BinStatus = await _bin.GetStatusAsync();
                vm.BinItems = await _bin.ListBinAsync(50);
            });
            await Section("problems", async () =>
            {
                if (_problems == null) return;
                vm.SyncProblems = await _problems.ListAsync(100);
                vm.QuarantinedCount = await _problems.CountQuarantinedAsync();
                var ids = vm.SyncProblems.Select(p => p.MailAccountId).Distinct().ToList();
                vm.AccountNames = await _db.MailAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name);
            });
            await Section("text", async () => vm.TextStatus = await _text.GetStatusAsync());
            await Section("settings", async () =>
            {
                vm.Settings = await _settings.GetAllAsync();
                vm.HasLocalPassword = await _settings.HasLocalPasswordAsync(Actor());
            });

            return vm;
        }

        private static string AnchorLine(AuditChainHead? head, string? fingerprint)
            => $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} seq={head?.Seq ?? 0} hash={head?.Hash ?? AuditChainEntry.GenesisHash} pubkey={fingerprint ?? "-"}";

        // ----------------------------------------------------------------------------------------- overview

        /// <summary>The newest verify-all run (polled while one is running).</summary>
        [HttpGet]
        public async Task<IActionResult> RunStatus()
        {
            var r = await _db.IntegrityRuns.AsNoTracking().OrderByDescending(x => x.Id).FirstOrDefaultAsync();
            return Json(r == null ? null : RunDto(r));
        }

        private static object RunDto(IntegrityRun r) => new
        {
            id = r.Id, status = r.Status, startedAt = r.StartedAt, finishedAt = r.FinishedAt, startedBy = r.StartedBy,
            total = r.Total, @checked = r.Checked, valid = r.Valid, invalid = r.Invalid, missing = r.Missing,
            notStored = r.NotStored, inconsistent = r.Inconsistent, errors = r.Errors, error = r.Error
        };

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RunVerifyAll()
        {
            try
            {
                var run = await _runner.StartAsync(Who);
                await LogAsync("verify-all started from the Integrity & Storage page");
                return Json(new { ok = true, run = RunDto(run) });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckChain()
        {
            try
            {
                var r = await _chain.VerifyAsync();
                await LogAsync("audit chain verified from the Integrity & Storage page");
                return Json(new
                {
                    ok = true, chainOk = r.Ok, entries = r.Entries,
                    head = r.Head == null ? null : new { seq = r.Head.Seq, hash = r.Head.Hash },
                    firstBreak = r.FirstBreak == null ? null : new { seq = r.FirstBreak.Seq, reason = r.FirstBreak.Reason },
                    warnings = r.Warnings, unchained = r.EmailsWithoutArchiveEntry, emailsDeletedWithRecord = r.EmailsDeletedWithRecord,
                    originalsBinned = r.OriginalsBinned, originalsRestored = r.OriginalsRestored, originalsPurged = r.OriginalsPurged
                });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        /// <summary>The chain head as one line, to copy to a machine the archive host cannot reach.</summary>
        [HttpGet]
        public async Task<IActionResult> Anchor()
        {
            var head = await _chain.GetHeadAsync();
            return Content(AnchorLine(head, _manifests.GetPublicKeyFingerprint()) + "\n", "text/plain");
        }

        // ----------------------------------------------------------------------------------------- manifests

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateManifest()
        {
            try
            {
                var m = await _manifests.CreateAsync(Who);
                await LogAsync($"manifest {m.Name} created from the Integrity & Storage page");
                return Json(new { ok = true, name = m.Name, entries = m.Entries, chainSeq = m.ChainSeq });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyManifest(string? name)
        {
            try
            {
                var c = await _manifests.VerifyAsync(name ?? string.Empty);
                if (c == null) return Json(new { ok = false, message = "Unknown manifest." });
                await LogAsync($"manifest {c.Name} verified from the Integrity & Storage page");
                return Json(new
                {
                    ok = true, manifestOk = c.Ok, name = c.Name, signatureValid = c.SignatureValid, entriesFileMatches = c.EntriesFileMatches,
                    chainStillContainsHead = c.ChainStillContainsHead, emailsChangedOrMissingSince = c.EmailsChangedOrMissingSince, problems = c.Problems
                });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        /// <summary>Downloads one file of a manifest (json, sig, jsonl) or the public key (kind=key). Takes only a validated name, never a path.</summary>
        [HttpGet]
        public async Task<IActionResult> DownloadManifestFile(string? name, string? kind)
        {
            var path = _manifests.ResolveFile(name, kind);
            if (path == null) return NotFound();
            var k = kind!.ToLowerInvariant();
            // N-18: a GET must not write to the audit chain (a page could make an admin's browser request it thousands of times).
            await Task.CompletedTask;
            var type = k switch { "json" => "application/json", "jsonl" => "application/x-ndjson", "key" => "application/x-pem-file", _ => "application/octet-stream" };
            return PhysicalFile(path, type, Path.GetFileName(path));
        }

        // ----------------------------------------------------------------------------------------- original bin

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BinRun(bool anyway = false)
        {
            try
            {
                var run = await _bin.StartAsync(Who, overrideBreaker: anyway);
                await LogAsync(anyway ? "Trash run started from the Integrity & Storage page with the safety limit overridden" : "Trash run started from the Integrity & Storage page");
                return Json(new { ok = true, status = run.Status, id = run.Id });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BinRestore(string? sha256)
        {
            var sha = (sha256 ?? string.Empty).Trim();
            var r = await _bin.RestoreAsync(sha, Who);
            await LogAsync($"restore from the Trash requested for {(sha.Length > 12 ? sha[..12] : sha)}: {(r.Restored ? "restored" : "not restored")}");
            return Json(new { ok = r.Restored, message = r.Message });
        }

        /// <summary>The explicit admin step for an originals folder the Trash refuses (upgrade, restored or moved volume): this database owns it.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BinAdopt()
        {
            try
            {
                var r = await _bin.AdoptAsync(Who);
                return Json(new { ok = r.Ok, message = r.Ok ? "Done. This database is now the owner of the originals folder, and the Trash can run." : r.Message });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        /// <summary>Lists originals that no email refers to and that have no recorded delete request (scans the folder, so it is only done on request).</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BinUnclaimed()
        {
            try
            {
                var items = await _bin.ListUnclaimedAsync(100);
                return Json(new { ok = true, items = items.Select(i => new { sha256 = i.Sha256, size = i.Size, modified = i.Modified.ToString("yyyy-MM-dd HH:mm") + " UTC" }) });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BinMoveUnclaimed(string? sha256)
        {
            var sha = (sha256 ?? string.Empty).Trim();
            var r = await _bin.MoveToTrashAsync(sha, Who);
            await LogAsync($"move to the Trash requested for an original without a delete request {(sha.Length > 12 ? sha[..12] : sha)}: {(r.Restored ? "moved" : "not moved")}");
            return Json(new { ok = r.Restored, message = r.Message });
        }

        // ----------------------------------------------------------------------------------------- messages that could not be archived

        /// <summary>Puts one quarantined message (or all) back into the normal flow; the next sync tries again.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ProblemRetry(int? id)
        {
            if (_problems == null) return Json(new { ok = false, message = "Not available." });
            try
            {
                var n = await _problems.RetryAsync(id, null);
                await LogAsync($"retry requested for {(id.HasValue ? "one quarantined message" : "all quarantined messages")}: {n} put back");
                return Json(new { ok = true, count = n, message = n == 0 ? "Nothing to retry." : $"{n} {(n == 1 ? "message" : "messages")} will be tried again at the next sync." });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        // ----------------------------------------------------------------------------------------- attachment text / OCR

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> OcrRescan(string? scope)
        {
            try
            {
                int removed;
                var s = (scope ?? string.Empty).ToLowerInvariant();
                if (s == "needsocr") removed = await _text.RescanNeedsOcrAsync();
                else if (s == "failed") removed = await _text.RescanAsync(false);
                else return Json(new { ok = false, message = "Scope must be 'failed' or 'needsocr'." });
                await LogAsync($"attachment-text rescan scope={s} removed={removed} (Integrity & Storage page)");
                return Json(new { ok = true, scope = s, removed });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        // ----------------------------------------------------------------------------------------- settings

        private SettingActor Actor() => new(_auth.GetCurrentUserId(HttpContext), Who, HttpContext.Connection.RemoteIpAddress?.ToString());

        /// <summary>
        /// The single Save of the settings form. Fields: <c>v_&lt;Key&gt;</c> = the new value of a changed setting, <c>r_&lt;Key&gt;</c> = go back to
        /// the standard value, <c>password</c> = the admin's password (needed when a change lowers a protection; the page asks for it in a
        /// dialog, the server enforces it). All or nothing: see <see cref="ISettingsService.ChangeManyAsync"/>.
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSettings(IFormCollection form)
        {
            var changes = new List<SettingChange>();
            var resets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in form.Keys.Where(k => k.StartsWith("r_", StringComparison.Ordinal))) resets.Add(name[2..]);
            foreach (var key in resets) changes.Add(new SettingChange(key, null, Reset: true));
            foreach (var name in form.Keys.Where(k => k.StartsWith("v_", StringComparison.Ordinal)))
            {
                var key = name[2..];
                if (resets.Contains(key)) continue;   // a reset wins over a typed value
                changes.Add(new SettingChange(key, form[name].ToString(), Reset: false));
            }

            var result = await _settings.ChangeManyAsync(changes, Actor(), form["password"].ToString());

            // Not saved: show the form again with what was typed, so nothing the admin entered is lost.
            if (!result.Applied && (result.RowErrors.Count > 0 || result.NeedsPassword))
            {
                var vm = await BuildViewModelAsync();
                vm.SaveMessage = result.Message;
                foreach (var e in result.RowErrors) vm.SettingErrors[e.Key] = e.Value;
                foreach (var c in changes) { if (c.Reset) vm.PostedResets.Add(c.Key); else vm.PostedValues[c.Key] = c.Value ?? string.Empty; }
                if (result.NeedsPassword) vm.PasswordProblem = result.Message;
                return View(nameof(Index), vm);
            }

            TempData["SettingsOk"] = result.Applied;   // true: saved (or nothing to change); false: nothing was applied
            TempData["SettingsMessage"] = result.Message;
            if (result.AppliedLines.Count > 0) TempData["SettingsApplied"] = string.Join("\n", result.AppliedLines);
            if (result.NotApplied.Count > 0) TempData["SettingsNotApplied"] = string.Join("\n", result.NotApplied);
            return Redirect(Url.Action(nameof(Index))! + "#settings");
        }
    }
}
