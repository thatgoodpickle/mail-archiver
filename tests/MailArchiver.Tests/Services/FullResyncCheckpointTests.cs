using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>
/// Audit finding 1: a Full resync (manual button and automatic periodic one) clears the sync checkpoints; a normal run (and a paused full
/// sync) still resumes from them. These tests drive the real folder sync loop against the in-process fake IMAP server.
/// </summary>
[Collection(TestDbFixture.CollectionName)]
public class FullResyncCheckpointTests
{
    private readonly TestDbFixture _fixture;
    public FullResyncCheckpointTests(TestDbFixture fixture) => _fixture = fixture;

    private static async Task SeedCheckpointAsync(SyncRig rig, long lastUid, long uidValidity = 1)
    {
        await using var ctx = rig.Kit.NewContext();
        ctx.SyncCheckpoints.Add(new SyncCheckpoint
        {
            MailAccountId = rig.Account.Id, FolderName = FakeImapServer.AllMail, LastUid = lastUid, UidValidity = uidValidity,
            LastMessageDate = DateTime.UtcNow.AddDays(-5), LastMessageId = "<old@x>", UpdatedAt = DateTime.UtcNow, IsCompleted = false
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>The UIDs that FETCH commands issued after <paramref name="sinceIndex"/> asked for.</summary>
    internal static HashSet<uint> FetchedUids(FakeImapServer server, int sinceIndex)
    {
        var set = new HashSet<uint>();
        var cmds = server.CommandsSince(sinceIndex);
        foreach (var c in cmds)
        {
            var parts = c.Split(' ');
            if (parts.Length < 3 || !parts[0].Equals("UID", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("FETCH", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var piece in parts[2].Split(','))
            {
                var ab = piece.Split(':');
                if (uint.TryParse(ab[0], out var lo))
                    for (var u = lo; u <= (ab.Length > 1 && uint.TryParse(ab[1], out var hi) ? hi : lo); u++) set.Add(u);
            }
        }
        return set;
    }

    [Fact]
    public async Task FirstSync_ArchivesEverything_AdvancesLastSync_AndLeavesNoCheckpoint()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var mails = Enumerable.Range(0, 3).Select(_ => rig.AddMail()).ToList();

        var after = await rig.RunAsync();

        Assert.Equal(3, (await rig.ArchivedMessageIdsAsync()).Count);
        Assert.True(after.LastSync > DateTime.UtcNow.AddMinutes(-5));
        Assert.Empty(await rig.CheckpointsAsync());
    }

    [Fact]
    public async Task NormalRun_ResumesFromAnExistingCheckpoint_AndSkipsWhatIsBelowIt()
    {
        // A paused full sync: LastSync is still 1970 and the checkpoint says "archived up to UID 2".
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        for (var i = 0; i < 4; i++) rig.AddMail();
        await SeedCheckpointAsync(rig, lastUid: 2);
        var mark = rig.Server.Commands.Count;

        await rig.RunAsync();

        // Only UIDs 3 and 4 were looked at; 1 and 2 were skipped by the checkpoint. The run ends clean and clears it.
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);
        Assert.Equal(new HashSet<uint> { 3, 4 }, FetchedUids(rig.Server, mark));
        Assert.Empty(await rig.CheckpointsAsync());
    }

    [Fact]
    public async Task ManualFullResync_ClearsAStaleCheckpoint_SoMessagesBelowTheWatermarkAreFetched()
    {
        // The audit scenario: a run left a checkpoint at UID 3; later, messages below that UID become visible (a cap raise on Gmail).
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        for (var i = 0; i < 3; i++) rig.AddMail();
        await SeedCheckpointAsync(rig, lastUid: 3);
        await rig.SetLastSyncAsync(DateTime.UtcNow.AddMinutes(-20));

        Assert.True(await rig.ResyncAsync());

        Assert.Equal(3, (await rig.ArchivedMessageIdsAsync()).Count);
        Assert.Empty(await rig.CheckpointsAsync());
    }

    [Fact]
    public async Task ManualFullResync_WithoutTheFix_WouldHaveSkippedThem_ControlRunWithTheStaleCheckpointKept()
    {
        // Control: the same state, but a plain full run (LastSync 1970, checkpoint kept) skips everything below the watermark. This is why the
        // checkpoint has to be cleared where the resync is requested, and it proves the test above really exercises the checkpoint.
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        for (var i = 0; i < 3; i++) rig.AddMail();
        await SeedCheckpointAsync(rig, lastUid: 3);

        await rig.RunAsync();

        Assert.Empty(await rig.ArchivedMessageIdsAsync());
    }

    [Fact]
    public async Task AutomaticFullSync_ClearsAStaleCheckpoint()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        for (var i = 0; i < 3; i++) rig.AddMail();
        await SeedCheckpointAsync(rig, lastUid: 3);
        await rig.SetLastSyncAsync(DateTime.UtcNow.AddHours(-30));

        // What the background service does when the periodic full sync is due.
        var lastSync = (await rig.ReloadAsync()).LastSync;
        await using (var ctx = rig.Kit.NewContext())
            await FullSyncRequest.RequestAsync(ctx, ServiceFactory.CreateBandwidthService(ctx), rig.Account.Id, FullSyncRequest.ShouldClearForAutomatic(lastSync));
        Assert.Empty(await rig.CheckpointsAsync());
        Assert.Equal(FullSyncRequest.EpochUtc, (await rig.ReloadAsync()).LastSync);

        await rig.RunAsync();
        Assert.Equal(3, (await rig.ArchivedMessageIdsAsync()).Count);
    }

    [Fact]
    public async Task AutomaticFullSync_DoesNotRestartAFullSyncThatIsStillResuming()
    {
        // LastSync at 1970 = a full sync that was paused (bandwidth limit, timeout). It has to resume, not start over every interval.
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        for (var i = 0; i < 4; i++) rig.AddMail();
        await SeedCheckpointAsync(rig, lastUid: 2);

        await using (var ctx = rig.Kit.NewContext())
            await FullSyncRequest.RequestAsync(ctx, ServiceFactory.CreateBandwidthService(ctx), rig.Account.Id, FullSyncRequest.ShouldClearForAutomatic(FullSyncRequest.EpochUtc));

        Assert.Single(await rig.CheckpointsAsync());
        var mark = rig.Server.Commands.Count;
        await rig.RunAsync();
        Assert.Equal(new HashSet<uint> { 3, 4 }, FetchedUids(rig.Server, mark));
    }

    [Fact]
    public async Task PausedFullSync_ResumesOnTheNextRun_AndFinishesEverything()
    {
        // The multi-day resume: Gmail's own bandwidth limit stops the first run part-way, the second (the next day) continues from the checkpoint.
        using var rig = new SyncRig(_fixture) { BatchSize = 2 }; await rig.InitAsync();
        rig.Bandwidth.Enabled = true; rig.Bandwidth.DailyLimitMb = 1;
        var big = new string('a', 450_000);
        for (var i = 0; i < 6; i++) rig.AddMail(big + i);

        var paused = await rig.RunAsync();

        Assert.Equal(FullSyncRequest.EpochUtc, paused.LastSync);
        var cp = Assert.Single(await rig.CheckpointsAsync());
        Assert.Equal(2L, cp.LastUid);
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);

        await rig.ResetBandwidthAsync();
        rig.Bandwidth.DailyLimitMb = 1000;
        var mark = rig.Server.Commands.Count;
        var done = await rig.RunAsync();

        Assert.Equal(6, (await rig.ArchivedMessageIdsAsync()).Count);
        Assert.Equal(new HashSet<uint> { 3, 4, 5, 6 }, FetchedUids(rig.Server, mark));
        Assert.True(done.LastSync > DateTime.UtcNow.AddMinutes(-5));
        Assert.Empty(await rig.CheckpointsAsync());
    }
}
