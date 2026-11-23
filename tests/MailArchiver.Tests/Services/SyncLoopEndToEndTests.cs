using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>
/// The REAL folder sync loop end to end (MailKit client, in-process fake IMAP server, real Postgres): quick sync versus full sync, the
/// deletion marker through a Full resync and Allow again. (Checkpoint resume, quarantine and connection drops are in
/// FullResyncCheckpointTests and QuarantineCountingSyncTests; the filter in FullSyncGmailFilterTests.)
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class SyncLoopEndToEndTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    public SyncLoopEndToEndTests(TestDbFixture fixture) => _fixture = fixture;
    public Task InitializeAsync() => new ReviewKit(_fixture).ResetChainAsync();
    public Task DisposeAsync() => new ReviewKit(_fixture).ResetChainAsync();

    [Fact]
    public async Task QuickSync_OnlyLooksAtRecentMail_AFullResyncFindsMailThatEnteredWithAnOldDate()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var first = rig.AddMail(DateTimeOffset.UtcNow.AddDays(-30));
        await rig.RunAsync();                                   // first sync: a full sync
        Assert.Equal(new[] { first.MessageId }, await rig.ArchivedMessageIdsAsync());

        var fresh = rig.AddMail(DateTimeOffset.UtcNow.AddHours(-1));
        var oldDated = rig.AddMail(DateTimeOffset.UtcNow.AddDays(-20));   // e.g. "Not spam": a new UID, an old INTERNALDATE
        var mark = rig.Server.Commands.Count;
        await rig.RunAsync();                                   // quick sync (LastSync is recent)

        var ids = await rig.ArchivedMessageIdsAsync();
        Assert.Contains(fresh.MessageId, ids);
        Assert.DoesNotContain(oldDated.MessageId, ids);         // the documented gap that the periodic full sync closes
        Assert.DoesNotContain(oldDated.Uid, FullResyncCheckpointTests.FetchedUids(rig.Server, mark));

        Assert.True(await rig.ResyncAsync());
        Assert.Contains(oldDated.MessageId, await rig.ArchivedMessageIdsAsync());
        Assert.Equal(3, (await rig.ArchivedMessageIdsAsync()).Count);   // nothing archived twice
    }

    [Fact]
    public async Task FullResync_AfterYouDeleteAMessage_DoesNotBringItBack_UntilAllowAgain()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var keep = rig.AddMail(); var gone = rig.AddMail();
        await rig.RunAsync();
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);

        // The user deletes one archived email (reason "user": the database trigger writes the marker).
        await using (var ctx = rig.Kit.NewContext(withChain: true))
        {
            var row = await ctx.ArchivedEmails.SingleAsync(e => e.MailAccountId == rig.Account.Id && e.MessageId == gone.MessageId);
            ctx.ChangeTracker.Clear();
            await DeleteReasonScope.RunAsync(ctx, DeleteReasons.User, async () =>
            {
                ctx.ArchivedEmails.RemoveRange(await ctx.ArchivedEmails.Where(e => e.Id == row.Id).ToListAsync());
                await ctx.SaveChangesAsync();
            });
        }
        Assert.Equal(new[] { keep.MessageId }, await rig.ArchivedMessageIdsAsync());

        // Full resync: the message is still on the server but the marker blocks it.
        Assert.True(await rig.ResyncAsync());
        Assert.Equal(new[] { keep.MessageId }, await rig.ArchivedMessageIdsAsync());

        // Allow again, then a Full resync returns it.
        await using (var ctx = rig.Kit.NewContext())
        {
            var markers = new DeletionMarkerService(ctx, rig.Kit.Chain);
            var list = await markers.ListAsync(rig.Account.Id, 10);
            var marker = Assert.Single(list);
            Assert.Equal(1, await markers.ReleaseAsync(rig.Account.Id, marker.Id, "tester"));
        }
        Assert.True(await rig.ResyncAsync());
        Assert.Contains(gone.MessageId, await rig.ArchivedMessageIdsAsync());
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);
    }
}
