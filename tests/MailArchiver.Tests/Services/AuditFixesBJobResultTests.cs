using MailArchiver.Models;
using MailArchiver.Tests.Infrastructure;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>The job result shows how many messages are set aside (quarantined).</summary>
[Collection(TestDbFixture.CollectionName)]
public class AuditFixesBJobResultTests
{
    private readonly TestDbFixture _fixture;
    public AuditFixesBJobResultTests(TestDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheJobResult_CountsQuarantinedMessages()
    {
        using var rig = new SyncRig(_fixture); await rig.InitAsync();
        rig.AddMail(); var bad = rig.AddMail(); rig.AddMail();
        rig.Server.OnBodyFetch = (_, uid) => uid == bad.Uid ? (FakeImapServer.FetchAction.No, "Invalid message data") : (FakeImapServer.FetchAction.Normal, "");
        for (var i = 0; i < 3; i++) await rig.RunAsync();   // quarantined on the third run

        var run = rig.NewRun();
        try
        {
            var jobId = run.Jobs!.StartSync(rig.Account.Id, rig.Account.Name);
            await run.Service.SyncMailAccountAsync(run.Account, jobId);
            Assert.Equal(1, run.Jobs.GetJob(jobId)!.QuarantinedEmails);
        }
        finally { await run.Ctx.DisposeAsync(); }
    }
}
