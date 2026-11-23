using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Delete memory, step 3: the marker rules, and the FAIL CLOSED behaviour of both sync checks.</summary>
public class DeletionMarkerRulesTests
{
    private static DeletedMessageMarker M(string? gmailId = "100", string messageId = "<a@x>", long size = 500, string folder = "INBOX", string? sha = "ab") =>
        new() { Id = 1, AccountId = 1, GmailMessageId = gmailId, MessageId = messageId, Size = size, Folder = folder, Sha256 = sha, ChainSeq = 7, Reason = "user" };

    [Fact] public void Gmail_SameGmailId_Matches_EvenWithAnotherFolderOrId() =>
        Assert.True(DeletionMarkerRules.MatchesKey(M(), gmail: true, "100", "<other@x>", 9, "[Gmail]/All Mail"));

    [Fact] public void Gmail_DifferentGmailId_DoesNotMatch_EvenWhenMessageIdAndSizeAreTheSame() =>
        Assert.False(DeletionMarkerRules.MatchesKey(M(), gmail: true, "101", "<a@x>", 500, "INBOX"));

    [Fact] public void Gmail_WhenTheMarkerHasNoGmailId_FallsBackToMessageIdAndSize_IgnoringTheFolder() =>
        Assert.True(DeletionMarkerRules.MatchesKey(M(gmailId: null), gmail: true, "100", "<a@x>", 500, "[Gmail]/All Mail"));

    [Fact] public void Gmail_WhenTheServerGivesNoGmailId_FallsBackToMessageIdAndSize() =>
        Assert.True(DeletionMarkerRules.MatchesKey(M(), gmail: true, null, "<a@x>", 500, "anything"));

    [Fact] public void Plain_NeedsMessageIdSizeAndFolder()
    {
        Assert.True(DeletionMarkerRules.MatchesKey(M(), gmail: false, null, "<a@x>", 500, "INBOX"));
        Assert.False(DeletionMarkerRules.MatchesKey(M(), gmail: false, null, "<a@x>", 500, "Archive"));
        Assert.False(DeletionMarkerRules.MatchesKey(M(), gmail: false, null, "<b@x>", 500, "INBOX"));
    }

    [Fact] public void SameMessageIdWithAnotherSize_IsNotBlocked_TheDocumentedLimit()
    {
        Assert.False(DeletionMarkerRules.MatchesKey(M(), gmail: false, null, "<a@x>", 501, "INBOX"));
        Assert.False(DeletionMarkerRules.MatchesKey(M(gmailId: null), gmail: true, "100", "<a@x>", 501, "INBOX"));
    }

    [Theory]
    [InlineData(0L)]
    public void AMarkerWithoutAKnownSize_CanOnlyMatchByGmailId(long size)
    {
        var m = M(size: size);
        Assert.False(DeletionMarkerRules.MatchesKey(m, gmail: false, null, "<a@x>", 500, "INBOX"));
        Assert.True(DeletionMarkerRules.MatchesKey(m, gmail: true, "100", "<a@x>", 500, "INBOX"));
    }

    [Fact] public void MissingServerFields_NeverMatchByKey()
    {
        Assert.False(DeletionMarkerRules.MatchesKey(M(), gmail: false, null, null, 500, "INBOX"));
        Assert.False(DeletionMarkerRules.MatchesKey(M(), gmail: false, null, "<a@x>", null, "INBOX"));
    }

    [Fact] public void Hash_MatchesCaseInsensitively_AndNeverOnEmpty()
    {
        Assert.True(DeletionMarkerRules.MatchesHash(M(sha: "ABCD"), "abcd"));
        Assert.False(DeletionMarkerRules.MatchesHash(M(sha: null), "abcd"));
        Assert.False(DeletionMarkerRules.MatchesHash(M(sha: "abcd"), null));
        Assert.False(DeletionMarkerRules.MatchesHash(M(sha: "abcd"), ""));
        Assert.False(DeletionMarkerRules.MatchesHash(M(sha: "abcd"), "abce"));
    }
}

public class DeletionMarkerFailClosedTests
{
    private sealed class Fake : IDeletionMarkerService
    {
        public Func<int> Count = () => 1;
        public Func<IReadOnlyList<DeletedMessageMarker>> Candidates = () => Array.Empty<DeletedMessageMarker>();
        public Func<DeletedMessageMarker?> ByHash = () => null;
        public Task<int> CountAsync(int accountId, CancellationToken ct = default) => Task.FromResult(Count());
        public Task<IReadOnlyList<DeletedMessageMarker>> CandidatesAsync(int accountId, IReadOnlyCollection<string> g, IReadOnlyCollection<string> m, CancellationToken ct = default) => Task.FromResult(Candidates());
        public Task<DeletedMessageMarker?> FindByHashAsync(int accountId, string sha256, CancellationToken ct = default) => Task.FromResult(ByHash());
        public Task<IReadOnlyList<DeletedMessageMarker>> ListAsync(int accountId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ReleaseAsync(int accountId, int? markerId, string who, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static readonly IReadOnlyList<MarkerProbe> Probes = new[]
    {
        new MarkerProbe(1, "100", "<a@x>", 500, DateTime.UtcNow), new MarkerProbe(2, "200", "<b@x>", 600, DateTime.UtcNow)
    };

    private static Task<MarkerBatchCheck> Run(Fake f, Func<Task<IReadOnlyList<MarkerProbe>>>? describe = null)
        => MarkerBatchCheck.RunAsync(f, 1, gmail: true, "[Gmail]/All Mail", 2, describe ?? (() => Task.FromResult(Probes)));

    [Fact]
    public async Task NoMarkers_NothingIsDescribedOrChecked()
    {
        var called = false;
        var r = await Run(new Fake { Count = () => 0 }, () => { called = true; return Task.FromResult(Probes); });
        Assert.False(called);
        Assert.False(r.Active);
        r.ThrowIfUnchecked(1);   // nothing to enforce
    }

    [Fact]
    public async Task NoMarkerService_IsTheSameAsNoMarkers()
    {
        var r = await MarkerBatchCheck.RunAsync(null, 1, true, "x", 2, () => Task.FromResult(Probes));
        Assert.Same(MarkerBatchCheck.None, r);
    }

    [Fact]
    public async Task ADatabaseErrorWhileCounting_FailsEveryMessage_NeverImportsThem()
    {
        var r = await Run(new Fake { Count = () => throw new InvalidOperationException("db down") });
        Assert.NotNull(r.Error);
        var ex = Assert.Throws<DeletionMarkerCheckException>(() => r.ThrowIfUnchecked(1));
        Assert.Contains("not imported", ex.Message);
        Assert.Throws<DeletionMarkerCheckException>(() => r.ThrowIfUnchecked(2));
    }

    [Fact]
    public async Task ADatabaseErrorWhileLookingUpCandidates_FailsEveryMessage()
    {
        var r = await Run(new Fake { Candidates = () => throw new InvalidOperationException("db down") });
        Assert.Throws<DeletionMarkerCheckException>(() => r.ThrowIfUnchecked(1));
        Assert.Empty(r.Hits);
    }

    [Fact]
    public async Task AFailedDescriptionFetch_FailsEveryMessage_BecauseNothingWasChecked()
    {
        var r = await Run(new Fake(), () => throw new IOException("connection lost"));
        Assert.Throws<DeletionMarkerCheckException>(() => r.ThrowIfUnchecked(1));
    }

    [Fact]
    public async Task AMessageTheServerDidNotDescribe_FailsAlone_OthersAreChecked()
    {
        var r = await Run(new Fake(), () => Task.FromResult<IReadOnlyList<MarkerProbe>>(new[] { Probes[0] }));
        r.ThrowIfUnchecked(1);
        Assert.Throws<DeletionMarkerCheckException>(() => r.ThrowIfUnchecked(2));
    }

    [Fact]
    public async Task AMatchingMarker_IsAHit_AndOthersPass()
    {
        var marker = new DeletedMessageMarker { Id = 9, AccountId = 1, GmailMessageId = "100", MessageId = "<a@x>", Size = 500, Folder = "INBOX", ChainSeq = 3, Reason = "user" };
        var r = await Run(new Fake { Candidates = () => new[] { marker } });
        r.ThrowIfUnchecked(1); r.ThrowIfUnchecked(2);
        Assert.Equal(9, r.Hits[1].Marker.Id);
        Assert.False(r.Hits.ContainsKey(2));
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MarkerBatchCheck.RunAsync(new Fake { Count = () => throw new OperationCanceledException() }, 1, true, "x", 2, () => Task.FromResult(Probes)));
    }

    [Fact]
    public async Task TheHashCheckAfterTheDownload_DoesNotCatch_ADatabaseErrorPropagates()
    {
        var f = new Fake { ByHash = () => throw new InvalidOperationException("db down") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => DeletionMarkerHashCheck.FindAsync(f, 1, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task TheHashCheck_HashesTheDownloadedBytes()
    {
        string? seen = null;
        var f = new Fake();
        var spy = new HashSpy(f, h => seen = h);
        await DeletionMarkerHashCheck.FindAsync(spy, 1, new byte[] { 1, 2, 3 });
        Assert.Equal(RawMessageCodec.ComputeSha256(new byte[] { 1, 2, 3 }), seen);
    }

    private sealed class HashSpy : IDeletionMarkerService
    {
        private readonly IDeletionMarkerService _inner; private readonly Action<string> _seen;
        public HashSpy(IDeletionMarkerService inner, Action<string> seen) { _inner = inner; _seen = seen; }
        public Task<int> CountAsync(int a, CancellationToken c = default) => _inner.CountAsync(a, c);
        public Task<IReadOnlyList<DeletedMessageMarker>> CandidatesAsync(int a, IReadOnlyCollection<string> g, IReadOnlyCollection<string> m, CancellationToken c = default) => _inner.CandidatesAsync(a, g, m, c);
        public Task<DeletedMessageMarker?> FindByHashAsync(int a, string sha, CancellationToken c = default) { _seen(sha); return _inner.FindByHashAsync(a, sha, c); }
        public Task<IReadOnlyList<DeletedMessageMarker>> ListAsync(int a, int l, CancellationToken c = default) => _inner.ListAsync(a, l, c);
        public Task<int> ReleaseAsync(int a, int? m, string w, CancellationToken c = default) => _inner.ReleaseAsync(a, m, w, c);
    }
}

/// <summary>The real service against PostgreSQL, fed by real trigger-written markers.</summary>
[Collection(TestDbFixture.CollectionName)]
public class DeletionMarkerServiceTests : IDisposable
{
    private readonly ReviewKit _kit;
    public DeletionMarkerServiceTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task ADeletedEmail_IsFoundByGmailId_MessageId_AndByTheHashOfItsOriginal_AndOnlyInItsOwnAccount()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        var other = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (_, bytes, _) = ReviewKit.BuildMail();
            var e = DeleteReasonTests.NewEmail(account.Id, gmailId: "777", folder: "INBOX");
            e.RawMessage = new RawMessage { Sha256 = RawMessageCodec.ComputeSha256(bytes), Size = bytes.Length, Source = "imap", StorageType = RawMessageStorage.Postgres, Content = new byte[] { 1 }, CreatedAt = DateTime.UtcNow };
            ctx.ArchivedEmails.Add(e);
            await ctx.SaveChangesAsync();
            ctx.ChangeTracker.Clear();
            await DeleteReasonScope.RunAsync(ctx, DeleteReasons.User, async () =>
            {
                ctx.ArchivedEmails.RemoveRange(await ctx.ArchivedEmails.Where(x => x.Id == e.Id).ToListAsync());
                await ctx.SaveChangesAsync();
            });

            var svc = new DeletionMarkerService(_kit.NewContext());
            Assert.Equal(1, await svc.CountAsync(account.Id));
            Assert.Equal(0, await svc.CountAsync(other.Id));

            var viaGmail = await svc.CandidatesAsync(account.Id, new[] { "777" }, Array.Empty<string>());
            Assert.Single(viaGmail);
            var viaMessageId = await svc.CandidatesAsync(account.Id, Array.Empty<string>(), new[] { e.MessageId });
            Assert.Single(viaMessageId);
            Assert.Empty(await svc.CandidatesAsync(other.Id, new[] { "777" }, new[] { e.MessageId }));

            var hit = await DeletionMarkerHashCheck.FindAsync(svc, account.Id, bytes);
            Assert.NotNull(hit);
            Assert.Null(await DeletionMarkerHashCheck.FindAsync(svc, other.Id, bytes));
            Assert.Null(await DeletionMarkerHashCheck.FindAsync(svc, account.Id, new byte[] { 9, 9, 9 }));

            // The whole gate with real data: Gmail id decides.
            var check = await MarkerBatchCheck.RunAsync(svc, account.Id, gmail: true, "[Gmail]/All Mail", 1,
                () => Task.FromResult<IReadOnlyList<MarkerProbe>>(new[] { new MarkerProbe(5, "777", "<zzz@x>", 1, DateTime.UtcNow) }));
            Assert.True(check.Hits.ContainsKey(5));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); await using var c2 = _kit.NewContext(); await c2.MailAccounts.Where(a => a.Id == other.Id).ExecuteDeleteAsync(); }
    }
}
