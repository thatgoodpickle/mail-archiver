using System.Data.Common;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit finding 19: the label refresh reads its rows per batch, and every envelope-only FETCH counts towards the bandwidth limit.</summary>
[Collection(TestDbFixture.CollectionName)]
public class AuditFixesBLabelRefreshTests
{
    private readonly TestDbFixture _fixture;
    public AuditFixesBLabelRefreshTests(TestDbFixture fixture) => _fixture = fixture;

    private sealed class SelectCounter : DbCommandInterceptor
    {
        public int Selects;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Selects++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task ApplyBatch_UsesAFixedNumberOfQueries_AndGivesTheSameResultAsOneAtATime()
    {
        var kit = new ReviewKit(_fixture);
        var counter = new SelectCounter();
        var ctx = kit.NewContext(false, counter);
        var a = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var rows = Enumerable.Range(0, 60).Select(i =>
            {
                var e = DeleteReasonTests.NewEmail(a.Id, gmailId: i % 2 == 0 ? "g" + i : null);   // half carry a Gmail id, half only a Message-ID
                e.MessageId = $"<m{i}@x>"; e.Labels = new List<string> { "\\Inbox" };
                return e;
            }).ToList();
            ctx.ArchivedEmails.AddRange(rows); await ctx.SaveChangesAsync(); ctx.ChangeTracker.Clear();

            var svc = new GmailMetaService(ctx, new MutableOptionsMonitor<IntegrityOptions>(new IntegrityOptions()));
            var items = Enumerable.Range(0, 60).Select(i => ((string?)$"<m{i}@x>", new GmailMeta(new List<string> { "\\Inbox", "Work" }, "g" + i, "t" + i))).ToList();
            counter.Selects = 0;
            var changed = await svc.ApplyBatchAsync(a.Id, items);
            Assert.Equal(60, changed);
            Assert.True(counter.Selects <= 2, $"expected at most 2 lookups for 60 messages, saw {counter.Selects}");

            ctx.ChangeTracker.Clear();
            var after = await ctx.ArchivedEmails.AsNoTracking().Where(e => e.MailAccountId == a.Id).ToListAsync();
            Assert.All(after, e => Assert.Contains("Work", e.Labels!));
            Assert.All(after, e => Assert.Equal("g" + e.MessageId.Substring(2, e.MessageId.IndexOf('@') - 2), e.GmailMessageId));

            // Nothing changed the second time: still two lookups, no writes counted.
            counter.Selects = 0;
            Assert.Equal(0, await svc.ApplyBatchAsync(a.Id, items));
            Assert.True(counter.Selects <= 2);
        }
        finally { await ReviewKit.CleanupAsync(ctx, a); kit.Dispose(); }
    }

    [Fact]
    public async Task TheDailyLabelRefresh_UpdatesLabels_AndCountsItsEnvelopeBytesInTheBandwidthUsage()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        rig.Bandwidth.Enabled = true;
        for (var i = 0; i < 5; i++) rig.AddMail((string?)null, (DateTimeOffset?)null, "\\Inbox");
        await rig.RunAsync();   // first sync archives them (and refreshes the labels once)
        Assert.Equal(5, (await rig.ArchivedMessageIdsAsync()).Count);

        // A day later: Gmail labels changed on the server; nothing new to download.
        foreach (var m in rig.Server.Main.Messages) m.Labels = new[] { "\\Inbox", "Project" };
        await rig.ResetBandwidthAsync();
        await using (var c = rig.Kit.NewContext())
            await c.MailAccounts.Where(a => a.Id == rig.Account.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.LabelsRefreshedAt, (DateTime?)null));
        var bodiesBefore = rig.Server.BodyFetches;
        await rig.RunAsync();
        Assert.Equal(bodiesBefore, rig.Server.BodyFetches);   // labels only, no body

        await using var ctx = rig.Kit.NewContext();
        var emails = await ctx.ArchivedEmails.AsNoTracking().Where(e => e.MailAccountId == rig.Account.Id).ToListAsync();
        Assert.All(emails, e => Assert.Contains("Project", e.Labels!));
        var usage = await ctx.BandwidthUsages.AsNoTracking().Where(u => u.MailAccountId == rig.Account.Id).ToListAsync();
        var bytes = usage.Sum(u => u.BytesDownloaded);
        Assert.True(bytes >= 5 * EnvelopeTraffic.PerMessageOverhead, $"the refresh must be counted, saw {bytes} bytes");
    }

    [Fact]
    public async Task TheFullSyncPreCheck_CountsItsEnvelopeBytes()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        rig.Bandwidth.Enabled = true;
        for (var i = 0; i < 5; i++) rig.AddMail();
        await rig.RunAsync();
        await rig.ResetBandwidthAsync();

        // A Full resync reads all five again: they are held, so nothing is downloaded, but the pre-check envelopes were fetched.
        var bodiesBefore = rig.Server.BodyFetches;
        await rig.ResyncAsync();
        await rig.RunAsync();
        await using var ctx = rig.Kit.NewContext();
        var bytes = (await ctx.BandwidthUsages.AsNoTracking().Where(u => u.MailAccountId == rig.Account.Id).ToListAsync()).Sum(u => u.BytesDownloaded);
        Assert.True(bytes >= 5 * EnvelopeTraffic.PerMessageOverhead, $"pre-check envelopes must be counted, saw {bytes} bytes (body fetches {rig.Server.BodyFetches - bodiesBefore})");
    }
}
