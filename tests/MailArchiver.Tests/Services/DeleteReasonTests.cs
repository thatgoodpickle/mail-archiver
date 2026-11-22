using System.Text.Json;
using MailArchiver.Controllers;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>
/// Delete memory, step 1: every delete site says WHY in the same transaction as the delete (SET LOCAL
/// mailarchiver.delete_reason), and the existing email.deleted trigger records it. Each test goes through the real
/// code of one site and reads the reason back from the chain entry the trigger wrote, which also proves that the
/// site really fires the trigger (including ExecuteDeleteAsync) and that the reason reaches it.
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class DeleteReasonTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly ReviewKit _kit;
    public DeleteReasonTests(TestDbFixture fixture) { _fixture = fixture; _kit = new ReviewKit(fixture); }
    public void Dispose() => _kit.Dispose();

    internal static ArchivedEmail NewEmail(int accountId, string? gmailId = null, string folder = "INBOX", DateTime? sent = null) => new()
    {
        MailAccountId = accountId, MessageId = $"<{Guid.NewGuid():N}@dm.test>", Subject = "dm", From = "a@dm.test", To = "b@dm.test",
        Body = "b", HtmlBody = "", Cc = "", Bcc = "", FolderName = folder, GmailMessageId = gmailId,
        SentDate = sent ?? DateTime.UtcNow.AddDays(-1), ReceivedDate = DateTime.UtcNow
    };

    internal static async Task<string?> ReasonOfAsync(MailArchiverDbContext ctx, int emailId)
    {
        var e = await ctx.AuditChain.AsNoTracking().Where(c => c.Kind == "email.deleted" && c.EmailId == emailId).SingleAsync();
        return JsonDocument.Parse(e.Payload).RootElement.GetProperty("reason").GetString();
    }

    private static async Task DropAsync(MailArchiverDbContext ctx, MailAccount a)
    {
        ctx.ChangeTracker.Clear();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {a.Id}");
        await ctx.ArchivedEmails.Where(e => e.MailAccountId == a.Id).ExecuteDeleteAsync();
        await ctx.MailAccounts.Where(x => x.Id == a.Id).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task SetReason_OutsideATransaction_Throws_BecauseItWouldSilentlyVanish()
    {
        await using var ctx = _kit.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeleteReasonScope.SetAsync(ctx, DeleteReasons.User));
    }

    [Fact]
    public async Task OnlyUserAndRetentionWriteMarkers()
    {
        Assert.True(DeleteReasons.WritesMarker("user"));
        Assert.True(DeleteReasons.WritesMarker("retention"));
        foreach (var r in new[] { "account", "internal", "unspecified", "", null, "USER" })
            Assert.False(DeleteReasons.WritesMarker(r));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NoReason_IsRecordedAsUnspecified_TheDefaultThatWritesNoMarker()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var e = NewEmail(account.Id);
            ctx.ArchivedEmails.Add(e);
            await ctx.SaveChangesAsync();
            ctx.ArchivedEmails.Remove(e);
            await ctx.SaveChangesAsync();
            Assert.Equal("unspecified", await ReasonOfAsync(ctx, e.Id));
        }
        finally { await DropAsync(ctx, account); await ctx.DisposeAsync(); }
    }

    [Fact]
    public async Task Scope_CarriesTheReasonToTheTrigger_AndItDoesNotLeakToTheNextDelete()
    {
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;
        try
        {
            var account = await ReviewKit.SeedAccountAsync(ctx);
            var a = NewEmail(account.Id); var b = NewEmail(account.Id);
            ctx.ArchivedEmails.AddRange(a, b);
            await ctx.SaveChangesAsync();

            await DeleteReasonScope.RunAsync(ctx, DeleteReasons.User, async () => { ctx.ArchivedEmails.Remove(a); await ctx.SaveChangesAsync(); });
            ctx.ArchivedEmails.Remove(b);              // same transaction, no scope: must not inherit 'user'
            await ctx.SaveChangesAsync();

            Assert.Equal("user", await ReasonOfAsync(ctx, a.Id));
            Assert.Equal("unspecified", await ReasonOfAsync(ctx, b.Id));
        }
        finally { await scope.RollbackAsync(); }
    }

    [Fact]
    public async Task Scope_WithoutAnOuterTransaction_OpensItsOwn_AndCommitsTheDelete()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var e = NewEmail(account.Id);
            ctx.ArchivedEmails.Add(e);
            await ctx.SaveChangesAsync();
            await DeleteReasonScope.RunAsync(ctx, DeleteReasons.User, async () => { ctx.ArchivedEmails.Remove(e); await ctx.SaveChangesAsync(); });
            Assert.Null(ctx.Database.CurrentTransaction);

            await using var other = _kit.NewContext();   // a second connection sees it: it was committed
            Assert.False(await other.ArchivedEmails.AnyAsync(x => x.Id == e.Id));
            Assert.Equal("user", await ReasonOfAsync(other, e.Id));
        }
        finally { await DropAsync(ctx, account); await ctx.DisposeAsync(); }
    }

    [Fact]
    public async Task Site_Retention_ExecuteDelete_FiresTheTrigger_WithReasonRetention()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            account.LocalRetentionDays = 30;
            var old = NewEmail(account.Id, sent: DateTime.UtcNow.AddDays(-90));
            ctx.ArchivedEmails.Add(old);
            await ctx.SaveChangesAsync();

            var wired = _kit.Make(ctx, lean: false);
            var n = await wired.Svc.DeleteOldLocalEmailsAsync(account);
            Assert.Equal(1, n);
            ctx.ChangeTracker.Clear();
            Assert.Equal("retention", await ReasonOfAsync(ctx, old.Id));
        }
        finally { await DropAsync(ctx, account); await ctx.DisposeAsync(); }
    }

    [Fact]
    public async Task Site_RollbackOfAnUnstoredEmail_IsInternal()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = ReviewKit.BuildMail();
            var wired = _kit.Make(ctx, lean: false, required: true, hashNamed: false);
            Directory.CreateDirectory(_kit.Root);
            foreach (var n in Enumerable.Range(0, 100)) File.WriteAllText(Path.Combine(_kit.Root, n.ToString("00")), "blocked");

            var outcome = await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap));
            Assert.Equal(ArchiveOutcome.Failed, outcome);

            ctx.ChangeTracker.Clear();
            var entry = await ctx.AuditChain.AsNoTracking().Where(c => c.Kind == "email.deleted").OrderByDescending(c => c.Seq).FirstAsync();
            Assert.Equal("internal", JsonDocument.Parse(entry.Payload).RootElement.GetProperty("reason").GetString());
        }
        finally { await DropAsync(ctx, account); await ctx.DisposeAsync(); }
    }

    // ---- controller (single and bulk) --------------------------------------------------------------------------------

    private sealed class StubUrl : IUrlHelper
    {
        public ActionContext ActionContext => new();
        public string? Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext c) => "/stub";
        public string? Content(string? p) => p;
        public bool IsLocalUrl(string? u) => true;
        public string? Link(string? r, object? v) => "/stub";
        public string? RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext c) => "/stub";
    }

    private static EmailsController Controller(MailArchiverDbContext ctx)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var auth = new Mock<IAuthenticationService>();
        auth.Setup(a => a.IsCurrentUserAdmin(It.IsAny<HttpContext>())).Returns(true);
        auth.Setup(a => a.GetCurrentUserDisplayName(It.IsAny<HttpContext>())).Returns("admin");
        var localization = new ServiceCollection().AddLogging().AddLocalization(o => o.ResourcesPath = "Resources").BuildServiceProvider();
        var c = new EmailsController(ctx, ServiceFactory.CreateEmailCoreService(ctx), providerFactory: null!, graphEmailService: null!,
            logger: NullLogger<EmailsController>.Instance, batchOptions: Opt.Create(new BatchRestoreOptions()),
            selectionOptions: Opt.Create(new SelectionOptions()), viewOptions: Opt.Create(new ViewOptions()),
            deletionPolicy: Opt.Create(new DeletionPolicyOptions { DeletionAllowed = true }),
            localizer: localization.GetRequiredService<IStringLocalizer<SharedResource>>(), authService: auth.Object, emailDeletionService: null);
        c.ControllerContext = new ControllerContext { HttpContext = http };
        c.Url = new StubUrl();
        c.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return c;
    }

    [Fact]
    public async Task Site_ControllerSingleAndBulkDelete_AreReasonUser()
    {
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;
        try
        {
            var account = await ReviewKit.SeedAccountAsync(ctx);
            var single = NewEmail(account.Id); var b1 = NewEmail(account.Id); var b2 = NewEmail(account.Id);
            ctx.ArchivedEmails.AddRange(single, b1, b2);
            await ctx.SaveChangesAsync();
            var ids = new List<int> { b1.Id, b2.Id };

            var c = Controller(ctx);
            await c.Delete(single.Id);
            await c.DeleteSelected(ids);

            ctx.ChangeTracker.Clear();
            Assert.False(await ctx.ArchivedEmails.AnyAsync(e => e.MailAccountId == account.Id));
            Assert.Equal("user", await ReasonOfAsync(ctx, single.Id));
            Assert.Equal("user", await ReasonOfAsync(ctx, b1.Id));
            Assert.Equal("user", await ReasonOfAsync(ctx, b2.Id));
        }
        finally { await scope.RollbackAsync(); }
    }

    // ---- background jobs ---------------------------------------------------------------------------------------------

    private static async Task<EmailDeletionJob> RunJobAsync(EmailDeletionService service, EmailDeletionJob job)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            service.QueueJob(job);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!job.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(250);
            return job;
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private static EmailDeletionService DeletionService(MailArchiverDbContext shared)
    {
        var services = new ServiceCollection();
        services.AddSingleton(shared);
        services.AddScoped<IAccessLogService, AccessLogService>();
        services.AddSingleton(Opt.Create(new DeletionPolicyOptions { DeletionAllowed = true }));
        return new EmailDeletionService(NullLogger<EmailDeletionService>.Instance,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), 1000);
    }

    [Fact]
    public async Task Site_DeletionJob_ById_IsReasonUser()
    {
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;
        try
        {
            var account = await ReviewKit.SeedAccountAsync(ctx);
            var a = NewEmail(account.Id); var b = NewEmail(account.Id);
            ctx.ArchivedEmails.AddRange(a, b);
            await ctx.SaveChangesAsync();

            var job = await RunJobAsync(DeletionService(ctx), new EmailDeletionJob { UserId = "dm-user", EmailIds = new List<int> { a.Id, b.Id } });
            Assert.Equal(EmailDeletionJobStatus.Completed, job.Status);

            ctx.ChangeTracker.Clear();
            Assert.Equal("user", await ReasonOfAsync(ctx, a.Id));
            Assert.Equal("user", await ReasonOfAsync(ctx, b.Id));
        }
        finally { await scope.RollbackAsync(); }
    }

    [Fact]
    public async Task Site_AccountDeletionJob_IsReasonAccount()
    {
        await using var scope = await _fixture.CreateTransactionalContextAsync();
        var ctx = scope.Context;
        try
        {
            var account = await ReviewKit.SeedAccountAsync(ctx);
            var a = NewEmail(account.Id); var b = NewEmail(account.Id);
            ctx.ArchivedEmails.AddRange(a, b);
            await ctx.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddSingleton(ctx);
            var svc = new MailAccountDeletionService(services.BuildServiceProvider(), NullLogger<MailAccountDeletionService>.Instance,
                Opt.Create(new BatchOperationOptions { PauseBetweenEmailsMs = 0 }), Mock.Of<ISyncJobService>());
            await svc.StartAsync(CancellationToken.None);
            string jobId;
            try
            {
                jobId = svc.QueueDeletion(account.Id, account.Name, "dm-user");
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline && svc.GetJob(jobId)!.Status is MailAccountDeletionJobStatus.Queued or MailAccountDeletionJobStatus.Running)
                    await Task.Delay(250);
            }
            finally { await svc.StopAsync(CancellationToken.None); }
            Assert.Equal(MailAccountDeletionJobStatus.Completed, svc.GetJob(jobId)!.Status);

            ctx.ChangeTracker.Clear();
            Assert.Equal("account", await ReasonOfAsync(ctx, a.Id));
            Assert.Equal("account", await ReasonOfAsync(ctx, b.Id));
        }
        finally { await scope.RollbackAsync(); }
    }
}
