using System.Text.RegularExpressions;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailArchiver.Tests.Services;

public class CoverageClassifierTests
{
    private static ServerMessageKey K(string? gid = "1", string? mid = "<a@x>", long? size = 100, string folder = "All", long validity = 5, long uid = 1) => new(gid, mid, size, folder, validity, uid);

    private static CoverageHeld Held() => new();

    [Fact] public void ArchivedByGmailId() { var h = Held(); h.AddArchived("1", "<other@x>", 9, "All"); Assert.Equal(CoverageBucket.Archived, CoverageClassifier.Classify(K(), h, gmail: true)); }

    [Fact] public void ArchivedRowWithoutGmailId_FallsBackToMessageIdAndSize()   // rows archived before the labels feature
    {
        var h = Held(); h.AddArchived(null, "<a@x>", 100, "INBOX");
        Assert.Equal(CoverageBucket.Archived, CoverageClassifier.Classify(K(), h, gmail: true));
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(mid: "<a@x>", size: 101), h, gmail: true));
    }

    [Fact] public void ARowWithAGmailId_DoesNotMatchAnotherGmailMessageByMessageId()
    {
        var h = Held(); h.AddArchived("2", "<a@x>", 100, "All");   // same Message-ID and size but a different Gmail message
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(gid: "1"), h, gmail: true));
    }

    [Fact] public void PlainImap_MatchesOnMessageIdSizeAndFolder()
    {
        var h = Held(); h.AddArchived(null, "<a@x>", 100, "INBOX");
        Assert.Equal(CoverageBucket.Archived, CoverageClassifier.Classify(K(gid: null, folder: "INBOX"), h, gmail: false));
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(gid: null, folder: "Other"), h, gmail: false));
    }

    [Fact] public void DeletedByYou_ByGmailId_ByFallback_AndPlain()
    {
        var m = new DeletedMessageMarker { GmailMessageId = "1", MessageId = "<z@x>", Size = 5, Folder = "All" };
        var h = Held(); h.AddMarker(m);
        Assert.Equal(CoverageBucket.DeletedByYou, CoverageClassifier.Classify(K(), h, gmail: true));

        var old = Held(); old.AddMarker(new DeletedMessageMarker { GmailMessageId = null, MessageId = "<a@x>", Size = 100, Folder = "INBOX" });
        Assert.Equal(CoverageBucket.DeletedByYou, CoverageClassifier.Classify(K(), old, gmail: true));
        Assert.Equal(CoverageBucket.DeletedByYou, CoverageClassifier.Classify(K(gid: null, folder: "INBOX"), old, gmail: false));
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(gid: null, folder: "Other"), old, gmail: false));
    }

    [Fact] public void Quarantined_ByGmailId_OrByFolderUidValidityAndUid()
    {
        var h = Held();
        h.AddProblem(new SyncProblemMessage { FolderName = "All", UidValidity = 5, Uid = 1 });
        Assert.Equal(CoverageBucket.Quarantined, CoverageClassifier.Classify(K(gid: null, uid: 1), h, gmail: false));
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(gid: null, uid: 2), h, gmail: false));
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(gid: null, uid: 1, validity: 6), h, gmail: false));   // renumbered folder: another message

        var g = Held(); g.AddProblem(new SyncProblemMessage { FolderName = "x", UidValidity = 1, Uid = 99, GmailMessageId = "1" });
        Assert.Equal(CoverageBucket.Quarantined, CoverageClassifier.Classify(K(uid: 7), g, gmail: true));
    }

    [Fact] public void FilteredOut_IsTheServerMessagesThatAreNOTInTheFilterKeepSet()   // audit F: the spec had it inverted
    {
        var h = Held(); h.FilterKeepGmailIds = new HashSet<string> { "1" };               // what -label:Marketing returns: the KEEP set
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(gid: "1"), h, gmail: true));  // kept by the filter, not archived yet
        Assert.Equal(CoverageBucket.FilteredOut, CoverageClassifier.Classify(K(gid: "2"), h, gmail: true)); // not in the keep set: tagged Marketing
    }

    [Fact] public void NoFilter_NothingIsFilteredOut()
    {
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(), Held(), gmail: true));
        var plain = Held(); plain.FilterKeepGmailIds = new HashSet<string>();
        Assert.Equal(CoverageBucket.NotYetSeen, CoverageClassifier.Classify(K(gid: null), plain, gmail: false));   // never for other servers
    }

    [Fact] public void Order_ArchivedBeatsEverything_ForAMessageLabelledMarketingAfterItWasArchived()
    {
        var h = Held();
        h.AddArchived("1", "<a@x>", 100, "All");
        h.FilterKeepGmailIds = new HashSet<string>();                                     // now outside the filter's keep set
        h.AddProblem(new SyncProblemMessage { FolderName = "All", UidValidity = 5, Uid = 1, GmailMessageId = "1" });
        h.AddMarker(new DeletedMessageMarker { GmailMessageId = "1", MessageId = "<a@x>", Size = 100, Folder = "All" });
        Assert.Equal(CoverageBucket.Archived, CoverageClassifier.Classify(K(), h, gmail: true));
    }

    [Fact] public void Order_DeletedBeatsQuarantinedBeatsFilteredOut()
    {
        var h = Held();
        h.FilterKeepGmailIds = new HashSet<string>();
        h.AddProblem(new SyncProblemMessage { FolderName = "All", UidValidity = 5, Uid = 1, GmailMessageId = "1" });
        Assert.Equal(CoverageBucket.Quarantined, CoverageClassifier.Classify(K(), h, gmail: true));
        h.AddMarker(new DeletedMessageMarker { GmailMessageId = "1", MessageId = "<a@x>", Size = 100, Folder = "All" });
        Assert.Equal(CoverageBucket.DeletedByYou, CoverageClassifier.Classify(K(), h, gmail: true));
    }

    [Fact] public void EveryKeyLandsInExactlyOneBucket_ForManyCombinations()
    {
        var rnd = new Random(7);
        var h = Held();
        h.FilterKeepGmailIds = Enumerable.Range(0, 100).Where(i => i % 3 == 0).Select(i => i.ToString()).ToHashSet();
        for (var i = 0; i < 100; i++)
        {
            if (rnd.Next(4) == 0) h.AddArchived(i.ToString(), $"<{i}@x>", 100 + i, "All");
            if (rnd.Next(5) == 0) h.AddMarker(new DeletedMessageMarker { GmailMessageId = i.ToString(), MessageId = $"<{i}@x>", Size = 100 + i, Folder = "All" });
            if (rnd.Next(6) == 0) h.AddProblem(new SyncProblemMessage { FolderName = "All", UidValidity = 5, Uid = i, GmailMessageId = i.ToString() });
        }
        var counts = new int[5];
        for (var i = 0; i < 100; i++) counts[(int)CoverageClassifier.Classify(K(gid: i.ToString(), mid: $"<{i}@x>", size: 100 + i, uid: i), h, gmail: true)]++;
        Assert.Equal(100, counts.Sum());
    }
}

[Collection(TestDbFixture.CollectionName)]
public class CoverageServiceTests : IDisposable
{
    private readonly ReviewKit _kit;
    public CoverageServiceTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public void Dispose() => _kit.Dispose();

    private sealed class FakeSource : IServerKeySource
    {
        public ServerListing Listing = null!;
        public Task<ServerListing> ListAsync(MailAccount account, CancellationToken ct = default) => Task.FromResult(Listing);
    }

    [Fact]
    public async Task TheSumBalances_WithArchivedDeletedQuarantinedFilteredAndNotYetSeen_AndTheExtraLinesAreRight()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        account.ImapServer = "imap.gmail.com";
        await ctx.SaveChangesAsync();
        try
        {
            RawMessage Raw(int size) => new() { Sha256 = Guid.NewGuid().ToString("N").PadRight(64, '0'), Size = size, Source = "imap", StorageType = RawMessageStorage.Postgres, Content = new byte[] { 1 }, CreatedAt = DateTime.UtcNow };
            var a1 = DeleteReasonTests.NewEmail(account.Id, "1001"); a1.RawMessage = Raw(100);
            var a2 = DeleteReasonTests.NewEmail(account.Id, "1002"); a2.RawMessage = Raw(100);
            var dupe = DeleteReasonTests.NewEmail(account.Id, "1002"); dupe.RawMessage = Raw(101);          // archived twice
            var gone = DeleteReasonTests.NewEmail(account.Id, "1999"); gone.RawMessage = Raw(100);            // no longer on the server
            var old = DeleteReasonTests.NewEmail(account.Id, null); old.RawMessage = Raw(777);                // archived before Gmail ids were recorded
            var del = DeleteReasonTests.NewEmail(account.Id, "1003"); del.RawMessage = Raw(100);
            ctx.ArchivedEmails.AddRange(a1, a2, dupe, gone, old, del);
            await ctx.SaveChangesAsync();
            var delId = del.Id;
            ctx.ChangeTracker.Clear();
            await DeleteReasonScope.RunAsync(ctx, DeleteReasons.User, async () =>
            {
                ctx.ArchivedEmails.RemoveRange(await ctx.ArchivedEmails.Where(e => e.Id == delId).ToListAsync());
                await ctx.SaveChangesAsync();
            });
            ctx.SyncProblemMessages.Add(new SyncProblemMessage { MailAccountId = account.Id, FolderName = "[Gmail]/All Mail", UidValidity = 9, Uid = 4, GmailMessageId = "1004", Reason = "boom", Status = SyncProblemMessage.Quarantined, FailCount = 3 });
            await ctx.SaveChangesAsync();

            ServerMessageKey S(string? gid, string mid, long size, long uid) => new(gid, mid, size, "[Gmail]/All Mail", 9, uid);
            var listing = new ServerListing(new[]
            {
                S("1001", a1.MessageId, 100, 1),          // archived
                S("1002", a2.MessageId, 100, 2),          // archived (twice in the archive)
                S("1003", del.MessageId, 100, 3),         // deleted by you
                S("1004", "<q@x>", 50, 4),                // quarantined
                S("1005", "<m@x>", 60, 5),                // tagged Marketing: not in the keep set
                S("1006", "<n@x>", 70, 6),                // not yet seen
                S(null, old.MessageId, 777, 7),           // archived row from before Gmail ids: matched by Message-ID + size
            }, new HashSet<string> { "1001", "1002", "1003", "1004", "1006" }, new[] { "[Gmail]/All Mail" });

            var svc = new CoverageService(_kit.NewContext(), new FakeSource { Listing = listing });
            var r = await svc.RunAsync(account);

            Assert.Equal(7, r.ServerTotal);
            Assert.Equal(3, r.Buckets[CoverageBucket.Archived]);
            Assert.Equal(1, r.Buckets[CoverageBucket.DeletedByYou]);
            Assert.Equal(1, r.Buckets[CoverageBucket.Quarantined]);
            Assert.Equal(1, r.Buckets[CoverageBucket.FilteredOut]);
            Assert.Equal(1, r.Buckets[CoverageBucket.NotYetSeen]);
            Assert.True(r.Balanced);
            Assert.Equal(1, r.ArchivedNoLongerOnServer);   // 1999 (the Gmail-id-less row has no id to compare)
            Assert.Equal(1, r.ArchivedTwice);              // 1002
            var seen = Assert.Single(r.NotYetSeen);
            Assert.Equal("<n@x>", seen.Key.MessageId);
            Assert.StartsWith("https://mail.google.com/mail/u/", seen.SearchLink);
            Assert.Contains("rfc822msgid", Uri.UnescapeDataString(seen.SearchLink!));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task AQuarantinedFailure_RecordsTheGmailIdAndTheFolderUidValidity()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var svc = new SyncProblemService(_kit.NewContext(), new MutableOptionsMonitor<SyncQuarantineOptions>(new SyncQuarantineOptions { QuarantineAfterFailures = 1 }));
            await svc.RecordFailureAsync(account.Id, "[Gmail]/All Mail", 42, 7, "<m@x>", "boom", default, "1700000000000000009");
            await using var read = _kit.NewContext();
            var row = await read.SyncProblemMessages.AsNoTracking().SingleAsync(p => p.MailAccountId == account.Id);
            Assert.Equal("1700000000000000009", row.GmailMessageId);
            Assert.Equal(42, row.UidValidity);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }
}

public class ImapServerKeySourceIsReadOnlyTests
{
    [Fact]
    public void TheAdapter_OpensFoldersReadOnly_AndCallsNothingThatWrites()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var text = File.ReadAllText(Path.Combine(root, "Services", "Providers", "Imap", "ImapServerKeySource.cs"));
        Assert.Contains("FolderAccess.ReadOnly", text);
        Assert.DoesNotContain("FolderAccess.ReadWrite", text);
        Assert.DoesNotContain("CloseAsync(true", text);
        var forbidden = new Regex(@"\.(AddFlags|RemoveFlags|SetFlags|Store|Expunge|Append|CopyTo|MoveTo|Create(?!ImapClient)|Delete|Rename|Subscribe|Unsubscribe|AddLabels|RemoveLabels|SetLabels|Replace)\w*\s*\(");
        Assert.DoesNotMatch(forbidden, text);
        Assert.DoesNotContain("GetStream", text);       // no body is fetched
        Assert.DoesNotContain("GetMessage", text);
    }
}
