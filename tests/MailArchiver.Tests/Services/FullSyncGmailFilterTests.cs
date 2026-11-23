using MailArchiver.Tests.Infrastructure;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit finding 2: on a full sync the Gmail filter is never bypassed by the "search returned fewer than the folder count" safety path.</summary>
[Collection(TestDbFixture.CollectionName)]
public class FullSyncGmailFilterTests
{
    private readonly TestDbFixture _fixture;
    public FullSyncGmailFilterTests(TestDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FirstSync_WithTheDefaultFilter_DoesNotArchiveMarketingMail()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var keep1 = rig.AddMail(null, "Inbox");
        var mkt = rig.AddMail(null, "Marketing");
        var keep2 = rig.AddMail(null, "Work");
        var mktSub = rig.AddMail(null, "Marketing/Sub");

        await rig.RunAsync();

        var ids = await rig.ArchivedMessageIdsAsync();
        Assert.Equal(2, ids.Count);
        Assert.Contains(keep1.MessageId, ids); Assert.Contains(keep2.MessageId, ids);
        Assert.DoesNotContain(mkt.MessageId, ids); Assert.DoesNotContain(mktSub.MessageId, ids);
    }

    [Fact]
    public async Task FullResync_WithTheDefaultFilter_StillSkipsMarketingMail()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var keep = rig.AddMail(null, "Inbox");
        var mkt = rig.AddMail(null, "Marketing");
        await rig.RunAsync();
        rig.AddMail(null, "Marketing");

        Assert.True(await rig.ResyncAsync());

        var ids = await rig.ArchivedMessageIdsAsync();
        Assert.Equal(new[] { keep.MessageId }, ids);
    }

    [Fact]
    public async Task ServerWithASearchLimit_FilterOn_IsWidenedByTheFilteredSearchOnly()
    {
        // The server answers a SINCE search with at most 2 UIDs. The safety path must recover the 5 messages the filter lets through,
        // and must not take the 2 Marketing messages along.
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var keep = Enumerable.Range(0, 5).Select(_ => rig.AddMail(null, "Inbox")).ToList();
        var mkt = Enumerable.Range(0, 2).Select(_ => rig.AddMail(null, "Marketing")).ToList();
        rig.Server.CapSinceSearch = 2;

        await rig.RunAsync();

        var ids = await rig.ArchivedMessageIdsAsync();
        Assert.Equal(5, ids.Count);
        Assert.All(keep, k => Assert.Contains(k.MessageId, ids));
        Assert.All(mkt, k => Assert.DoesNotContain(k.MessageId, ids));
    }

    [Fact]
    public async Task ServerWithASearchLimit_NoFilter_StillFetchesAllByTheSequenceSafetyPath()
    {
        // Not a Gmail account: no filter, the old safety behaviour is kept.
        using var rig = new SyncRig(_fixture, gmail: false); await rig.InitAsync();
        for (var i = 0; i < 5; i++) rig.AddMail();
        rig.Server.CapSinceSearch = 2;

        await rig.RunAsync();

        Assert.Equal(5, (await rig.ArchivedMessageIdsAsync()).Count);
    }
}
