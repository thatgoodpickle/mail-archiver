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
using Npgsql;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

public class GmailLabelHelperTests
{
    [Theory]
    [InlineData("Marketing", true)]
    [InlineData("marketing", true)]
    [InlineData("MARKETING/Newsletters", true)]
    [InlineData("Marketing/Offers/2026", true)]
    [InlineData("Marketingx", false)]
    [InlineData("Not Marketing", false)]
    [InlineData("Work/Marketing", false)]
    [InlineData("", false)]
    public void MarketingIsTheLabelOrAnySubLabelOfIt(string label, bool expected) => Assert.Equal(expected, GmailLabels.IsMarketing(label));

    [Fact]
    public void SentIsGmailsSystemLabel()
    {
        Assert.True(GmailLabels.IsSent(new[] { "\\Inbox", "\\Sent" }));
        Assert.True(GmailLabels.IsSent(new[] { "\\sent" }));
        Assert.False(GmailLabels.IsSent(new[] { "Sent" }));          // a user label of that name is not the system label
        Assert.False(GmailLabels.IsSent(null));
    }

    [Fact]
    public void LabelSetsAreComparedWithoutOrder()
    {
        Assert.True(GmailLabels.SameSet(new[] { "a", "b" }, new[] { "b", "a" }));
        Assert.False(GmailLabels.SameSet(new[] { "a" }, new[] { "a", "b" }));
        Assert.True(GmailLabels.SameSet(null, Array.Empty<string>()));
    }
}

/// <summary>Gmail labels (step 5): captured at ingest, refreshed on archived emails, filterable, and never able to touch a locked email's content.</summary>
[Collection(TestDbFixture.CollectionName)]
public class GmailLabelsTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gl-" + Guid.NewGuid().ToString("N"));
    public GmailLabelsTests(TestDbFixture fixture) => _fixture = fixture;

    private async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(_fixture.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task ResetChainAsync() => ExecAsync(@"ALTER TABLE mail_archiver.""AuditChain"" DISABLE TRIGGER USER; DELETE FROM mail_archiver.""AuditChain""; ALTER TABLE mail_archiver.""AuditChain"" ENABLE TRIGGER USER;");
    public Task InitializeAsync() => ResetChainAsync();
    public async Task DisposeAsync() { await ResetChainAsync(); try { Directory.Delete(_root, true); } catch { } }

    private MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    private EmailCoreService Core(MailArchiverDbContext ctx)
    {
        var raw = new RawMessageOptions { Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0, FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = true } };
        var opts = Opt.Create(raw);
        var fs = new FileSystemRawMessageStore(ctx, opts, new RawMessagePathResolver(raw.FileSystem, _root), NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
        return new EmailCoreService(ctx, NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "UTC" })),
            Opt.Create(new BatchOperationOptions()), Opt.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null, rawMessageOptions: opts, rawMessageStore: router,
            contentHydrator: new StrippedContentHydrator(ctx, new OriginalMessageReader(router)),
            storageMode: Opt.Create(new StorageModeOptions { Mode = "Lean" }),
            leanIngest: new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance));
    }

    private static (MimeMessage Message, byte[] Bytes) Mail(string subject)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "a@example.test")); m.To.Add(new MailboxAddress("", "b@example.test"));
        m.Subject = subject; m.MessageId = $"{Guid.NewGuid():N}@example.test"; m.Body = new TextPart("plain") { Text = "body of " + subject };
        using var ms = new MemoryStream(); m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        return (MimeMessage.Load(read), bytes);
    }

    private async Task<MailAccount> AccountAsync(MailArchiverDbContext ctx)
    {
        var a = new MailAccount { Name = $"gl-{Guid.NewGuid():N}".Substring(0, 18), EmailAddress = $"{Guid.NewGuid():N}@test.local", Provider = ProviderType.IMAP, ImapServer = "imap.gmail.com", IsEnabled = true, LastSync = DateTime.UtcNow };
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

    private static GmailMeta Meta(string id, params string[] labels) => new(labels, id, "t" + id);

    private GmailMetaService MetaService(MailArchiverDbContext ctx, bool chainLabels = false)
    {
        var services = new ServiceCollection(); services.AddScoped(_ => NewContext());
        var integrity = new IntegrityOptions { ChainLabelChanges = chainLabels };
        var chain = new AuditChain(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), Opt.Create(integrity), NullLogger<AuditChain>.Instance);
        return new GmailMetaService(ctx, new MutableOptionsMonitor<IntegrityOptions>(integrity), chain);
    }

    [Fact]
    public async Task LabelsAndIds_AreStoredWithTheEmail_AndSentMakesItOutgoing()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var (m, b) = Mail("sent one");
            Assert.Equal(ArchiveOutcome.Archived, await Core(ctx).ArchiveEmailOutcomeAsync(a, m, false, "[Gmail]/All Mail", new RawMessageCapture(b, RawMessageSource.Imap), Meta("111", "\\Sent", "Work/Projects")));

            ctx.ChangeTracker.Clear();
            var e = await ctx.ArchivedEmails.SingleAsync(x => x.MailAccountId == a.Id);
            Assert.Equal(new[] { "Work/Projects", "\\Sent" }, e.Labels!.OrderBy(l => l, StringComparer.Ordinal).ToArray());
            Assert.Equal("111", e.GmailMessageId);
            Assert.Equal("t111", e.GmailThreadId);
            Assert.NotNull(e.LabelsCapturedAt);
            Assert.True(e.IsOutgoing);                       // first seen in All Mail, which is not a sent folder: the Sent label decides
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task ChangedLabels_AreRefreshedOnTheArchivedEmail_ByGmailIdOrMessageId_AndOnlyWhenTheyChanged()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var (m, b) = Mail("refresh");
            await Core(ctx).ArchiveEmailOutcomeAsync(a, m, false, "[Gmail]/All Mail", new RawMessageCapture(b, RawMessageSource.Imap), Meta("222", "\\Inbox"));
            var svc = MetaService(ctx);

            Assert.Equal(0, await svc.ApplyAsync(a.Id, m.MessageId, Meta("222", "\\Inbox")));                       // nothing changed
            Assert.Equal(1, await svc.ApplyAsync(a.Id, null, Meta("222", "\\Inbox", "Marketing/Newsletters")));      // found by Gmail's own id
            ctx.ChangeTracker.Clear();
            Assert.Contains("Marketing/Newsletters", (await ctx.ArchivedEmails.SingleAsync(x => x.MailAccountId == a.Id)).Labels!);

            Assert.Equal(1, await svc.ApplyAsync(a.Id, m.MessageId, Meta("222", "\\Inbox")));                       // and back
            ctx.ChangeTracker.Clear();
            Assert.DoesNotContain("Marketing/Newsletters", (await ctx.ArchivedEmails.SingleAsync(x => x.MailAccountId == a.Id)).Labels!);

            Assert.Equal(0, await svc.ApplyAsync(a.Id + 999999, m.MessageId, Meta("222", "x")));                   // another account's mail is never touched
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task ALockedEmail_StillGetsItsLabels_ButKeepsItsContentAndDirection()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var (m, b) = Mail("locked");
            await Core(ctx).ArchiveEmailOutcomeAsync(a, m, false, "[Gmail]/All Mail", new RawMessageCapture(b, RawMessageSource.Imap), Meta("333", "\\Inbox"));
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = true WHERE \"MailAccountId\" = {a.Id}");

            Assert.Equal(1, await MetaService(ctx).ApplyAsync(a.Id, m.MessageId, Meta("333", "\\Inbox", "\\Sent", "Work")));   // the trigger lets labels through

            ctx.ChangeTracker.Clear();
            var e = await ctx.ArchivedEmails.SingleAsync(x => x.MailAccountId == a.Id);
            Assert.Contains("Work", e.Labels!);
            Assert.False(e.IsOutgoing);                                                    // a locked email's direction is part of its protected content
            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"Subject\" = 'forged' WHERE \"Id\" = {e.Id}"));
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task LabelChanges_AreChainedOnlyWhenTheOptionIsOn()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var (m, b) = Mail("chained");
            await Core(ctx).ArchiveEmailOutcomeAsync(a, m, false, "[Gmail]/All Mail", new RawMessageCapture(b, RawMessageSource.Imap), Meta("444", "\\Inbox"));

            await MetaService(ctx, chainLabels: false).ApplyAsync(a.Id, m.MessageId, Meta("444", "A"));
            Assert.Equal(0, await ctx.AuditChain.CountAsync(c => c.Kind == "email.labels.changed"));

            await MetaService(ctx, chainLabels: true).ApplyAsync(a.Id, m.MessageId, Meta("444", "B"));
            Assert.Equal(1, await ctx.AuditChain.CountAsync(c => c.Kind == "email.labels.changed"));
        }
        finally { await CleanupAsync(ctx, a); }
    }

    [Fact]
    public async Task TheLabelFilter_FindsExactLabels_AndMarketingWithAllSubLabels_AndFeedsTheDeletionQuery()
    {
        var ctx = NewContext(); var a = await AccountAsync(ctx);
        try
        {
            var svc = Core(ctx);
            foreach (var (subject, labels) in new[]
            {
                ("plain work", new[] { "\\Inbox", "Work" }),
                ("promo", new[] { "Marketing" }),
                ("promo sub", new[] { "marketing/Newsletters", "\\Inbox" }),
                ("not marketing", new[] { "Work/Marketing", "Marketingx" }),
                ("no labels", Array.Empty<string>()),
            })
            {
                var (m, b) = Mail(subject);
                await svc.ArchiveEmailOutcomeAsync(a, m, false, "[Gmail]/All Mail", new RawMessageCapture(b, RawMessageSource.Imap), Meta(Guid.NewGuid().ToString("N")[..8], labels));
            }
            var allowed = new List<int> { a.Id };

            (List<ArchivedEmail> Emails, int TotalCount) Search(string? label) => svc.SearchEmailsAsync(string.Empty, null, null, a.Id, null, null, 0, 50, allowed, "SentDate", "desc", label).GetAwaiter().GetResult();

            Assert.Equal(5, Search(null).TotalCount);
            Assert.Equal(1, Search("Work").TotalCount);
            Assert.Equal(new[] { "promo", "promo sub" }, Search("@marketing").Emails.Select(e => e.Subject).OrderBy(x => x).ToArray());
            Assert.Equal(0, Search("Nothing").TotalCount);

            // The bulk delete uses the very same clause, so what the page shows is what it deletes.
            var clause = EmailFilterSql.Build(null, null, null, a.Id, null, null, allowed, label: "@marketing");
            await using var c = new NpgsqlConnection(_fixture.ConnectionString);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($@"SELECT COUNT(*) FROM mail_archiver.""ArchivedEmails"" {clause.WhereClause}", c);
            foreach (var p in clause.Parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!);
        }
        finally { await CleanupAsync(ctx, a); }
    }
}
