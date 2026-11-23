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
}
