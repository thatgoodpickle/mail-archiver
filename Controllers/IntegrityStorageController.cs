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

        public IntegrityStorageController(MailArchiverDbContext db, ISettingsService settings, IIntegrityRunner runner,
            IAuditChainVerifier chain, IManifestService manifests, IOriginalBin bin, IAttachmentTextProcessor text,
            MailArchiver.Services.IAuthenticationService auth, IAccessLogService accessLog, ILogger<IntegrityStorageController> logger)
        {
            _db = db; _settings = settings; _runner = runner; _chain = chain; _manifests = manifests; _bin = bin;
            _text = text; _auth = auth; _accessLog = accessLog; _logger = logger;
        }

        private string Who => _auth.GetCurrentUserDisplayName(HttpContext) ?? "unknown";

        private Task LogAsync(string what) => _accessLog.LogAccessAsync(Who, AccessLogType.Integrity, searchParameters: what);

        private static string Short(Exception ex) => ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;

        // ----------------------------------------------------------------------------------------- page

        [HttpGet]
        public async Task<IActionResult> Index()
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
            await Section("text", async () => vm.TextStatus = await _text.GetStatusAsync());
            await Section("settings", async () => vm.Settings = await _settings.GetAllAsync());

            return View(vm);
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
            await LogAsync($"manifest file {(k == "key" ? "public-key.pem" : name + "." + k)} downloaded");
            var type = k switch { "json" => "application/json", "jsonl" => "application/x-ndjson", "key" => "application/x-pem-file", _ => "application/octet-stream" };
            return PhysicalFile(path, type, Path.GetFileName(path));
        }

        // ----------------------------------------------------------------------------------------- original bin

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BinRun()
        {
            try
            {
                var run = await _bin.StartAsync(Who);
                await LogAsync("original bin run started from the Integrity & Storage page");
                return Json(new { ok = true, status = run.Status, id = run.Id });
            }
            catch (Exception ex) { return Json(new { ok = false, message = Short(ex) }); }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> BinRestore(string? sha256)
        {
            var sha = (sha256 ?? string.Empty).Trim();
            var r = await _bin.RestoreAsync(sha, Who);
            await LogAsync($"original bin restore requested for {(sha.Length > 12 ? sha[..12] : sha)}: {(r.Restored ? "restored" : "not restored")}");
            return Json(new { ok = r.Restored, message = r.Message });
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

        private SettingActor Actor() => new(_auth.GetCurrentUserId(HttpContext), Who);

        private IActionResult BackToSettings(SettingChangeResult r)
        {
            TempData["SettingsOk"] = r.Ok;
            TempData["SettingsMessage"] = r.Message;
            return Redirect(Url.Action(nameof(Index))! + "#settings");
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeSetting(string? key, string? value, string? password)
            => BackToSettings(await _settings.ChangeAsync(key ?? string.Empty, value, Actor(), password));

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetSetting(string? key, string? password)
            => BackToSettings(await _settings.ChangeAsync(key ?? string.Empty, null, Actor(), password, reset: true));
    }
}
