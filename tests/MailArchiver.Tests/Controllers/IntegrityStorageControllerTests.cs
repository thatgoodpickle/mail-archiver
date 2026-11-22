using System.Reflection;
using System.Text.Json;
using MailArchiver.Attributes;
using MailArchiver.Controllers;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;
using MailArchiver.Services;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Tests.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Controllers;

/// <summary>Who may use the Integrity and Storage page, and what its actions do. Synthetic data only.</summary>
public class IntegrityStorageControllerPolicyTests
{
    private static readonly Type ControllerType = typeof(IntegrityStorageController);

    private static List<MethodInfo> Actions()
        => ControllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).ToList();

    [Fact]
    public void TheWholeController_IsAdminOnly_AndNoActionOpensItUp()
    {
        Assert.NotNull(ControllerType.GetCustomAttribute<AdminRequiredAttribute>());
        Assert.NotEmpty(Actions());
        foreach (var m in Actions())
        {
            Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Null(m.GetCustomAttribute<UserAccessRequiredAttribute>());   // a weaker role check must not sit beside the admin check
        }
    }

    [Fact]
    public void EveryActionDeclaresItsVerb_AndEveryPostChecksTheAntiForgeryToken()
    {
        foreach (var m in Actions())
        {
            var isPost = m.GetCustomAttribute<HttpPostAttribute>() != null;
            var isGet = m.GetCustomAttribute<HttpGetAttribute>() != null;
            Assert.True(isPost ^ isGet, $"{m.Name} must be exactly one of GET or POST");
            if (isPost) Assert.True(m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() != null, $"{m.Name} (POST) needs [ValidateAntiForgeryToken]");
        }
    }

    [Fact]
    public void OnlyReadingActions_AreGets()
    {
        // A state-changing action added as a GET would escape the anti-forgery check: this list is the whole set of GETs.
        var gets = Actions().Where(m => m.GetCustomAttribute<HttpGetAttribute>() != null).Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Anchor", "DownloadManifestFile", "Index", "RunStatus" }, gets);
    }

    private static IActionResult? RunAdminFilter(bool authenticated, bool admin)
    {
        var auth = new Mock<MailArchiver.Services.IAuthenticationService>();
        auth.Setup(a => a.IsAuthenticated(It.IsAny<HttpContext>())).Returns(authenticated);
        auth.Setup(a => a.IsCurrentUserAdmin(It.IsAny<HttpContext>())).Returns(admin);
        auth.Setup(a => a.GetCurrentUserId(It.IsAny<HttpContext>())).Returns(7);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(auth.Object).BuildServiceProvider() };
        var ctx = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: null!);
        new AdminRequiredAttribute().OnActionExecuting(ctx);
        return ctx.Result;
    }

    [Fact]
    public void ANonAdmin_IsDenied_AnAnonymousVisitor_GoesToLogin_AnAdmin_Passes()
    {
        var denied = Assert.IsType<RedirectToActionResult>(RunAdminFilter(authenticated: true, admin: false));
        Assert.Equal("AccessDenied", denied.ActionName);

        var login = Assert.IsType<RedirectToActionResult>(RunAdminFilter(authenticated: false, admin: false));
        Assert.Equal("Login", login.ActionName);

        Assert.Null(RunAdminFilter(authenticated: true, admin: true));
    }

    [Fact]
    public void NoActionTakesAPathOrAFileName_OnlyValidatedNamesKindsAndHashes()
    {
        foreach (var m in Actions())
            foreach (var p in m.GetParameters())
                Assert.DoesNotContain(p.Name!.ToLowerInvariant(), new[] { "path", "file", "filename", "folder", "directory" });
    }
}

/// <summary>The actions themselves, against the real settings stack and the test database.</summary>
[Collection(TestDbFixture.CollectionName)]
public class IntegrityStorageControllerTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "isc-" + Guid.NewGuid().ToString("N"));

    public IntegrityStorageControllerTests(TestDbFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await using var ctx = SettingsKit.NewContext(_fixture.ConnectionString);
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM mail_archiver.\"AppSettings\"");
    }

    public async Task DisposeAsync()
    {
        await InitializeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Kit
    {
        public SettingsHarness Settings = null!;
        public IntegrityStorageController Controller = null!;
        public Mock<IIntegrityRunner> Runner = new();
        public Mock<IAuditChainVerifier> Chain = new();
        public Mock<IManifestService> Manifests = new();
        public Mock<IOriginalBin> Bin = new();
        public Mock<IAttachmentTextProcessor> Text = new();
        public RecordingAccessLog Log = new();
        public MailArchiverDbContext Db = null!;
        public IServiceScope Scope = null!;
    }

    private Kit NewKit(Action<Mock<IUserService>>? users = null, bool chainFails = false)
    {
        var k = new Kit { Settings = SettingsKit.Create(_fixture.ConnectionString, null, users, chainFails) };
        k.Db = SettingsKit.NewContext(_fixture.ConnectionString);
        k.Scope = k.Settings.Sp.CreateScope();

        var auth = new Mock<MailArchiver.Services.IAuthenticationService>();
        auth.Setup(a => a.GetCurrentUserDisplayName(It.IsAny<HttpContext>())).Returns("admin");
        auth.Setup(a => a.GetCurrentUserId(It.IsAny<HttpContext>())).Returns(1);

        k.Manifests.Setup(m => m.ListAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<ManifestInfo>());
        k.Chain.Setup(c => c.GetHeadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AuditChainHead(5, new string('a', 64), DateTime.UtcNow));
        k.Bin.Setup(b => b.GetStatusAsync()).ReturnsAsync(new OriginalBinStatus(true, true, null, 30, 1, 1000, 0, 0, null, 0, 0, 0, null));
        k.Bin.Setup(b => b.ListBinAsync(It.IsAny<int>())).ReturnsAsync(Array.Empty<OriginalBinItem>());
        k.Text.Setup(t => t.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AttachmentTextStatus("off", Array.Empty<AttachmentTextCount>(), 0, 0));

        var http = new DefaultHttpContext();
        var url = new Mock<IUrlHelper>();
        url.Setup(u => u.Action(It.IsAny<UrlActionContext>())).Returns("/IntegrityStorage");
        k.Controller = new IntegrityStorageController(k.Db, k.Scope.ServiceProvider.GetRequiredService<ISettingsService>(), k.Runner.Object,
            k.Chain.Object, k.Manifests.Object, k.Bin.Object, k.Text.Object, auth.Object, k.Log, NullLogger<IntegrityStorageController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>()),
            Url = url.Object
        };
        return k;
    }

    private static JsonElement Json(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        return JsonSerializer.SerializeToElement(json.Value);
    }

    // ------------------------------------------------------------------------------------ settings through the page: ONE save

    private static FormCollection Form(params (string Name, string Value)[] fields)
        => new(fields.ToDictionary(f => f.Name, f => new Microsoft.Extensions.Primitives.StringValues(f.Value)));

    private static IntegrityStorageViewModel ShownAgain(IActionResult result)
        => Assert.IsType<IntegrityStorageViewModel>(Assert.IsType<ViewResult>(result).Model);

    private async Task<string?> StoredAsync(Kit k, string key)
        => (await k.Db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;

    [Theory]
    [InlineData("Storage:Mode", "Lean")]
    [InlineData("RawMessages:Required", "true")]
    [InlineData("RawMessages:FileSystem:BasePath", "/somewhere/else")]
    [InlineData("Integrity:AuditChainEnabled", "false")]
    [InlineData("Integrity:AllowNewKey", "true")]
    [InlineData("Storage:AllowSerializedImports", "true")]
    public async Task SaveSettings_OnALockedSetting_IsRefused_AndNothingChanges(string key, string value)
    {
        var k = NewKit();
        var vm = ShownAgain(await k.Controller.SaveSettings(Form(("v_" + key, value), ("password", "secret"))));

        Assert.Contains("locked", vm.SettingErrors[key]);
        Assert.False(await k.Db.AppSettings.AnyAsync());
        Assert.Empty(k.Settings.Chain.Entries);
    }

    [Fact]
    public async Task SaveSettings_SafeChanges_SaveStraightAway_WithoutAPassword_OneRecordEach()
    {
        var k = NewKit();
        var result = await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "44"), ("v_Ocr:TimeoutSecondsPerImage", "12")));

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.EndsWith("#settings", redirect.Url);
        Assert.Equal(true, k.Controller.TempData["SettingsOk"]);
        Assert.Equal("44", await StoredAsync(k, "Ocr:MaxPages"));
        Assert.Equal("12", await StoredAsync(k, "Ocr:TimeoutSecondsPerImage"));
        Assert.Equal("admin", (await k.Db.AppSettings.AsNoTracking().SingleAsync(s => s.Key == "Ocr:MaxPages")).UpdatedBy);
        Assert.Equal(2, k.Settings.Chain.Entries.Count(e => e.Kind == "setting.changed"));
        Assert.Equal(2, k.Settings.Log.Rows.Count(r => r.Type == AccessLogType.Integrity));
        Assert.Contains("Pages read from a scanned PDF", (string)k.Controller.TempData["SettingsApplied"]!);
    }

    [Fact]
    public async Task SaveSettings_OneBadRow_BlocksTheWholeSave_AndEachBadRowSaysWhy()
    {
        var k = NewKit();
        var vm = ShownAgain(await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "44"), ("v_Ocr:Threads", "99"), ("v_Ocr:MaxImagePixels", "abc"))));

        Assert.Equal(new[] { "Ocr:MaxImagePixels", "Ocr:Threads" }, vm.SettingErrors.Keys.OrderBy(x => x).ToArray());   // the good row has no error
        Assert.Equal("44", vm.PostedValues["Ocr:MaxPages"]);        // what was typed is shown again
        Assert.Equal("99", vm.PostedValues["Ocr:Threads"]);
        Assert.False(await k.Db.AppSettings.AnyAsync());            // nothing was applied, not even the good row
        Assert.Empty(k.Settings.Chain.Entries);
        Assert.Empty(k.Settings.Log.Rows);
    }

    [Fact]
    public async Task SaveSettings_ALockedRowAmongGoodOnes_BlocksTheWholeSave()
    {
        var k = NewKit();
        var vm = ShownAgain(await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "44"), ("v_Storage:Mode", "Lean"))));
        Assert.Single(vm.SettingErrors, e => e.Key == "Storage:Mode");
        Assert.False(await k.Db.AppSettings.AnyAsync());
    }

    [Fact]
    public async Task SaveSettings_AMixOfSafeAndRisky_NeedsThePassword_TheServerEnforcesIt_AndNothingIsAppliedWithoutIt()
    {
        var k = NewKit();
        var mixed = new[] { ("v_Ocr:MaxPages", "44"), ("v_RawMessages:OriginalCleanup:BinRetentionDays", "7") };   // 30 -> 7 shortens: risky

        // No password at all (the dialog was bypassed): refused, and the safe row is not applied either.
        var none = ShownAgain(await k.Controller.SaveSettings(Form(mixed)));
        Assert.False(string.IsNullOrEmpty(none.PasswordProblem));
        Assert.Equal("44", none.PostedValues["Ocr:MaxPages"]);
        Assert.False(await k.Db.AppSettings.AnyAsync());

        // A wrong password: refused.
        var wrong = ShownAgain(await k.Controller.SaveSettings(Form(mixed.Append(("password", "nope")).ToArray())));
        Assert.Contains("not correct", wrong.PasswordProblem);
        Assert.False(await k.Db.AppSettings.AnyAsync());
        Assert.Empty(k.Settings.Chain.Entries);

        // The right password: both are applied, one record each.
        var ok = await k.Controller.SaveSettings(Form(mixed.Append(("password", "secret")).ToArray()));
        Assert.IsType<RedirectResult>(ok);
        Assert.Equal("44", await StoredAsync(k, "Ocr:MaxPages"));
        Assert.Equal("7", await StoredAsync(k, "RawMessages:OriginalCleanup:BinRetentionDays"));
        Assert.Equal(2, k.Settings.Chain.Entries.Count(e => e.Kind == "setting.changed"));
    }

    [Fact]
    public async Task SaveSettings_WithoutALocalPassword_TheSafeChangesApply_TheRiskyOnesDoNot_AndTheMessageSaysSo()
    {
        var k = NewKit(users: m => m.Setup(u => u.GetUserByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new User { Id = 1, Username = "admin", PasswordHash = null, OAuthRemoteUserId = "sso-subject" }));

        var result = await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "44"), ("v_RawMessages:OriginalCleanup:BinRetentionDays", "7"), ("password", "secret")));

        Assert.IsType<RedirectResult>(result);
        Assert.Equal(true, k.Controller.TempData["SettingsOk"]);
        Assert.Equal("44", await StoredAsync(k, "Ocr:MaxPages"));
        Assert.Null(await StoredAsync(k, "RawMessages:OriginalCleanup:BinRetentionDays"));
        Assert.Contains("single sign-on", (string)k.Controller.TempData["SettingsNotApplied"]!);
        Assert.Contains("RawMessages__OriginalCleanup__BinRetentionDays", (string)k.Controller.TempData["SettingsNotApplied"]!);
        Assert.Single(k.Settings.Chain.Entries, e => e.Kind == "setting.changed");   // only the applied change is recorded
    }

    [Fact]
    public async Task SaveSettings_WithoutALocalPassword_OnlyRiskyChanges_AppliesNothing()
    {
        var k = NewKit(users: m => m.Setup(u => u.GetUserByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new User { Id = 1, Username = "admin", PasswordHash = null, OAuthRemoteUserId = "sso-subject" }));

        await k.Controller.SaveSettings(Form(("v_RawMessages:OriginalCleanup:BinRetentionDays", "7")));

        Assert.Equal(false, k.Controller.TempData["SettingsOk"]);
        Assert.False(await k.Db.AppSettings.AnyAsync());
        Assert.Empty(k.Settings.Chain.Entries);
    }

    [Fact]
    public async Task SaveSettings_WhenTheRecordCannotBeWritten_NothingIsApplied()
    {
        var k = NewKit(chainFails: true);
        await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "44"), ("v_Ocr:TimeoutSecondsPerImage", "12")));

        Assert.Equal(false, k.Controller.TempData["SettingsOk"]);
        Assert.False(await k.Db.AppSettings.AnyAsync());
    }

    [Fact]
    public async Task SaveSettings_UnchangedValues_ChangeNothing_AndWriteNoRecord()
    {
        var k = NewKit();
        var current = (await k.Settings.ViewAsync("Ocr:MaxPages")).Value;
        await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", current)));

        Assert.Equal(true, k.Controller.TempData["SettingsOk"]);
        Assert.False(await k.Db.AppSettings.AnyAsync());
        Assert.Empty(k.Settings.Chain.Entries);
    }

    [Fact]
    public async Task SaveSettings_AResetOnlyMarksTheRow_UntilSave_AndThenGoesBackToTheStandardValue()
    {
        var k = NewKit();
        await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "44")));
        Assert.Equal("44", await StoredAsync(k, "Ocr:MaxPages"));

        await k.Controller.SaveSettings(Form(("r_Ocr:MaxPages", "1")));
        Assert.Null(await StoredAsync(k, "Ocr:MaxPages"));
        Assert.Equal(2, k.Settings.Chain.Entries.Count(e => e.Kind == "setting.changed"));

        // A reset wins over a value typed for the same setting.
        await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "50")));
        await k.Controller.SaveSettings(Form(("v_Ocr:MaxPages", "60"), ("r_Ocr:MaxPages", "1")));
        Assert.Null(await StoredAsync(k, "Ocr:MaxPages"));
    }

    [Fact]
    public async Task SaveSettings_ReshowsThePageWithWhatWasTyped_AndOpensTheDialogAgainWhenThePasswordWasWrong()
    {
        var k = NewKit();
        var vm = ShownAgain(await k.Controller.SaveSettings(Form(("v_RawMessages:OriginalCleanup:BinRetentionDays", "7"), ("password", "nope"))));
        Assert.Equal("7", vm.PostedValues["RawMessages:OriginalCleanup:BinRetentionDays"]);
        Assert.NotNull(vm.PasswordProblem);
        Assert.True(vm.HasLocalPassword);
    }

    // ------------------------------------------------------------------------------------ actions

    [Fact]
    public async Task ActionsThatFail_ReportAnErrorInJson_NotAnException()
    {
        var k = NewKit();
        k.Runner.Setup(r => r.StartAsync(It.IsAny<string?>())).ThrowsAsync(new InvalidOperationException("boom"));
        k.Manifests.Setup(m => m.CreateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no key"));
        k.Chain.Setup(c => c.VerifyAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        k.Bin.Setup(b => b.StartAsync(It.IsAny<string?>())).ThrowsAsync(new InvalidOperationException("not applicable"));

        foreach (var r in new[] { await k.Controller.RunVerifyAll(), await k.Controller.CreateManifest(), await k.Controller.CheckChain(), await k.Controller.BinRun() })
        {
            var j = Json(r);
            Assert.False(j.GetProperty("ok").GetBoolean());
            Assert.False(string.IsNullOrEmpty(j.GetProperty("message").GetString()));
        }
    }

    [Fact]
    public async Task CheckChain_ReportsTheResult_AndIsAccessLogged()
    {
        var k = NewKit();
        k.Chain.Setup(c => c.VerifyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new AuditChainReport(true, 9, new AuditChainHead(9, new string('b', 64), DateTime.UtcNow), null, new[] { "w" }, 2, 1, 3, 1, 0));

        var j = Json(await k.Controller.CheckChain());
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.True(j.GetProperty("chainOk").GetBoolean());
        Assert.Equal(9, j.GetProperty("entries").GetInt64());
        Assert.Equal(2, j.GetProperty("unchained").GetInt64());
        Assert.Equal(3, j.GetProperty("originalsBinned").GetInt64());
        Assert.Contains(k.Log.Rows, r => r.Type == AccessLogType.Integrity && r.Text!.Contains("audit chain"));
    }

    [Fact]
    public async Task RunVerifyAll_StartsOneRun_AndIsAccessLogged()
    {
        var k = NewKit();
        k.Runner.Setup(r => r.StartAsync("admin")).ReturnsAsync(new IntegrityRun { Id = 3, Status = IntegrityRun.Running });
        var j = Json(await k.Controller.RunVerifyAll());
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.Equal(3, j.GetProperty("run").GetProperty("id").GetInt32());
        Assert.Contains(k.Log.Rows, r => r.Type == AccessLogType.Integrity && r.Text!.Contains("verify-all"));
    }

    [Fact]
    public async Task OcrRescan_AcceptsOnlyKnownScopes()
    {
        var k = NewKit();
        k.Text.Setup(t => t.RescanAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(4);
        k.Text.Setup(t => t.RescanNeedsOcrAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);

        Assert.Equal(4, Json(await k.Controller.OcrRescan("failed")).GetProperty("removed").GetInt32());
        Assert.Equal(2, Json(await k.Controller.OcrRescan("NeedsOcr")).GetProperty("removed").GetInt32());
        // "all" would drop every result: the page does not offer it, so it is not accepted.
        Assert.False(Json(await k.Controller.OcrRescan("all")).GetProperty("ok").GetBoolean());
        Assert.False(Json(await k.Controller.OcrRescan(null)).GetProperty("ok").GetBoolean());
        k.Text.Verify(t => t.RescanAsync(true, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BinRestore_PassesOnlyTheHash_AndReportsTheOutcome()
    {
        var k = NewKit();
        var sha = new string('c', 64);
        k.Bin.Setup(b => b.RestoreAsync(sha, "admin")).ReturnsAsync(new OriginalRestoreResult(true, "restored"));
        var j = Json(await k.Controller.BinRestore(sha));
        Assert.True(j.GetProperty("ok").GetBoolean());
        k.Bin.Verify(b => b.RestoreAsync(sha, "admin"), Times.Once);
    }

    // ------------------------------------------------------------------------------------ downloads

    [Theory]
    [InlineData("../etc/passwd", "json")]
    [InlineData("manifest-20261122T000000Z", "exe")]
    [InlineData("manifest-20261122T000000Z", null)]
    [InlineData(null, "json")]
    [InlineData("/etc/passwd", "key")]
    public async Task Download_OfAnythingElse_IsNotFound(string? name, string? kind)
    {
        var k = NewKit();
        k.Manifests.Setup(m => m.ResolveFile(It.IsAny<string?>(), It.IsAny<string?>())).Returns((string?)null);
        Assert.IsType<NotFoundResult>(await k.Controller.DownloadManifestFile(name, kind));
    }

    [Fact]
    public async Task Download_OfAManifestFile_IsServedAsAnAttachment_AndAccessLogged()
    {
        var k = NewKit();
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "manifest-20261122T000000Z.sig");
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 });
        k.Manifests.Setup(m => m.ResolveFile("manifest-20261122T000000Z", "sig")).Returns(file);

        var result = Assert.IsType<PhysicalFileResult>(await k.Controller.DownloadManifestFile("manifest-20261122T000000Z", "sig"));
        Assert.Equal("manifest-20261122T000000Z.sig", result.FileName.Split('/').Last());
        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Contains(k.Log.Rows, r => r.Type == AccessLogType.Integrity && r.Text!.Contains("downloaded"));
    }

    // ------------------------------------------------------------------------------------ the page

    [Fact]
    public async Task Index_BuildsEverySection()
    {
        var k = NewKit();
        k.Manifests.Setup(m => m.ListAsync(It.IsAny<int>())).ReturnsAsync(new[] { new ManifestInfo("manifest-20261122T000000Z", DateTime.UtcNow, 12, 5, new string('a', 64), "h") });
        k.Manifests.Setup(m => m.GetPublicKeyFingerprint()).Returns("fp123");

        var view = Assert.IsType<ViewResult>(await k.Controller.Index());
        var vm = Assert.IsType<IntegrityStorageViewModel>(view.Model);

        Assert.Empty(vm.Errors);
        Assert.Equal(SettingsCatalog.All.Count, vm.Settings.Count);
        Assert.Contains(vm.Settings, s => s.Key == "Storage:Mode" && s.Group == SettingGroup.Locked && !s.CanEdit);
        Assert.Contains(vm.Settings, s => s.Key == "Ocr:Mode" && s.Group == SettingGroup.Editable && s.CanEdit);
        Assert.Contains("seq=5", vm.AnchorLine);
        Assert.Contains("pubkey=fp123", vm.AnchorLine);
        Assert.Single(vm.Manifests);
        Assert.NotNull(vm.BinStatus);
        Assert.NotNull(vm.TextStatus);
    }

    [Fact]
    public async Task Index_OneBrokenSection_DoesNotBreakThePage()
    {
        var k = NewKit();
        k.Bin.Setup(b => b.GetStatusAsync()).ThrowsAsync(new InvalidOperationException("bin exploded"));
        k.Manifests.Setup(m => m.ListAsync(It.IsAny<int>())).ThrowsAsync(new IOException("disk gone"));

        var view = Assert.IsType<ViewResult>(await k.Controller.Index());
        var vm = Assert.IsType<IntegrityStorageViewModel>(view.Model);

        Assert.True(vm.Errors.ContainsKey("bin"));
        Assert.True(vm.Errors.ContainsKey("manifests"));
        Assert.False(vm.Errors.ContainsKey("settings"));
        Assert.NotEmpty(vm.Settings);
        Assert.NotNull(vm.TextStatus);
    }

    // ------------------------------------------------------------------------------------ manifest file names (the real service)

    private ManifestService NewManifestService()
    {
        var db = SettingsKit.NewContext(_fixture.ConnectionString);
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        var integrity = Opt.Create(new IntegrityOptions { ManifestPath = _dir });
        return new ManifestService(db, Mock.Of<IAuditChain>(), new AuditChainVerifier(db), integrity, env.Object, NullLogger<ManifestService>.Instance);
    }

    [Fact]
    public void ResolveFile_FindsOnlyWellNamedFilesOfTheRightKind()
    {
        Directory.CreateDirectory(_dir);
        foreach (var f in new[] { "manifest-20261122T000000Z.json", "manifest-20261122T000000Z.sig", "manifest-20261122T000000Z.jsonl", "public-key.pem", "secret.txt" })
            File.WriteAllText(Path.Combine(_dir, f), "x");
        var svc = NewManifestService();

        Assert.EndsWith("manifest-20261122T000000Z.json", svc.ResolveFile("manifest-20261122T000000Z", "json"));
        Assert.EndsWith("manifest-20261122T000000Z.sig", svc.ResolveFile("manifest-20261122T000000Z", "SIG"));
        Assert.EndsWith("manifest-20261122T000000Z.jsonl", svc.ResolveFile("manifest-20261122T000000Z", "jsonl"));
        Assert.EndsWith("public-key.pem", svc.ResolveFile(null, "key"));

        Assert.Null(svc.ResolveFile("../secret", "json"));
        Assert.Null(svc.ResolveFile("..\\secret", "json"));
        Assert.Null(svc.ResolveFile("manifest-20261122T000000Z\n", "json"));
        Assert.Null(svc.ResolveFile("manifest-2026", "json"));
        Assert.Null(svc.ResolveFile("secret", "txt"));
        Assert.Null(svc.ResolveFile("manifest-20261122T000000Z", "txt"));
        Assert.Null(svc.ResolveFile("manifest-20261122T000000Z", null));
        Assert.Null(svc.ResolveFile("manifest-20261122T000000Z", "../x"));
        Assert.Null(svc.ResolveFile(Path.Combine(_dir, "secret.txt"), "json"));
        Assert.Null(svc.ResolveFile("manifest-20990101T000000Z", "json"));   // well named, but there is no such file
    }

    [Fact]
    public void ResolveFile_DoesNotFollowASymbolicLink()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_dir);
        var outside = Path.Combine(Path.GetTempPath(), "isc-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "private");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_dir, "manifest-20261122T000000Z.json"), outside);
            Assert.Null(NewManifestService().ResolveFile("manifest-20261122T000000Z", "json"));
        }
        finally { File.Delete(outside); }
    }
}
