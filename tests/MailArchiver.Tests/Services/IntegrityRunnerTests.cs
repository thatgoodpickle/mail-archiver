using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Core;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Moq;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Verify-all runs over real ingested synthetic mail, with deliberate tampering.</summary>
[Collection(TestDbFixture.CollectionName)]
public class IntegrityRunnerTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "run-" + Guid.NewGuid().ToString("N"));

    public IntegrityRunnerTests(TestDbFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private RawMessageOptions Raw() => new()
    {
        Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
        FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = true }
    };

    private MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString, npgsql => npgsql.CommandTimeout(120))
            .AddInterceptors(new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    private IRawMessageStore NewStore(MailArchiverDbContext ctx)
    {
        var raw = Raw();
        var opts = Opt.Create(raw);
        var fs = new FileSystemRawMessageStore(ctx, opts, new RawMessagePathResolver(raw.FileSystem, _root), NullLogger<FileSystemRawMessageStore>.Instance);
        return new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
    }

    private IntegrityRunner NewRunner()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddScoped(sp => NewStore(sp.GetRequiredService<MailArchiverDbContext>()));
        services.AddScoped<IIntegrityService>(sp => new IntegrityService(
            sp.GetRequiredService<MailArchiverDbContext>(), sp.GetRequiredService<IRawMessageStore>()));
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStopping).Returns(CancellationToken.None);
        return new IntegrityRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            lifetime.Object, NullLogger<IntegrityRunner>.Instance);
    }

    private static MimeMessage Mail(out byte[] bytes)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "from@example.test"));
        m.To.Add(new MailboxAddress("", "to@example.test"));
        m.Subject = "run " + Guid.NewGuid().ToString("N");
        m.MessageId = $"{Guid.NewGuid():N}@example.test";
        m.Body = new TextPart("plain") { Text = "run body " + Guid.NewGuid().ToString("N") };
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        return MimeMessage.Load(read);
    }

    [Fact]
    public async Task VerifyAll_RecordsCountsAndFindings_ForTamperedEmails()
    {
        var ctx = NewContext();
        var raw = Raw();
        var opts = Opt.Create(raw);
        var store = NewStore(ctx);
        var svc = new EmailCoreService(ctx, NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Opt.Create(new BatchOperationOptions()), Opt.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null, rawMessageOptions: opts, rawMessageStore: store);

        var account = new MailAccount
        {
            Name = $"r-{Guid.NewGuid():N}".Substring(0, 18), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();
        int runId = 0;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var msg = Mail(out var bytes);
                Assert.True(await svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
            }

            ctx.ChangeTracker.Clear();
            var ids = await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
            Assert.Equal(3, ids.Count);

            // email 1: database copy edited; email 2: original deleted; email 3: untouched
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"Subject\" = 'tampered' WHERE \"Id\" = {ids[0]}");
            var path = await ctx.RawMessages.Where(r => r.ArchivedEmailId == ids[1]).Select(r => r.StoragePath).SingleAsync();
            File.Delete(Path.Combine(_root, path!));

            var runner = NewRunner();
            var started = await runner.StartAsync("test");
            runId = started.Id;
            await runner.WaitForCurrentAsync();

            var run = await ctx.IntegrityRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            Assert.Equal(IntegrityRun.Completed, run.Status);
            Assert.NotNull(run.FinishedAt);
            Assert.Equal(run.Total, run.Checked);
            Assert.True(run.Checked >= 3);
            Assert.True(run.Valid >= 1);
            Assert.True(run.Inconsistent >= 1);
            Assert.True(run.Missing >= 1);

            var findings = await ctx.IntegrityFindings.AsNoTracking().Where(f => f.RunId == runId).ToListAsync();
            Assert.Equal("Inconsistent", findings.Single(f => f.EmailId == ids[0]).Status);
            Assert.Equal("Missing", findings.Single(f => f.EmailId == ids[1]).Status);
            Assert.DoesNotContain(findings, f => f.EmailId == ids[2]); // valid emails produce no finding
        }
        finally
        {
            ctx.ChangeTracker.Clear();
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
            await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
            await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
            if (runId != 0) await ctx.IntegrityRuns.Where(r => r.Id == runId).ExecuteDeleteAsync();
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_MarksAnInterruptedRunAsFailed()
    {
        var ctx = NewContext();
        var stale = new IntegrityRun { Status = IntegrityRun.Running, StartedAt = DateTime.UtcNow.AddHours(-2) };
        ctx.IntegrityRuns.Add(stale);
        await ctx.SaveChangesAsync();
        int newId = 0;
        try
        {
            var runner = NewRunner();
            newId = (await runner.StartAsync("test")).Id;
            await runner.WaitForCurrentAsync();

            ctx.ChangeTracker.Clear();
            var reloaded = await ctx.IntegrityRuns.AsNoTracking().SingleAsync(r => r.Id == stale.Id);
            Assert.Equal(IntegrityRun.Failed, reloaded.Status);
            Assert.Contains("Interrupted", reloaded.Error);
        }
        finally
        {
            await ctx.IntegrityRuns.Where(r => r.Id == stale.Id || r.Id == newId).ExecuteDeleteAsync();
            await ctx.DisposeAsync();
        }
    }
}
