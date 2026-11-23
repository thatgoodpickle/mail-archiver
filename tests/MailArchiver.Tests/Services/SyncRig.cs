using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Providers.Eml;
using MailArchiver.Services.Providers.Imap;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>
/// Drives the REAL <see cref="ImapMailSyncService"/> (folder loop, search, checkpoints, quarantine, markers, ingest into a real Postgres)
/// against a <see cref="FakeImapServer"/> on loopback. Every run builds fresh scoped services, as the production scheduler does.
/// The account's server name is a Gmail name (so the Gmail logic applies) but the connection factory always connects to the fake.
/// </summary>
internal sealed class SyncRig : IDisposable
{
    private sealed class LocalFactory : ImapConnectionFactory
    {
        private readonly int _port;
        public LocalFactory(int port, MailArchiverDbContext ctx)
            : base(NullLogger<ImapConnectionFactory>.Instance, Opt.Create(new MailSyncOptions()), Opt.Create(new BatchOperationOptions { PauseBetweenBatchesMs = 0 }),
                  msaOAuthService: null!, ctx) => _port = port;
        public override Task ConnectWithFallbackAsync(MailKit.Net.Imap.ImapClient client, string server, int port, bool useSSL, string accountName)
            => client.ConnectAsync("127.0.0.1", _port, MailKit.Security.SecureSocketOptions.None);
    }

    public readonly ReviewKit Kit;
    public readonly FakeImapServer Server;
    public MailAccount Account = null!;
    public int BatchSize = 20;
    public bool Gmail { get; }
    public readonly BandwidthTrackingOptions Bandwidth = new() { Enabled = false };
    public SyncQuarantineOptions Quarantine = new();

    private static readonly int[] OriginalDelays = ImapMailSyncService.TransientFetchRetryDelaysMs;

    public SyncRig(TestDbFixture fixture, bool gmail = true)
    {
        Kit = new ReviewKit(fixture);
        Gmail = gmail;
        Server = new FakeImapServer(gmail);
        // The retry back-off for a failing FETCH is 5/15/60 s in production; here it is a millisecond.
        ImapMailSyncService.TransientFetchRetryDelaysMs = new[] { 1, 1, 1 };
    }

    public async Task InitAsync(string? filter = null, bool setFilter = false)
    {
        await using var ctx = Kit.NewContext();
        Account = await ReviewKit.SeedAccountAsync(ctx);
        var a = await ctx.MailAccounts.FirstAsync(x => x.Id == Account.Id);
        a.ImapServer = Gmail ? "imap.gmail.com" : "imap.fake.test";
        a.ImapPort = 993;
        a.UseSSL = false;
        a.Username = "tester";
        a.Password = "secret";
        a.LastSync = FullSyncRequest.EpochUtc;
        if (setFilter) a.GmailSearchFilter = filter;
        await ctx.SaveChangesAsync();
    }

    public void Dispose()
    {
        ImapMailSyncService.TransientFetchRetryDelaysMs = OriginalDelays;
        Server.Dispose();
        try
        {
            using var ctx = Kit.NewContext();
            ctx.Database.ExecuteSqlInterpolated($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {Account.Id}");
            ctx.ArchivedEmails.Where(e => e.MailAccountId == Account.Id).ExecuteDelete();
            ctx.SyncProblemMessages.Where(p => p.MailAccountId == Account.Id).ExecuteDelete();
            ctx.SyncCheckpoints.Where(c => c.MailAccountId == Account.Id).ExecuteDelete();
            ctx.MailAccounts.Where(a => a.Id == Account.Id).ExecuteDelete();
        }
        catch { }
        Kit.Dispose();
    }

    /// <summary>Adds a synthetic message to the server's main folder and returns its UID and Message-ID.</summary>
    public (uint Uid, string MessageId) AddMail(DateTimeOffset? internalDate = null, params string[] labels) => AddMail(null, internalDate, labels);

    public (uint Uid, string MessageId) AddMail(string? text, DateTimeOffset? internalDate = null, params string[] labels)
    {
        var (m, bytes, _) = ReviewKit.BuildMail(text: text);
        var msg = Server.Add(Server.Main, bytes, m.MessageId, m.Subject, internalDate ?? DateTimeOffset.UtcNow.AddHours(-3), labels);
        return (msg.Uid, m.MessageId);
    }

    public sealed record RunContext(MailArchiverDbContext Ctx, ImapMailSyncService Service, MailAccount Account, DeletionMarkerService Markers, ISyncProblemService Problems, IBandwidthService BandwidthService);

    public RunContext NewRun()
    {
        var ctx = Kit.NewContext(withChain: true);
        var wired = Kit.Make(ctx, lean: false);
        var bandwidth = ServiceFactory.CreateBandwidthService(ctx, Bandwidth);
        var markers = new DeletionMarkerService(ctx, Kit.Chain);
        var problems = new SyncProblemService(ctx, new MutableOptionsMonitor<SyncQuarantineOptions>(Quarantine));
        var meta = new GmailMetaService(ctx, new MutableOptionsMonitor<IntegrityOptions>(new IntegrityOptions()), Kit.Chain);
        var services = new ServiceCollection();
        services.AddSingleton(ctx);
        var sync = new ImapMailSyncService(ctx, NullLogger<ImapMailSyncService>.Instance, wired.Svc,
            new SyncJobService(NullLogger<SyncJobService>.Instance, services.BuildServiceProvider(), Opt.Create(new MailSyncOptions())),
            bandwidth, new LocalFactory(Server.Port, ctx), new EmlMailCleaner(NullLogger<EmlMailCleaner>.Instance), new ImapFolderService(NullLogger<ImapFolderService>.Instance),
            new MailArchiver.Utilities.DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Opt.Create(new BatchOperationOptions { BatchSize = BatchSize, PauseBetweenBatchesMs = 0, PauseBetweenEmailsMs = 0 }),
            Opt.Create(new MailSyncOptions()), Opt.Create(Bandwidth), problems, meta, markers);
        var account = ctx.MailAccounts.AsNoTracking().First(a => a.Id == Account.Id);
        return new RunContext(ctx, sync, account, markers, problems, bandwidth);
    }

    /// <summary>One scheduled sync run of the account, as the background service does it (with the stored LastSync).</summary>
    public async Task<MailAccount> RunAsync(CancellationToken ct = default)
    {
        var run = NewRun();
        try { await run.Service.SyncMailAccountAsync(run.Account, null, ct); }
        finally { await run.Ctx.DisposeAsync(); }
        return await ReloadAsync();
    }

    public async Task<MailAccount> ReloadAsync()
    {
        await using var ctx = Kit.NewContext();
        return await ctx.MailAccounts.AsNoTracking().FirstAsync(a => a.Id == Account.Id);
    }

    /// <summary>A Full resync as the manual button does it.</summary>
    public async Task<bool> ResyncAsync()
    {
        var run = NewRun();
        try { return await run.Service.ResyncAccountAsync(Account.Id); }
        finally { await run.Ctx.DisposeAsync(); }
    }

    public async Task<List<string>> ArchivedMessageIdsAsync()
    {
        await using var ctx = Kit.NewContext();
        return await ctx.ArchivedEmails.AsNoTracking().Where(e => e.MailAccountId == Account.Id).Select(e => e.MessageId).ToListAsync();
    }

    public async Task<List<SyncCheckpoint>> CheckpointsAsync()
    {
        await using var ctx = Kit.NewContext();
        return await ctx.SyncCheckpoints.AsNoTracking().Where(c => c.MailAccountId == Account.Id).ToListAsync();
    }

    public async Task<List<SyncProblemMessage>> ProblemsAsync()
    {
        await using var ctx = Kit.NewContext();
        return await ctx.SyncProblemMessages.AsNoTracking().Where(p => p.MailAccountId == Account.Id).ToListAsync();
    }

    /// <summary>Forgets today's bandwidth usage (as if the 24 hours had passed).</summary>
    public async Task ResetBandwidthAsync()
    {
        await using var ctx = Kit.NewContext();
        await ctx.BandwidthUsages.Where(u => u.MailAccountId == Account.Id).ExecuteDeleteAsync();
    }

    public async Task SetLastSyncAsync(DateTime when)
    {
        await using var ctx = Kit.NewContext();
        await ctx.MailAccounts.Where(a => a.Id == Account.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.LastSync, when));
    }
}
