using MailArchiver.Models;
using MailArchiver.Services.Providers.Imap;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailKit;
using MailKit.Net.Imap;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit finding 3: only real per-message failures count towards the quarantine; the environment's failures do not.</summary>
public class SyncFailureClassifierTests
{
    [Fact] public void ContentFailures_Count()
    {
        Assert.True(SyncFailureClassifier.CountsTowardsQuarantine(new FormatException("Failed to parse message headers")));
        Assert.True(SyncFailureClassifier.CountsTowardsQuarantine(new MessageNotFoundException("gone")));
        Assert.True(SyncFailureClassifier.CountsTowardsQuarantine(new InvalidOperationException("odd message")));
        Assert.True(SyncFailureClassifier.CountsTowardsQuarantine(new Microsoft.EntityFrameworkCore.DbUpdateException("save", new Exception("invalid byte sequence for encoding UTF8: 0x00"))));
    }

    [Fact] public void EnvironmentFailures_DoNotCount()
    {
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new System.IO.IOException("connection reset")));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new ImapProtocolException("The IMAP server has unexpectedly disconnected.")));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new InvalidOperationException("Failed to reconnect to IMAP server", new System.Net.Sockets.SocketException())));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new OperationCanceledException()));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new TimeoutException()));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new ArchiveNotStoredException(ArchiveOutcome.Refused)));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new ArchiveNotStoredException(ArchiveOutcome.Failed)));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new DeletionMarkerCheckException("db down")));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new Microsoft.EntityFrameworkCore.DbUpdateException("save", new Exception("connection refused"))));
        Assert.False(SyncFailureClassifier.CountsTowardsQuarantine(new ServiceNotConnectedException()));
    }
}

[Collection(TestDbFixture.CollectionName)]
public class QuarantineCountingSyncTests
{
    private readonly TestDbFixture _fixture;
    public QuarantineCountingSyncTests(TestDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AMessageTheServerRefusesEveryTime_IsQuarantinedAfterThreeRuns_AndLastSyncMovesOnlyThen()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var a = rig.AddMail(); var bad = rig.AddMail(); var c = rig.AddMail();
        rig.Server.OnBodyFetch = (_, uid) => uid == bad.Uid ? (FakeImapServer.FetchAction.No, "Invalid message data") : (FakeImapServer.FetchAction.Normal, "");

        var r1 = await rig.RunAsync();
        Assert.Equal(FullSyncRequest.EpochUtc, r1.LastSync);
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);
        Assert.Equal(1, Assert.Single(await rig.ProblemsAsync()).FailCount);

        var r2 = await rig.RunAsync();
        Assert.Equal(FullSyncRequest.EpochUtc, r2.LastSync);
        Assert.Equal(2, Assert.Single(await rig.ProblemsAsync()).FailCount);

        var r3 = await rig.RunAsync();
        var p = Assert.Single(await rig.ProblemsAsync());
        Assert.Equal(SyncProblemMessage.Quarantined, p.Status);
        Assert.True(r3.LastSync > DateTime.UtcNow.AddMinutes(-5));
        Assert.Equal(2, (await rig.ArchivedMessageIdsAsync()).Count);

        // Quarantined: not fetched again.
        var before = rig.Server.BodyFetches;
        await rig.RunAsync();
        Assert.Equal(before, rig.Server.BodyFetches);
    }

    [Fact]
    public async Task ADroppedConnection_ForThreeRuns_IsNotCounted_AndLastSyncStaysBehindTheMessage()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var a = rig.AddMail(); var victim = rig.AddMail(); var c = rig.AddMail();
        rig.Server.OnBodyFetch = (_, uid) => uid == victim.Uid ? (FakeImapServer.FetchAction.Drop, "") : (FakeImapServer.FetchAction.Normal, "");

        for (var i = 0; i < 4; i++)
        {
            var r = await rig.RunAsync();
            Assert.Equal(FullSyncRequest.EpochUtc, r.LastSync);
            Assert.Empty(await rig.ProblemsAsync());
        }
        Assert.DoesNotContain(victim.MessageId, await rig.ArchivedMessageIdsAsync());

        // The connection is healthy again: the message is archived and only now does LastSync move.
        rig.Server.OnBodyFetch = (_, _) => (FakeImapServer.FetchAction.Normal, "");
        var ok = await rig.RunAsync();
        Assert.Contains(victim.MessageId, await rig.ArchivedMessageIdsAsync());
        Assert.Equal(3, (await rig.ArchivedMessageIdsAsync()).Count);
        Assert.True(ok.LastSync > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task GmailsOwnBandwidthLimitAnswer_ForThreeRuns_IsNotCounted()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        var a = rig.AddMail(); var limited = rig.AddMail();
        rig.Server.OnBodyFetch = (_, uid) => uid == limited.Uid ? (FakeImapServer.FetchAction.No, "[OVERQUOTA] Account exceeded command or bandwidth limits. (Failure)") : (FakeImapServer.FetchAction.Normal, "");

        for (var i = 0; i < 4; i++)
        {
            var r = await rig.RunAsync();
            Assert.Equal(FullSyncRequest.EpochUtc, r.LastSync);
            Assert.Empty(await rig.ProblemsAsync());
        }
        Assert.Single(await rig.ArchivedMessageIdsAsync());
    }
}
