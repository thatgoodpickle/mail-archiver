using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit findings 15, 14, 18 and the small leftovers: sync-path behaviour driven through the real loop and the fake server.</summary>
[Collection(TestDbFixture.CollectionName)]
public class AuditFixesBSyncTests
{
    private readonly TestDbFixture _fixture;
    public AuditFixesBSyncTests(TestDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task F15_AllMailInTheExcludedFolders_FailsTheJobLoudly_AndLastSyncDoesNotMove()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        rig.AddMail(); rig.AddMail();
        await using (var c = rig.Kit.NewContext())
            await c.MailAccounts.Where(a => a.Id == rig.Account.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ExcludedFolders, FakeImapServer.AllMail));

        var run = rig.NewRun();
        try
        {
            var jobId = run.Jobs!.StartSync(rig.Account.Id, rig.Account.Name);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Service.SyncMailAccountAsync(run.Account, jobId));
            Assert.Contains("excluded", ex.Message);
            var job = run.Jobs.GetJob(jobId)!;
            Assert.Equal(SyncJobStatus.Failed, job.Status);
            Assert.Contains("excluded", job.ErrorMessage);
            var issue = Assert.Single(job.Issues.Issues);
            Assert.Equal(SyncIssueKind.FolderFailed, issue.Kind);
        }
        finally { await run.Ctx.DisposeAsync(); }

        Assert.Equal(FullSyncRequest.EpochUtc, (await rig.ReloadAsync()).LastSync);
        Assert.Empty(await rig.ArchivedMessageIdsAsync());

        // Control: without the exclusion the same server syncs and LastSync moves.
        await using (var c = rig.Kit.NewContext())
            await c.MailAccounts.Where(a => a.Id == rig.Account.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ExcludedFolders, ""));
        var ok = await rig.RunAsync();
        Assert.True(ok.LastSync > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F14_AfterAUidValidityChange_OldProblemRowsAreSuperseded_AndTheMessageIsReadAgainByTheRequestedFullResync(bool retryFirst)
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var a = rig.AddMail(); var bad = rig.AddMail(); var c = rig.AddMail();
        rig.Server.OnBodyFetch = (_, uid) => uid == bad.Uid ? (FakeImapServer.FetchAction.No, "Invalid message data") : (FakeImapServer.FetchAction.Normal, "");
        for (var i = 0; i < 3; i++) await rig.RunAsync();
        var row = Assert.Single(await rig.ProblemsAsync());
        Assert.Equal(SyncProblemMessage.Quarantined, row.Status);
        Assert.Equal(1, row.UidValidity);
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);

        if (retryFirst)
        {
            // An administrator presses Retry on the listed row: it goes back into the flow.
            var run = rig.NewRun();
            try { Assert.Equal(1, await run.Problems.RetryAsync(row.Id, null)); } finally { await run.Ctx.DisposeAsync(); }
        }

        // The server renumbers the folder; the message is healthy again.
        rig.Server.Main.UidValidity = 2;
        rig.Server.OnBodyFetch = (_, _) => (FakeImapServer.FetchAction.Normal, "");
        var r1 = await rig.RunAsync();

        // The old row no longer shows up as set aside, and a Full resync is pending (LastSync back at 1970, not moved forward).
        var old = Assert.Single(await rig.ProblemsAsync());
        Assert.Equal(SyncProblemMessage.Superseded, old.Status);
        var check = rig.NewRun();
        try
        {
            Assert.Equal(0, await check.Problems.CountQuarantinedAsync(rig.Account.Id));
            Assert.DoesNotContain(await check.Problems.ListAsync(1000), p => p.MailAccountId == rig.Account.Id);
        }
        finally { await check.Ctx.DisposeAsync(); }
        Assert.Equal(FullSyncRequest.EpochUtc, r1.LastSync);

        // The next run is that Full resync: the message comes back, and LastSync moves on.
        var r2 = await rig.RunAsync();
        Assert.Equal(3, (await rig.ArchivedMessageIdsAsync()).Count);
        Assert.True(r2.LastSync > DateTime.UtcNow.AddMinutes(-5));
        // The superseded row is not resurrected and nothing is counted as set aside.
        Assert.All(await rig.ProblemsAsync(), p => Assert.Equal(SyncProblemMessage.Superseded, p.Status));
    }
}
