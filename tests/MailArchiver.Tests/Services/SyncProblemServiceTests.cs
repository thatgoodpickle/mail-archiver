using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

public class SyncPreCheckTests
{
    private static readonly (string, string, long?)[] Held = { ("id@x", "INBOX", 1000) };

    [Fact]
    public void SameIdSizeAndFolder_IsAlreadyArchived() => Assert.True(SyncPreCheck.AlreadyArchived(Held, "id@x", 1000, "INBOX", gmail: false));

    [Fact]
    public void ADifferentSize_IsDownloadedAndJudgedByItsHash() => Assert.False(SyncPreCheck.AlreadyArchived(Held, "id@x", 1001, "INBOX", gmail: false));

    [Fact]
    public void AnotherFolder_IsDownloadedOutsideGmail_ButSkippedInGmail()
    {
        Assert.False(SyncPreCheck.AlreadyArchived(Held, "id@x", 1000, "Archive", gmail: false));   // a moved message must update its folder
        Assert.True(SyncPreCheck.AlreadyArchived(Held, "id@x", 1000, "Label", gmail: true));       // Gmail copies never change the folder
    }

    [Fact]
    public void AnUnknownIdOrNoStoredSize_IsDownloaded()
    {
        Assert.False(SyncPreCheck.AlreadyArchived(Held, "other@x", 1000, "INBOX", gmail: false));
        Assert.False(SyncPreCheck.AlreadyArchived(new (string, string, long?)[] { ("id@x", "INBOX", null) }, "id@x", 1000, "INBOX", gmail: false));
    }

    [Fact]
    public void AGmailAccountGetsTheMarketingFilterByDefault_AndBlankMeansNoFilter()
    {
        Assert.Equal("-label:Marketing", GmailAccount.EffectiveFilter(new MailAccount { ImapServer = "imap.gmail.com" }));                       // never set: the default
        Assert.Equal("-label:Marketing", GmailAccount.DefaultFilter);
        Assert.Null(GmailAccount.EffectiveFilter(new MailAccount { ImapServer = "imap.gmail.com", GmailSearchFilter = "" }));                    // explicitly none
        Assert.Null(GmailAccount.EffectiveFilter(new MailAccount { ImapServer = "imap.gmail.com", GmailSearchFilter = "   " }));
        Assert.Equal("-label:Marketing -from:boss@example.test", GmailAccount.EffectiveFilter(new MailAccount { ImapServer = "imap.gmail.com", GmailSearchFilter = "  -label:Marketing -from:boss@example.test " }));
    }

    [Fact]
    public void OtherServersNeverFilter_WhateverIsStored()
    {
        Assert.Null(GmailAccount.EffectiveFilter(new MailAccount { ImapServer = "mail.example.test" }));
        Assert.Null(GmailAccount.EffectiveFilter(new MailAccount { ImapServer = "mail.example.test", GmailSearchFilter = "-label:Marketing" }));
        Assert.Null(GmailAccount.EffectiveFilter(new MailAccount { ImapServer = null }));
    }

    [Theory]
    [InlineData("imap.gmail.com", true)]
    [InlineData("IMAP.GMAIL.COM", true)]
    [InlineData("imap.googlemail.com", true)]
    [InlineData("mail.example.test", false)]
    [InlineData("", false)]
    public void GmailIsRecognisedByServerName(string host, bool expected)
        => Assert.Equal(expected, GmailAccount.Is(new MailAccount { ImapServer = host }));
}

/// <summary>The problem-message quarantine (second review N-02).</summary>
[Collection(TestDbFixture.CollectionName)]
public class SyncProblemServiceTests : IAsyncLifetime
{
    private readonly TestDbFixture _fixture;
    public SyncProblemServiceTests(TestDbFixture fixture) => _fixture = fixture;

    private MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    public async Task InitializeAsync() { await using var c = NewContext(); await c.SyncProblemMessages.ExecuteDeleteAsync(); }
    public async Task DisposeAsync() { await using var c = NewContext(); await c.SyncProblemMessages.ExecuteDeleteAsync(); }

    private static SyncProblemService Svc(MailArchiverDbContext c, int limit = 3)
        => new(c, new MutableOptionsMonitor<SyncQuarantineOptions>(new SyncQuarantineOptions { QuarantineAfterFailures = limit }));

    [Fact]
    public async Task ThirdFailureInARow_QuarantinesTheMessage_AndOnlyThatOne()
    {
        await using var c = NewContext();
        var s = Svc(c);
        Assert.False((await s.RecordFailureAsync(1, "INBOX", 7, 100, "<a@x>", "boom")).Quarantined);
        Assert.False((await s.RecordFailureAsync(1, "INBOX", 7, 100, "<a@x>", "boom")).Quarantined);
        var third = await s.RecordFailureAsync(1, "INBOX", 7, 100, "<a@x>", "boom again");
        Assert.True(third.Quarantined);
        Assert.Equal(3, third.FailCount);
        Assert.False((await s.RecordFailureAsync(1, "INBOX", 7, 101, null, "other")).Quarantined);   // another message starts at 1
        Assert.Equal(1, await s.CountQuarantinedAsync());
        Assert.Equal(1, await s.CountQuarantinedAsync(1));
        Assert.Equal(0, await s.CountQuarantinedAsync(2));
    }

    [Fact]
    public async Task AMessageThatSucceeds_LeavesTheList()
    {
        await using var c = NewContext();
        var s = Svc(c);
        await s.RecordFailureAsync(1, "INBOX", 7, 100, null, "boom");
        await s.ResolveAsync(1, "INBOX", 7, 100);
        Assert.Empty(await s.LoadAsync(1, "INBOX", 7));
        Assert.False((await s.RecordFailureAsync(1, "INBOX", 7, 100, null, "boom")).Quarantined);       // the count starts over
    }

    [Fact]
    public async Task ProblemsBelongToTheFoldersCurrentUidValidity()
    {
        await using var c = NewContext();
        var s = Svc(c);
        await s.RecordFailureAsync(1, "INBOX", 7, 100, null, "boom");
        Assert.Single(await s.LoadAsync(1, "INBOX", 7));
        Assert.Empty(await s.LoadAsync(1, "INBOX", 8));       // renumbered folder: the old UID means nothing
        Assert.Empty(await s.LoadAsync(1, "Other", 7));
    }

    [Fact]
    public async Task Retry_PutsQuarantinedMessagesBackIntoTheFlow_OneOrAll()
    {
        await using var c = NewContext();
        var s = Svc(c, limit: 1);
        await s.RecordFailureAsync(1, "INBOX", 7, 100, null, "a");
        await s.RecordFailureAsync(1, "INBOX", 7, 101, null, "b");
        await s.RecordFailureAsync(2, "INBOX", 7, 100, null, "c");
        Assert.Equal(3, await s.CountQuarantinedAsync());

        var one = (await s.ListAsync(10)).First(p => p.Uid == 100 && p.MailAccountId == 1);
        Assert.Equal(1, await s.RetryAsync(one.Id, null));
        var loaded = (await s.LoadAsync(1, "INBOX", 7))[100];
        Assert.Equal(SyncProblemMessage.Failing, loaded.Status);
        Assert.Equal(0, loaded.FailCount);
        Assert.Equal(2, await s.CountQuarantinedAsync());

        Assert.Equal(2, await s.RetryAsync(null, null));
        Assert.Equal(0, await s.CountQuarantinedAsync());
    }

    [Fact]
    public async Task TheReasonAndTheMessageIdAreKeptShort()
    {
        await using var c = NewContext();
        await Svc(c).RecordFailureAsync(1, "INBOX", 7, 100, new string('m', 2000), new string('r', 5000));
        var p = (await Svc(c).LoadAsync(1, "INBOX", 7))[100];
        Assert.True(p.Reason.Length <= 2000);
        Assert.True(p.MessageId!.Length <= 500);
    }

    [Fact]
    public void TheDefaultIsThreeFailures() => Assert.Equal(3, new SyncQuarantineOptions().QuarantineAfterFailures);
}
