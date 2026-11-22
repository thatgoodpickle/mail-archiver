using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>An audit chain that records what is appended (or refuses to, like a chain whose write failed).</summary>
internal sealed class RecordingChain : IAuditChain
{
    public List<(string Kind, object? Payload)> Entries { get; } = new();
    public bool Fail { get; set; }
    public Func<Task>? BeforeAppend { get; set; }

    public async Task<AuditChainEntry?> AppendAsync(string kind, int? emailId = null, string? sha256 = null, object? payload = null,
        CancellationToken cancellationToken = default)
    {
        if (BeforeAppend != null) await BeforeAppend();
        if (Fail) return null;
        Entries.Add((kind, payload));
        return new AuditChainEntry { Kind = kind };
    }
}

/// <summary>An access log that records its rows, or throws like a database that is down.</summary>
internal sealed class RecordingAccessLog : IAccessLogService
{
    public List<(string User, AccessLogType Type, string? Text)> Rows { get; } = new();
    public bool Throw { get; set; }

    public Task LogAccessAsync(string username, AccessLogType type, int? emailId = null, string? emailSubject = null, string? emailFrom = null,
        string? searchParameters = null, int? mailAccountId = null)
    {
        if (Throw) throw new InvalidOperationException("access log is down");
        Rows.Add((username, type, searchParameters));
        return Task.CompletedTask;
    }

    public Task<List<AccessLog>> GetLogsForUserAsync(string username, int limit = 100) => Task.FromResult(new List<AccessLog>());
    public Task<List<AccessLog>> GetLogsForAdminAsync(int limit = 1000) => Task.FromResult(new List<AccessLog>());
    public Task<List<AccessLog>> GetLogsForUserAsync(string username, DateTime? fromDate = null, DateTime? toDate = null) => Task.FromResult(new List<AccessLog>());
    public Task<List<AccessLog>> GetLogsForAdminAsync(DateTime? fromDate = null, DateTime? toDate = null) => Task.FromResult(new List<AccessLog>());
}

/// <summary>
/// The settings of the Integrity and Storage page: precedence (environment variable, then database, then default), floors and
/// ceilings, locked settings, the password rule for risky changes, and the audit record that comes first.
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class AppSettingsTests : IAsyncLifetime
{
    private const string EnvPrefix = SettingsKit.EnvPrefix;
    private readonly TestDbFixture _fixture;
    private readonly List<string> _envSet = new();

    public AppSettingsTests(TestDbFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => await ClearAsync();

    public async Task DisposeAsync()
    {
        foreach (var name in _envSet) Environment.SetEnvironmentVariable(name, null);
        await ClearAsync();
    }

    private async Task ClearAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM mail_archiver.\"AppSettings\"");
    }

    private MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    /// <summary>Sets an environment variable that PINS a key (Ocr:MaxPages is set as MAS_PIN_Ocr__MaxPages and read with that prefix).</summary>
    private void Pin(string key, string value)
    {
        var name = EnvPrefix + key.Replace(":", "__");
        Environment.SetEnvironmentVariable(name, value);
        _envSet.Add(name);
    }

    private SettingsHarness NewHarness(Dictionary<string, string?>? extra = null, Action<Mock<IUserService>>? users = null, bool chainFails = false)
        => SettingsKit.Create(_fixture.ConnectionString, extra, users, chainFails);

    private async Task<string?> StoredAsync(string key)
    {
        await using var ctx = NewContext();
        return (await ctx.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key))?.Value;
    }

    // ------------------------------------------------------------------------------------------ precedence

    [Fact]
    public async Task Default_ThenDatabase_AppliesLive_AndSurvivesAReload()
    {
        var h = NewHarness();
        Assert.Equal(0, h.Mon<IntegrityOptions>().CurrentValue.ManifestIntervalHours);

        var r = await h.ChangeAsync("Integrity:ManifestIntervalHours", "24");
        Assert.True(r.Ok, r.Message);

        // The monitor sees it at once, without a restart.
        Assert.Equal(24, h.Mon<IntegrityOptions>().CurrentValue.ManifestIntervalHours);
        Assert.Equal("24", await StoredAsync("Integrity:ManifestIntervalHours"));
        var view = await h.ViewAsync("Integrity:ManifestIntervalHours");
        Assert.Equal("changed in this page", view.Source);
        Assert.Equal("admin", view.ChangedBy);

        // A new process: nothing in memory, the stored value comes back from the table.
        var restarted = NewHarness();
        await restarted.ReloadAsync();
        Assert.Equal(24, restarted.Mon<IntegrityOptions>().CurrentValue.ManifestIntervalHours);
    }

    [Fact]
    public async Task EnvironmentVariable_Pins_AndTheStoredValueIsIgnored()
    {
        Pin("Ocr:MaxPages", "7");

        // A stored value exists (written by an earlier run before the variable was set) ...
        await using (var ctx = NewContext())
        {
            ctx.AppSettings.Add(new AppSetting { Key = "Ocr:MaxPages", Value = "99", UpdatedBy = "earlier" });
            await ctx.SaveChangesAsync();
        }

        var h = NewHarness();
        await h.ReloadAsync();

        // ... but the variable wins.
        Assert.Equal(7, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
        var view = await h.ViewAsync("Ocr:MaxPages");
        Assert.True(view.Pinned);
        Assert.False(view.CanEdit);
        Assert.Equal("environment variable", view.Source);

        var r = await h.ChangeAsync("Ocr:MaxPages", "50");
        Assert.False(r.Ok);
        Assert.Contains("Ocr__MaxPages", r.Message);
        Assert.Equal(7, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
        Assert.Empty(h.Chain.Entries);
        Assert.Equal("99", await StoredAsync("Ocr:MaxPages"));   // untouched
    }

    [Fact]
    public async Task ValueFromAppsettings_DoesNotPin()
    {
        // appsettings.json values are defaults, not host pins: the page can still change them.
        var h = NewHarness(new Dictionary<string, string?> { ["Ocr:MaxPages"] = "12" });
        Assert.Equal(12, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
        Assert.False((await h.ViewAsync("Ocr:MaxPages")).Pinned);

        Assert.True((await h.ChangeAsync("Ocr:MaxPages", "30")).Ok);
        Assert.Equal(30, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
    }

    // ------------------------------------------------------------------------------------------ limits

    [Theory]
    [InlineData("RawMessages:OriginalCleanup:BinRetentionDays", "3")]        // below the floor of 7
    [InlineData("RawMessages:OriginalCleanup:BinRetentionDays", "3651")]
    [InlineData("RawMessages:OriginalCleanup:BinRetentionDays", "abc")]
    [InlineData("RawMessages:OriginalCleanup:IntervalHours", "0")]
    [InlineData("RawMessages:OriginalCleanup:MaxFilesPerRun", "0")]
    [InlineData("Ocr:Mode", "turbo")]
    [InlineData("Ocr:MaxPages", "0")]
    [InlineData("Ocr:Threads", "17")]
    [InlineData("Ocr:TimeoutSecondsPerImage", "601")]
    [InlineData("Integrity:VerifyAllIntervalHours", "-1")]
    [InlineData("Integrity:ManifestIntervalHours", "8761")]
    [InlineData("RawMessages:OriginalCleanup:Enabled", "maybe")]
    public async Task OutOfRangeOrMalformedValues_AreRefused_AndNothingIsRecorded(string key, string value)
    {
        var h = NewHarness();
        var r = await h.ChangeAsync(key, value, "secret");
        Assert.False(r.Ok);
        Assert.Null(await StoredAsync(key));
        Assert.Empty(h.Chain.Entries);
        Assert.Empty(h.Log.Rows);
    }

    [Fact]
    public async Task ValidValues_AtTheEdges_AreAccepted()
    {
        var h = NewHarness();
        Assert.False((await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "7")).Ok);   // 30 -> 7 is a shortening: needs the password
        Assert.True((await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "7", "secret")).Ok);
        Assert.Equal(7, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
        Assert.True((await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "3650")).Ok);
        Assert.Equal(3650, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
        Assert.True((await h.ChangeAsync("Ocr:Threads", "16")).Ok);
        Assert.Equal(16, h.Mon<AttachmentTextOptions>().CurrentValue.Threads);
    }

    [Fact]
    public async Task EnvironmentVariable_MayStillSetWhatThePageForbids()
    {
        // The page refuses less than 7 days; the host (environment variable) may set 1, and the page then only shows it.
        Pin("RawMessages:OriginalCleanup:BinRetentionDays", "1");
        var h = NewHarness();
        Assert.Equal(1, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
        Assert.True((await h.ViewAsync("RawMessages:OriginalCleanup:BinRetentionDays")).Pinned);
    }

    // ------------------------------------------------------------------------------------------ locked

    [Fact]
    public async Task LockedSettings_CannotBeChanged_OrReset_AndNothingIsRecorded()
    {
        var h = NewHarness();
        var locked = SettingsCatalog.All.Where(d => d.Group == SettingGroup.Locked).ToList();
        Assert.True(locked.Count >= 12);
        Assert.Contains(locked, d => d.Key == "Storage:Mode");
        Assert.Contains(locked, d => d.Key == "RawMessages:Required");
        Assert.Contains(locked, d => d.Key == "Integrity:AuditChainEnabled");

        foreach (var d in locked)
        {
            var r = await h.ChangeAsync(d.Key, "anything", "secret");
            Assert.False(r.Ok);
            Assert.Contains("locked", r.Message);
            Assert.Contains(d.EnvVar, r.Message);
            Assert.False((await h.ChangeAsync(d.Key, null, "secret", reset: true)).Ok);
            Assert.Null(await StoredAsync(d.Key));
        }
        Assert.False((await h.ChangeAsync("No:SuchSetting", "1")).Ok);
        Assert.Empty(h.Chain.Entries);
        Assert.Empty(h.Log.Rows);
    }

    [Fact]
    public void Catalog_EditableSettings_HaveAnApplyAndATarget_LockedOnesDoNot_AndEveryDefaultIsValid()
    {
        foreach (var d in SettingsCatalog.All)
        {
            Assert.Equal(d.Key.Replace(":", "__"), d.EnvVar);
            if (d.Group == SettingGroup.Editable)
            {
                Assert.NotNull(d.Apply);
                Assert.NotNull(d.Target);
                var def = SettingsCatalog.Default(d);
                Assert.True(SettingsCatalog.TryValidate(d, def, out _, out var why), $"{d.Key}: default '{def}' is refused: {why}");
            }
            else
            {
                Assert.Null(d.Apply);
                Assert.Null(d.Target);
            }
        }
    }

    // ------------------------------------------------------------------------------------------ audit first

    [Fact]
    public async Task EveryChange_IsChainedAndAccessLogged_BeforeItIsSaved()
    {
        var h = NewHarness();
        string? storedWhenChained = "not checked";
        h.Chain.BeforeAppend = async () => storedWhenChained = await StoredAsync("Ocr:MaxPages");

        var r = await h.ChangeAsync("Ocr:MaxPages", "33");
        Assert.True(r.Ok, r.Message);

        Assert.Null(storedWhenChained);   // the record was written while the row did not exist yet
        var entry = Assert.Single(h.Chain.Entries);
        Assert.Equal("setting.changed", entry.Kind);
        var json = System.Text.Json.JsonSerializer.Serialize(entry.Payload);
        Assert.Contains("Ocr:MaxPages", json);
        Assert.Contains("\"old\":\"20\"", json);
        Assert.Contains("\"new\":\"33\"", json);
        Assert.Contains("admin", json);

        var row = Assert.Single(h.Log.Rows);
        Assert.Equal(AccessLogType.Integrity, row.Type);
        Assert.Contains("Ocr:MaxPages: 20 -> 33", row.Text);
    }

    [Fact]
    public async Task WhenTheChainEntryCannotBeWritten_TheChangeIsNotApplied()
    {
        var h = NewHarness(chainFails: true);
        var r = await h.ChangeAsync("Ocr:MaxPages", "33");

        Assert.False(r.Ok);
        Assert.Contains("audit chain", r.Message);
        Assert.Null(await StoredAsync("Ocr:MaxPages"));
        Assert.Equal(20, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
        Assert.Empty(h.Log.Rows);
    }

    [Fact]
    public async Task WhenTheChainIsSwitchedOff_TheAccessLogAloneIsRequired()
    {
        var h = NewHarness(new Dictionary<string, string?> { ["Integrity:AuditChainEnabled"] = "false" }, chainFails: true);
        var ok = await h.ChangeAsync("Ocr:MaxPages", "33");
        Assert.True(ok.Ok, ok.Message);
        Assert.Single(h.Log.Rows);

        h.Log.Throw = true;
        var refused = await h.ChangeAsync("Ocr:MaxPages", "34");
        Assert.False(refused.Ok);
        Assert.Equal("33", await StoredAsync("Ocr:MaxPages"));
    }

    [Fact]
    public async Task WhenTheAccessLogFails_TheChangeIsNotApplied_AndTheFailureIsChained()
    {
        var h = NewHarness();
        h.Log.Throw = true;
        var r = await h.ChangeAsync("Ocr:MaxPages", "33");

        Assert.False(r.Ok);
        Assert.Null(await StoredAsync("Ocr:MaxPages"));
        Assert.Equal(20, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
        Assert.Contains(h.Chain.Entries, e => e.Kind == "setting.change.failed");
    }

    [Fact]
    public async Task ChangingToTheCurrentValue_IsANoOp_WithNoRecord()
    {
        var h = NewHarness();
        var r = await h.ChangeAsync("Ocr:MaxPages", "20");   // the default
        Assert.True(r.Ok);
        Assert.Empty(h.Chain.Entries);
        Assert.Empty(h.Log.Rows);
        Assert.Null(await StoredAsync("Ocr:MaxPages"));
    }

    // ------------------------------------------------------------------------------------------ risky changes need the password

    [Fact]
    public async Task ShorteningTheBinRetention_NeedsThePassword()
    {
        var h = NewHarness();
        var none = await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "10");
        Assert.False(none.Ok);
        Assert.True(none.NeedsPassword);

        var wrong = await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "10", "nope", userId: 41);
        Assert.False(wrong.Ok);
        Assert.Contains("not correct", wrong.Message);
        Assert.Equal(30, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
        Assert.Empty(h.Chain.Entries);

        var right = await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "10", "secret", userId: 41);
        Assert.True(right.Ok, right.Message);
        Assert.Equal(10, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
    }

    [Fact]
    public async Task LengtheningTheRetention_NeedsNoPassword()
    {
        var h = NewHarness();
        var r = await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "60");
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public async Task SwitchingTheBinOff_NeedsThePassword_ButSwitchingItOnDoesNot()
    {
        var h = NewHarness();
        var off = await h.ChangeAsync("RawMessages:OriginalCleanup:Enabled", "false");
        Assert.False(off.Ok);
        Assert.True(off.NeedsPassword);
        Assert.True((await h.ChangeAsync("RawMessages:OriginalCleanup:Enabled", "false", "secret", userId: 42)).Ok);
        Assert.False(h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.Enabled);
        Assert.True((await h.ChangeAsync("RawMessages:OriginalCleanup:Enabled", "true")).Ok);
    }

    [Fact]
    public async Task SwitchingTheOcrMode_NeedsThePassword()
    {
        var h = NewHarness();
        var r = await h.ChangeAsync("Ocr:Mode", "cpu");
        Assert.False(r.Ok);
        Assert.True(r.NeedsPassword);
        Assert.True((await h.ChangeAsync("Ocr:Mode", "cpu", "secret", userId: 43)).Ok);
        Assert.True(h.Mon<AttachmentTextOptions>().CurrentValue.UsesOcr);
    }

    [Fact]
    public async Task SwitchingAScheduleOff_NeedsThePassword_ButChangingItsLengthDoesNot()
    {
        var h = NewHarness();
        Assert.True((await h.ChangeAsync("Integrity:VerifyAllIntervalHours", "24")).Ok);   // off -> on: not risky
        Assert.True((await h.ChangeAsync("Integrity:VerifyAllIntervalHours", "6")).Ok);    // shorter: not risky
        Assert.True((await h.ChangeAsync("Integrity:VerifyAllIntervalHours", "48")).Ok);   // longer: not risky
        var off = await h.ChangeAsync("Integrity:VerifyAllIntervalHours", "0");
        Assert.False(off.Ok);
        Assert.True(off.NeedsPassword);
        Assert.True((await h.ChangeAsync("Integrity:VerifyAllIntervalHours", "0", "secret", userId: 44)).Ok);
    }

    [Theory]
    [InlineData(null, null)]          // no password at all
    [InlineData(null, "oidc-subject")] // a single-sign-on account
    [InlineData("", null)]
    public async Task AnAccountWithoutALocalPassword_CannotConfirm_AndIsToldToUseTheEnvironmentVariable(string? hash, string? oidc)
    {
        var h = NewHarness(users: m => m.Setup(u => u.GetUserByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => new User { Id = id, Username = "sso", PasswordHash = hash, OAuthRemoteUserId = oidc }));

        var r = await h.ChangeAsync("RawMessages:OriginalCleanup:Enabled", "false", "secret");
        Assert.False(r.Ok);
        Assert.Contains("single sign-on", r.Message);
        Assert.Contains("RawMessages__OriginalCleanup__Enabled", r.Message);
        Assert.True(h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.Enabled);
        Assert.Empty(h.Chain.Entries);
    }

    [Fact]
    public async Task AnUnknownActor_CannotConfirmARiskyChange()
    {
        var h = NewHarness(users: m => m.Setup(u => u.GetUserByIdAsync(It.IsAny<int>())).ReturnsAsync((User?)null));
        var r = await h.ChangeAsync("Ocr:Mode", "cpu", "secret");
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task TooManyWrongPasswords_LockTheConfirmationForAWhile()
    {
        SettingsService.ResetThrottle();
        var h = NewHarness();
        for (var i = 0; i < 5; i++)
            Assert.Contains("not correct", (await h.ChangeAsync("Ocr:Mode", "cpu", "wrong", userId: 77)).Message);

        // Even the right password is refused now.
        var locked = await h.ChangeAsync("Ocr:Mode", "cpu", "secret", userId: 77);
        Assert.False(locked.Ok);
        Assert.Contains("Too many", locked.Message);

        // Another admin is not affected.
        Assert.True((await h.ChangeAsync("Ocr:Mode", "cpu", "secret", userId: 78)).Ok);
        SettingsService.ResetThrottle();
    }

    // ------------------------------------------------------------------------------------------ reset

    [Fact]
    public async Task Reset_RestoresTheDefault_RemovesTheRow_AndIsRecorded()
    {
        var h = NewHarness();
        Assert.True((await h.ChangeAsync("Ocr:MaxPages", "50")).Ok);
        Assert.Equal(50, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);

        var r = await h.ChangeAsync("Ocr:MaxPages", null, reset: true);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(20, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
        Assert.Null(await StoredAsync("Ocr:MaxPages"));
        Assert.Equal("default", (await h.ViewAsync("Ocr:MaxPages")).Source);
        Assert.Equal(2, h.Chain.Entries.Count(e => e.Kind == "setting.changed"));
        Assert.Contains("reset to default", h.Log.Rows.Last().Text);
    }

    [Fact]
    public async Task Reset_ThatLowersAProtection_NeedsThePassword()
    {
        var h = NewHarness();
        Assert.True((await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", "90")).Ok);
        // 90 -> default 30 is a shortening.
        var r = await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", null, reset: true);
        Assert.False(r.Ok);
        Assert.True(r.NeedsPassword);
        Assert.True((await h.ChangeAsync("RawMessages:OriginalCleanup:BinRetentionDays", null, "secret", reset: true, userId: 55)).Ok);
    }

    // ------------------------------------------------------------------------------------------ start-up safety

    [Fact]
    public async Task BadStoredValues_AreIgnored_AndNeverPreventStartup()
    {
        await using (var ctx = NewContext())
        {
            ctx.AppSettings.AddRange(
                new AppSetting { Key = "Ocr:MaxPages", Value = "banana" },
                new AppSetting { Key = "RawMessages:OriginalCleanup:BinRetentionDays", Value = "1" },   // below the page's floor
                new AppSetting { Key = "Ocr:Mode", Value = "turbo" },
                new AppSetting { Key = "Storage:Mode", Value = "Lean" },                                 // a locked key can never come from the table
                new AppSetting { Key = "Integrity:ManifestIntervalHours", Value = "12" });               // this one is fine
            await ctx.SaveChangesAsync();
        }

        var h = NewHarness();
        await h.ReloadAsync();

        Assert.Equal(20, h.Mon<AttachmentTextOptions>().CurrentValue.MaxPages);
        Assert.Equal(30, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
        Assert.Equal("off", h.Mon<AttachmentTextOptions>().CurrentValue.Mode);
        Assert.Equal(12, h.Mon<IntegrityOptions>().CurrentValue.ManifestIntervalHours);
        Assert.Equal("Standard", h.Mon<StorageModeOptions>().CurrentValue.Mode);
    }

    [Fact]
    public async Task LoadingFromAnUnreachableDatabase_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<ISettingsSnapshot, SettingsSnapshot>();
        services.AddDbContext<MailArchiverDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1;Command Timeout=1")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        using var sp = services.BuildServiceProvider();

        await SettingsLoader.LoadAsync(sp, NullLogger.Instance);   // must complete without an exception

        Assert.Empty(sp.GetRequiredService<ISettingsSnapshot>().All);
    }

    // ------------------------------------------------------------------------------------------ the page's single Save

    [Fact]
    public async Task HasLocalPassword_IsTrueForAPasswordAccount_FalseForSingleSignOn()
    {
        Assert.True(await NewHarness().Sp.CreateScope().ServiceProvider.GetRequiredService<ISettingsService>().HasLocalPasswordAsync(new SettingActor(1, "admin")));

        var sso = NewHarness(users: m => m.Setup(u => u.GetUserByIdAsync(It.IsAny<int>()))
            .ReturnsAsync(new User { Id = 1, Username = "admin", PasswordHash = null, OAuthRemoteUserId = "sub" }));
        Assert.False(await sso.Sp.CreateScope().ServiceProvider.GetRequiredService<ISettingsService>().HasLocalPasswordAsync(new SettingActor(1, "admin")));
    }

    [Fact]
    public async Task ChangeMany_NothingToChange_IsASuccess_AndWritesNothing()
    {
        var h = NewHarness();
        using var scope = h.Sp.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<ISettingsService>().ChangeManyAsync(
            new[] { new SettingChange("Ocr:MaxPages", "20") }, new SettingActor(1, "admin"), null);   // 20 is the standard value
        Assert.True(r.Applied);
        Assert.Empty(r.RowErrors);
        Assert.Empty(h.Chain.Entries);
        Assert.Null(await StoredAsync("Ocr:MaxPages"));
    }

    [Fact]
    public async Task ChangeMany_EveryBadRowIsReported_AndNothingIsApplied()
    {
        var h = NewHarness();
        using var scope = h.Sp.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<ISettingsService>().ChangeManyAsync(new[]
        {
            new SettingChange("Ocr:MaxPages", "44"), new SettingChange("Ocr:Threads", "0"), new SettingChange("Ocr:Mode", "turbo"),
            new SettingChange("Storage:Mode", "Lean"), new SettingChange("No:SuchSetting", "1")
        }, new SettingActor(1, "admin"), "secret");

        Assert.False(r.Applied);
        Assert.Equal(4, r.RowErrors.Count);
        Assert.False(r.RowErrors.ContainsKey("Ocr:MaxPages"));
        Assert.Null(await StoredAsync("Ocr:MaxPages"));
        Assert.Empty(h.Chain.Entries);
    }

    // ------------------------------------------------------------------------------------------ wording and the password rule

    [Fact]
    public void Catalog_EveryRiskyEditableSetting_SaysWhenThePasswordIsAsked_AndTheClientRuleMatchesTheServerRule()
    {
        foreach (var d in SettingsCatalog.Editable)
        {
            if (d.Risky == null) { Assert.Null(d.PasswordRule); continue; }
            Assert.False(string.IsNullOrEmpty(d.RiskyNote), d.Key);
            Assert.False(string.IsNullOrEmpty(d.PasswordRule), d.Key);

            // What the page's script decides for the dialog must be what the server decides.
            switch (d.PasswordRule)
            {
                case "off": Assert.True(d.Risky("true", "false")); Assert.False(d.Risky("false", "true")); break;
                case "lower": Assert.True(d.Risky("30", "7")); Assert.False(d.Risky("30", "60")); Assert.False(d.Risky("30", "30")); break;
                case "zero": Assert.True(d.Risky("24", "0")); Assert.False(d.Risky("24", "12")); Assert.False(d.Risky("0", "0")); break;
                case "change": Assert.True(d.Risky("cpu", "off")); Assert.False(d.Risky("cpu", "cpu")); break;
                default: Assert.Fail($"{d.Key}: unknown rule {d.PasswordRule}"); break;
            }
        }
    }

    [Fact]
    public void Catalog_NoVisibleTextSaysBin_ItIsCalledTheTrash()
    {
        foreach (var d in SettingsCatalog.All)
            foreach (var text in new[] { d.Label, d.Help, d.RiskyNote, d.RestartNote })
                Assert.DoesNotMatch(@"\b[Bb]in\b", text ?? string.Empty);
        Assert.Contains(SettingsCatalog.All, d => d.Label.Contains("Trash"));
    }
}

/// <summary>The background services read their settings again on every tick, so a change on the page needs no restart.</summary>
public class LiveSettingsTests
{
    private static IntegrityScheduleService NewSchedule(IIntegrityRunner runner, IOptionsMonitor<IntegrityOptions> monitor, IServiceScopeFactory? scopes = null)
        => new(runner, monitor, NullLogger<IntegrityScheduleService>.Instance, scopes);

    [Fact]
    public async Task VerifySchedule_FollowsTheCurrentInterval()
    {
        var runner = new Mock<IIntegrityRunner>();
        runner.Setup(r => r.StartAsync(It.IsAny<string?>())).ReturnsAsync(new IntegrityRun());
        var monitor = new MutableOptionsMonitor<IntegrityOptions>(new IntegrityOptions { VerifyAllIntervalHours = 0 });
        var svc = NewSchedule(runner.Object, monitor);
        var t0 = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);

        await svc.TickAsync(t0, CancellationToken.None);
        runner.Verify(r => r.StartAsync(It.IsAny<string?>()), Times.Never);        // off

        monitor.CurrentValue = new IntegrityOptions { VerifyAllIntervalHours = 1 };   // switched on on the page
        await svc.TickAsync(t0.AddSeconds(30), CancellationToken.None);
        runner.Verify(r => r.StartAsync("schedule"), Times.Once);                    // picked up at the next tick

        await svc.TickAsync(t0.AddMinutes(30), CancellationToken.None);
        runner.Verify(r => r.StartAsync(It.IsAny<string?>()), Times.Once);           // not yet due

        await svc.TickAsync(t0.AddMinutes(61), CancellationToken.None);
        runner.Verify(r => r.StartAsync(It.IsAny<string?>()), Times.Exactly(2));     // due again

        monitor.CurrentValue = new IntegrityOptions { VerifyAllIntervalHours = 0 };   // switched off
        await svc.TickAsync(t0.AddHours(5), CancellationToken.None);
        runner.Verify(r => r.StartAsync(It.IsAny<string?>()), Times.Exactly(2));

        monitor.CurrentValue = new IntegrityOptions { VerifyAllIntervalHours = 2 };   // on again: runs at the next tick
        await svc.TickAsync(t0.AddHours(5).AddSeconds(30), CancellationToken.None);
        runner.Verify(r => r.StartAsync(It.IsAny<string?>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ManifestSchedule_FollowsTheCurrentInterval()
    {
        var manifests = new Mock<IManifestService>();
        manifests.Setup(m => m.CreateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManifestInfo("manifest-20261122T000000Z", DateTime.UtcNow, 0, null, null, "x"));
        var services = new ServiceCollection();
        services.AddScoped(_ => manifests.Object);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var monitor = new MutableOptionsMonitor<IntegrityOptions>(new IntegrityOptions());
        var svc = NewSchedule(Mock.Of<IIntegrityRunner>(), monitor, scopes);
        var t0 = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);

        await svc.TickAsync(t0, CancellationToken.None);
        manifests.Verify(m => m.CreateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        monitor.CurrentValue = new IntegrityOptions { ManifestIntervalHours = 24 };
        await svc.TickAsync(t0.AddSeconds(30), CancellationToken.None);
        manifests.Verify(m => m.CreateAsync("schedule", It.IsAny<CancellationToken>()), Times.Once);
        await svc.TickAsync(t0.AddHours(23), CancellationToken.None);
        manifests.Verify(m => m.CreateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        await svc.TickAsync(t0.AddHours(25), CancellationToken.None);
        manifests.Verify(m => m.CreateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static RawMessageOptions ApplicableRaw(Action<OriginalCleanupOptions>? tweak = null)
    {
        var raw = new RawMessageOptions { Enabled = true, Storage = RawMessageStorage.FileSystem, FileSystem = new RawMessageFileSystemOptions { HashNamed = true } };
        tweak?.Invoke(raw.OriginalCleanup);
        return raw;
    }

    [Fact]
    public async Task BinSchedule_FollowsTheCurrentSettings()
    {
        var bin = new Mock<IOriginalBin>();
        bin.Setup(b => b.RunOnceAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new OriginalCleanupRun());
        var monitor = new MutableOptionsMonitor<RawMessageOptions>(ApplicableRaw(o => o.Enabled = false));
        var svc = new OriginalBinService(bin.Object, monitor, NullLogger<OriginalBinService>.Instance);
        var t0 = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc);

        await svc.TickAsync(t0, CancellationToken.None);
        bin.Verify(b => b.RunOnceAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);   // off

        monitor.CurrentValue = ApplicableRaw(o => { o.Enabled = true; o.IntervalHours = 2; });             // switched on on the page
        await svc.TickAsync(t0.AddSeconds(30), CancellationToken.None);
        bin.Verify(b => b.RunOnceAsync("schedule", It.IsAny<CancellationToken>()), Times.Once);

        await svc.TickAsync(t0.AddHours(1), CancellationToken.None);
        bin.Verify(b => b.RunOnceAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);   // 2 hours not over

        monitor.CurrentValue = ApplicableRaw(o => { o.Enabled = true; o.IntervalHours = 1; });             // shortened on the page
        await svc.TickAsync(t0.AddHours(1).AddMinutes(1), CancellationToken.None);
        bin.Verify(b => b.RunOnceAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        monitor.CurrentValue = ApplicableRaw(o => o.Enabled = false);                                        // switched off again
        await svc.TickAsync(t0.AddDays(2), CancellationToken.None);
        bin.Verify(b => b.RunOnceAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Bin_StaysIdle_WhenItDoesNotApplyToTheArchive()
    {
        var bin = new Mock<IOriginalBin>();
        var raw = new RawMessageOptions { Enabled = true };   // postgres storage: not applicable
        var svc = new OriginalBinService(bin.Object, new MutableOptionsMonitor<RawMessageOptions>(raw), NullLogger<OriginalBinService>.Instance);
        await svc.TickAsync(DateTime.UtcNow, CancellationToken.None);
        bin.Verify(b => b.RunOnceAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AttachmentTextWorker_FollowsTheCurrentMode()
    {
        var processor = new Mock<IAttachmentTextProcessor>();
        processor.Setup(p => p.ProcessBatchAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        var services = new ServiceCollection();
        services.AddScoped(_ => processor.Object);
        var monitor = new MutableOptionsMonitor<AttachmentTextOptions>(new AttachmentTextOptions { Mode = "off" });
        var worker = new AttachmentTextWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), monitor,
            NullLogger<AttachmentTextWorker>.Instance);

        Assert.False(await worker.RoundAsync(CancellationToken.None));
        processor.Verify(p => p.ProcessBatchAsync(It.IsAny<CancellationToken>()), Times.Never);

        monitor.CurrentValue = new AttachmentTextOptions { Mode = "cpu" };                 // switched on on the page
        Assert.True(await worker.RoundAsync(CancellationToken.None));
        processor.Verify(p => p.ProcessBatchAsync(It.IsAny<CancellationToken>()), Times.Once);

        monitor.CurrentValue = new AttachmentTextOptions { Mode = "off" };                 // and off again
        Assert.False(await worker.RoundAsync(CancellationToken.None));
        processor.Verify(p => p.ProcessBatchAsync(It.IsAny<CancellationToken>()), Times.Once);

        monitor.CurrentValue = new AttachmentTextOptions { Mode = "nonsense" };             // an invalid mode keeps it off
        Assert.False(await worker.RoundAsync(CancellationToken.None));
        processor.Verify(p => p.ProcessBatchAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

}
