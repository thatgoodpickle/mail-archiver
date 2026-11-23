using System.Text.Json;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Core;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Moq;
using Npgsql;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Second review N-04, N-05, N-09, N-13: what "Valid" now covers.</summary>
[Collection(TestDbFixture.CollectionName)]
public class IntegrityReviewTwoTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "irt-" + Guid.NewGuid().ToString("N"));
    private readonly List<MailArchiverDbContext> _ctxs = new();
    public IntegrityReviewTwoTests(TestDbFixture fixture) => _fixture = fixture;

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(_fixture.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task ResetChainAsync() => ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER; DELETE FROM mail_archiver.""AuditChain""; ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;");

    public Task InitializeAsync() => ResetChainAsync();

    public async Task DisposeAsync()
    {
        foreach (var c in _ctxs) await c.DisposeAsync();
        await ResetChainAsync();
        try { Directory.Delete(_root, true); } catch { }
    }

    private MailArchiverDbContext NewContext()
    {
        var c = new MailArchiverDbContext(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);
        _ctxs.Add(c);
        return c;
    }

    private (EmailCoreService Svc, Func<DateTimeHelper?, IntegrityService> Integrity) Make(MailArchiverDbContext ctx, bool lean)
    {
        var raw = new RawMessageOptions
        {
            Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
            FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = lean }
        };
        var opts = Opt.Create(raw);
        var paths = new RawMessagePathResolver(raw.FileSystem, _root);
        var fs = new FileSystemRawMessageStore(ctx, opts, paths, NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
        var svc = new EmailCoreService(ctx, NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Opt.Create(new BatchOperationOptions()), Opt.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null, rawMessageOptions: opts, rawMessageStore: router,
            contentHydrator: new StrippedContentHydrator(ctx, new OriginalMessageReader(router)),
            storageMode: Opt.Create(new StorageModeOptions { Mode = lean ? "Lean" : "Standard" }),
            leanIngest: new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance));
        return (svc, helper => new IntegrityService(ctx, router, helper));
    }

    private static DateTimeHelper Zone(string id) => new(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = id }));

    private async Task<MailAccount> AccountAsync(MailArchiverDbContext ctx)
    {
        var a = new MailAccount { Name = $"r2-{Guid.NewGuid():N}".Substring(0, 18), EmailAddress = $"{Guid.NewGuid():N}@test.local", Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow };
        ctx.MailAccounts.Add(a); await ctx.SaveChangesAsync();
        return a;
    }

    private async Task CleanupAsync(MailArchiverDbContext ctx, MailAccount a)
    {
        ctx.ChangeTracker.Clear();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {a.Id}");
        await ctx.ArchivedEmails.Where(e => e.MailAccountId == a.Id).ExecuteDeleteAsync();
        await ctx.MailAccounts.Where(x => x.Id == a.Id).ExecuteDeleteAsync();
    }

    private static (MimeMessage Message, byte[] Bytes) Build(Func<MimeMessage, MimeEntity> body, DateTimeOffset? date = null)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "from@example.test")); m.To.Add(new MailboxAddress("", "to@example.test"));
        m.Subject = "r2 " + Guid.NewGuid().ToString("N"); m.MessageId = $"{Guid.NewGuid():N}@example.test";
        m.Date = date ?? new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        m.Body = body(m);
        using var ms = new MemoryStream(); m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        return (MimeMessage.Load(read), bytes);
    }

    private static MimeEntity TextAndPdf(MimeMessage _)
    {
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "some text" } };
        mixed.Add(new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(Enumerable.Range(0, 2000).Select(i => (byte)(i * 7 + 1)).ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "a.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64
        });
        return mixed;
    }

    private async Task<int> ArchiveAsync(MailArchiverDbContext ctx, MailAccount a, bool lean, (MimeMessage Message, byte[] Bytes) mail)
    {
        var (svc, _) = Make(ctx, lean);
        Assert.True(await svc.ArchiveEmailAsync(a, mail.Message, false, "INBOX", new RawMessageCapture(mail.Bytes, RawMessageSource.Imap)));
        ctx.ChangeTracker.Clear();
        return await ctx.ArchivedEmails.Where(e => e.MailAccountId == a.Id).Select(e => e.Id).SingleAsync();
    }

    // ---- N-04 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AForgedCalendarTypedRow_IsNoLongerSkipped()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var id = await ArchiveAsync(ctx, a, lean: false, Build(TextAndPdf));
            var (_, integrity) = Make(ctx, false);
            Assert.Equal(IntegrityStatus.Valid, (await integrity(null).VerifyAsync(id)).Status);

            ctx.EmailAttachments.Add(new EmailAttachment { ArchivedEmailId = id, FileName = "invoice.pdf", ContentType = "text/calendar; x", Size = 5, Content = new byte[] { 1, 2, 3, 4, 5 } });
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();

            var report = await integrity(null).VerifyAsync(id);
            Assert.NotEqual(IntegrityStatus.Valid, report.Status);
            Assert.Contains(report.Findings, f => f.Contains("invoice.pdf"));
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task ARealAttachmentRetypedAsCalendar_IsAFinding()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var id = await ArchiveAsync(ctx, a, lean: false, Build(TextAndPdf));
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"EmailAttachments\" SET \"ContentType\" = 'text/calendar' WHERE \"ArchivedEmailId\" = {id}");
            ctx.ChangeTracker.Clear();
            Assert.NotEqual(IntegrityStatus.Valid, (await Make(ctx, false).Integrity(null).VerifyAsync(id)).Status);
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task AGenuineSynthesizedCalendarAttachment_StaysValid()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var mail = Build(_ =>
            {
                var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "meeting" } };
                mixed.Add(new TextPart("calendar") { Text = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nSUMMARY:Board meeting\r\nDTSTART:20260601T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n" });
                return mixed;
            });
            var id = await ArchiveAsync(ctx, a, lean: true, mail);
            var report = await Make(ctx, true).Integrity(null).VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Valid, report.Status);
        }
        finally { await CleanupAsync(ctx, a); }
    }

    // ---- N-05 ---------------------------------------------------------------------------------------------

    private async Task AppendStoredEntryAsync()
    {
        var services = new ServiceCollection(); services.AddScoped(_ => NewContext());
        var chain = new AuditChain(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), Opt.Create(new IntegrityOptions()), NullLogger<AuditChain>.Instance);
        await chain.AppendAsync("original.stored", 999999, new string('a', 64), new { test = true });
    }

    [Fact]
    public async Task AnEmailArchivedAfterTheChainBegan_WithoutAnEntry_IsAFinding_NotValid()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            await AppendStoredEntryAsync();                       // the chain is in use from here on
            await Task.Delay(50);
            var id = await ArchiveAsync(ctx, a, lean: true, Build(TextAndPdf));   // this harness has no chain interceptor: a lost chain write

            var report = await Make(ctx, true).Integrity(null).VerifyAsync(id);

            Assert.Equal(IntegrityStatus.ChainMismatch, report.Status);
            Assert.Equal(IntegrityChainState.Gap, report.ChainState);
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task AnEmailArchivedBeforeTheChainBegan_IsStillJustUnchained()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var id = await ArchiveAsync(ctx, a, lean: true, Build(TextAndPdf));
            await Task.Delay(50);
            await AppendStoredEntryAsync();                       // the chain only starts after this email

            var report = await Make(ctx, true).Integrity(null).VerifyAsync(id);

            Assert.Equal(IntegrityStatus.Valid, report.Status);
            Assert.Equal(IntegrityChainState.Unchained, report.ChainState);
        }
        finally { await CleanupAsync(ctx, a); }
    }

    // ---- N-09 ---------------------------------------------------------------------------------------------

    private async Task<List<int>> ArchiveManyAsync(MailArchiverDbContext ctx, MailAccount a, int n)
    {
        var (svc, _) = Make(ctx, false);
        for (var i = 0; i < n; i++)
        {
            // January and June dates: a zone change then differs by two offsets (daylight saving).
            var date = new DateTimeOffset(2026, i % 2 == 0 ? 1 : 6, 1 + i, 12, 0, 0, TimeSpan.Zero);
            var mail = Build(TextAndPdf, date);
            Assert.True(await svc.ArchiveEmailAsync(a, mail.Message, false, "INBOX", new RawMessageCapture(mail.Bytes, RawMessageSource.Imap)));
        }
        ctx.ChangeTracker.Clear();
        return await ctx.ArchivedEmails.Where(e => e.MailAccountId == a.Id).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
    }

    private Task ShiftAsync(MailArchiverDbContext ctx, int id, string interval) =>
        ctx.Database.ExecuteSqlRawAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"SentDate\" = \"SentDate\" + interval '{interval}' WHERE \"Id\" = {id}");

    [Fact]
    public async Task ChangingTheDisplayTimeZone_OfTheWholeArchive_IsNotAFinding()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var ids = await ArchiveManyAsync(ctx, a, 12);                                   // archived under Europe/Berlin
            var (_, integrity) = Make(ctx, false);
            Assert.All(ids, id => Assert.Equal(IntegrityStatus.Valid, integrity(Zone("Europe/Berlin")).VerifyAsync(id).Result.Status));

            var svc = integrity(Zone("UTC"));                                                // the zone setting changed afterwards
            foreach (var id in ids)
            {
                var moved = await svc.VerifyAsync(id);
                Assert.Equal(IntegrityStatus.Valid, moved.Status);
                Assert.Contains(moved.NotCompared, n => n.Contains("time-zone"));
            }
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Theory]
    [InlineData("1 hour")]
    [InlineData("24 hours")]
    [InlineData("25 hours 45 minutes")]
    [InlineData("7 minutes")]
    public async Task ASingleShiftedSentDate_IsAFinding_EvenWhenItIsAWholeQuarterHour(string interval)
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var ids = await ArchiveManyAsync(ctx, a, 12);
            var (_, integrity) = Make(ctx, false);
            await ShiftAsync(ctx, ids[3], interval);
            ctx.ChangeTracker.Clear();

            var forged = await integrity(Zone("Europe/Berlin")).VerifyAsync(ids[3]);
            Assert.NotEqual(IntegrityStatus.Valid, forged.Status);
            Assert.Contains(forged.Findings, f => f.Contains("sent date"));
            Assert.Equal(IntegrityStatus.Valid, (await integrity(Zone("Europe/Berlin")).VerifyAsync(ids[4])).Status);
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task AForgedRow_AfterARealZoneChange_IsStillAFinding_AndSoIsAnArchiveTooSmallToTell()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var ids = await ArchiveManyAsync(ctx, a, 12);
            var (_, integrity) = Make(ctx, false);
            await ShiftAsync(ctx, ids[5], "3 hours");                                       // forged, on top of the zone change below
            ctx.ChangeTracker.Clear();
            var svc = integrity(Zone("UTC"));
            Assert.NotEqual(IntegrityStatus.Valid, (await svc.VerifyAsync(ids[5])).Status);
            Assert.Equal(IntegrityStatus.Valid, (await svc.VerifyAsync(ids[6])).Status);
        }
        finally { await CleanupAsync(ctx, a); }

        var ctx2 = NewContext(); var a2 = await AccountAsync(ctx2);
        try
        {
            var only = await ArchiveManyAsync(ctx2, a2, 1);                                 // one row: nothing to compare against
            var (_, integrity) = Make(ctx2, false);
            Assert.NotEqual(IntegrityStatus.Valid, (await integrity(Zone("UTC")).VerifyAsync(only[0])).Status);
        }
        finally { await CleanupAsync(ctx2, a2); }
    }

    // ---- N-13 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABodyAppearingOnAnEmailWhoseOriginalHasNoText_IsAFinding()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var attachmentOnly = Build(_ =>
            {
                var mixed = new Multipart("mixed");
                mixed.Add(new MimePart("application", "pdf")
                {
                    Content = new MimeContent(new MemoryStream(Enumerable.Range(0, 500).Select(i => (byte)(i * 3 + 1)).ToArray())),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "only.pdf" },
                    ContentTransferEncoding = ContentEncoding.Base64
                });
                return mixed;
            });
            var id = await ArchiveAsync(ctx, a, lean: true, attachmentOnly);
            Assert.Equal(IntegrityStatus.Valid, (await Make(ctx, true).Integrity(null).VerifyAsync(id)).Status);

            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"Body\" = 'added text that the original does not have' WHERE \"Id\" = {id}");
            ctx.ChangeTracker.Clear();
            var report = await Make(ctx, true).Integrity(null).VerifyAsync(id);
            Assert.NotEqual(IntegrityStatus.Valid, report.Status);
            Assert.Contains(report.Findings, f => f.Contains("no text"));
        }
        finally { await CleanupAsync(ctx, a); }
    }
}

/// <summary>Second review N-10 (settings reset) and N-19 (password throttle per address).</summary>
[Collection(TestDbFixture.CollectionName)]
public class SettingsReviewTwoTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    public SettingsReviewTwoTests(TestDbFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() { await ClearAsync(); SettingsService.ResetThrottle(); }
    public async Task DisposeAsync() { await ClearAsync(); SettingsService.ResetThrottle(); }

    private async Task ClearAsync()
    {
        await using var ctx = SettingsKit.NewContext(_fixture.ConnectionString);
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM mail_archiver.\"AppSettings\"");
    }

    private const string Key = "RawMessages:OriginalCleanup:BinRetentionDays";

    [Fact]
    public async Task AResetThatLowersAProtection_AsksForThePassword_AndRecordsTheConfiguredValue()
    {
        // appsettings.json says 7 days; the page stored 30.
        var h = SettingsKit.Create(_fixture.ConnectionString, new Dictionary<string, string?> { [Key] = "7" });
        Assert.True((await h.ChangeAsync(Key, "30")).Ok);                      // raising is safe, no password
        Assert.Equal(30, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);

        var noPassword = await h.ChangeAsync(Key, null, reset: true);
        Assert.False(noPassword.Ok);
        Assert.True(noPassword.NeedsPassword);                                  // 30 -> 7 lowers the protection, so it asks
        Assert.Equal(30, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);

        var withPassword = await h.ChangeAsync(Key, null, password: "secret", reset: true);
        Assert.True(withPassword.Ok, withPassword.Message);
        Assert.Equal(7, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
        var entry = h.Chain.Entries.Last(e => e.Kind == "setting.changed");
        Assert.Equal("7", JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(entry.Payload)).GetProperty("new").GetString());
        Assert.Equal("7", (await h.ViewAsync(Key)).Default);                    // the page's "default" is the configured value too
    }

    [Fact]
    public async Task TheBuiltInDefault_IsUsedWhenNothingIsConfigured()
    {
        var h = SettingsKit.Create(_fixture.ConnectionString);
        Assert.True((await h.ChangeAsync(Key, "60")).Ok);
        Assert.True((await h.ChangeAsync(Key, null, password: "secret", reset: true)).Ok);
        Assert.Equal(30, h.Mon<RawMessageOptions>().CurrentValue.OriginalCleanup.BinRetentionDays);
    }

    [Fact]
    public async Task WrongPasswordsFromOneAddress_AreBlocked_EvenAcrossAccounts_AndAreChained()
    {
        var h = SettingsKit.Create(_fixture.ConnectionString, users: u =>
        {
            u.Setup(x => x.GetUserByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => new User { Id = id, Username = "admin" + id, PasswordHash = "hash" });
            u.Setup(x => x.VerifyPassword("secret", "hash")).Returns(true);
        });
        using var scope = h.Sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        // Ten wrong passwords from one address, spread over different admin accounts (each under its own limit of five).
        for (var i = 0; i < 10; i++)
            await svc.ChangeAsync(Key, "8", new SettingActor(100 + i, "admin" + (100 + i), "203.0.113.9"), "wrong");

        var blocked = await svc.ChangeAsync(Key, "8", new SettingActor(200, "admin200", "203.0.113.9"), "secret");   // even the right password
        Assert.False(blocked.Ok);
        Assert.Contains("address", blocked.Message);
        Assert.Contains(h.Chain.Entries, e => e.Kind == "setting.password-failed");

        var elsewhere = await svc.ChangeAsync(Key, "8", new SettingActor(201, "admin201", "198.51.100.4"), "secret");   // another address is fine
        Assert.True(elsewhere.Ok, elsewhere.Message);
    }
}
