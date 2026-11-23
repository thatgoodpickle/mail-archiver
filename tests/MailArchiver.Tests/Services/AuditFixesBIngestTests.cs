using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit findings 10 (failed save leaves rows in the shared context) and 20 follow-up (no archived email without its original.stored entry).</summary>
[Collection(TestDbFixture.CollectionName)]
public class AuditFixesBIngestTests : IDisposable
{
    private readonly ReviewKit _kit;
    public AuditFixesBIngestTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public void Dispose() => _kit.Dispose();

    /// <summary>Fails the first N saves that carry a new ArchivedEmail, as a transient database error would.</summary>
    private sealed class FailingSaves : SaveChangesInterceptor
    {
        public int Remaining;
        public int Failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Remaining > 0 && eventData.Context!.ChangeTracker.Entries<ArchivedEmail>().Any(e => e.State == EntityState.Added))
            {
                Remaining--; Failed++;
                throw new DbUpdateException("simulated transient failure", new TimeoutException("simulated"));
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F10_AFailedSave_DoesNotLetTheNextSaveCommitAnEmailWithoutItsOriginal(bool lean)
    {
        var failing = new FailingSaves { Remaining = 1 };
        var ctx = _kit.NewContext(withChain: false, failing);
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (m1, b1, _) = ReviewKit.BuildMail();
            var (m2, b2, _) = ReviewKit.BuildMail();
            var wired = _kit.Make(ctx, lean: lean, required: true, hashNamed: lean);

            Assert.Equal(ArchiveOutcome.Failed, await wired.Svc.ArchiveEmailOutcomeAsync(account, m1, false, "INBOX", new RawMessageCapture(b1, RawMessageSource.Imap)));
            Assert.Equal(1, failing.Failed);
            // Nothing from the failed message stays tracked in the shared context.
            Assert.DoesNotContain(ctx.ChangeTracker.Entries(), e => e.State == EntityState.Added);

            // The very next message is saved correctly, and an unrelated save (like a failure record or checkpoint) commits nothing extra.
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, m2, false, "INBOX", new RawMessageCapture(b2, RawMessageSource.Imap)));
            await ctx.SaveChangesAsync();

            await using var check = _kit.NewContext();
            var rows = await check.ArchivedEmails.AsNoTracking().Where(e => e.MailAccountId == account.Id).Select(e => new { e.MessageId, HasRaw = e.RawMessage != null }).ToListAsync();
            var only = Assert.Single(rows);
            Assert.Equal(m2.MessageId, only.MessageId);
            Assert.True(only.HasRaw);
            Assert.Equal(0, await check.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id && e.RawMessage == null));

            // And the failed message is archived normally when it is fetched again.
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, m1, false, "INBOX", new RawMessageCapture(b1, RawMessageSource.Imap)));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    /// <summary>A chain that loses its appends while <see cref="Down"/> is set (as a lock timeout would), and writes through to the real one otherwise.</summary>
    private sealed class FlakyChain : IAuditChain
    {
        private readonly IAuditChain _real;
        public bool Down; public bool EnabledFlag = true;
        public FlakyChain(IAuditChain real) => _real = real;
        public bool Enabled => EnabledFlag;
        public Task<AuditChainEntry?> AppendAsync(string kind, int? emailId = null, string? sha256 = null, object? payload = null, CancellationToken cancellationToken = default)
            => Down || !EnabledFlag ? Task.FromResult<AuditChainEntry?>(null) : _real.AppendAsync(kind, emailId, sha256, payload, cancellationToken);
    }

    private MailArchiverDbContextHolder Flaky(out FlakyChain chain)
    {
        chain = new FlakyChain(_kit.Chain);
        var interceptor = new AuditChainInterceptor(new ServiceCollection().AddSingleton<IAuditChain>(chain).BuildServiceProvider());
        return new MailArchiverDbContextHolder(_kit.NewContext(withChain: false, interceptor));
    }

    private sealed record MailArchiverDbContextHolder(MailArchiver.Data.MailArchiverDbContext Ctx);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task F20_AnEmailWhoseOriginalStoredEntryWasLost_IsNotArchived_ButFailsAndIsArchivedOnTheRetry(bool lean, bool required)
    {
        var holder = Flaky(out var chain);
        var ctx = holder.Ctx;
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (m, b, _) = ReviewKit.BuildMail();
            var wired = _kit.Make(ctx, lean: lean, required: required, hashNamed: lean);
            chain.Down = true;
            var outcome = await wired.Svc.ArchiveEmailOutcomeAsync(account, m, false, "INBOX", new RawMessageCapture(b, RawMessageSource.Imap));
            Assert.Equal(ArchiveOutcome.Failed, outcome);
            // The sync turns Failed into ArchiveNotStoredException, which the classifier never counts towards the quarantine.
            Assert.False(MailArchiver.Services.Providers.Imap.SyncFailureClassifier.CountsTowardsQuarantine(new ArchiveNotStoredException(outcome)));
            await using (var check = _kit.NewContext())
                Assert.Equal(0, await check.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id));

            chain.Down = false;
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, m, false, "INBOX", new RawMessageCapture(b, RawMessageSource.Imap)));
            await using var check2 = _kit.NewContext();
            var email = await check2.ArchivedEmails.AsNoTracking().SingleAsync(e => e.MailAccountId == account.Id);
            Assert.Equal(1, await check2.AuditChain.CountAsync(c => c.Kind == "original.stored" && c.EmailId == email.Id));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F20_WithTheChainSwitchedOff_ANullAppendIsNotAFailure()
    {
        var holder = Flaky(out var chain);
        var ctx = holder.Ctx;
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            chain.EnabledFlag = false;
            var (m, b, _) = ReviewKit.BuildMail();
            var wired = _kit.Make(ctx, lean: false, required: true, hashNamed: false);
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, m, false, "INBOX", new RawMessageCapture(b, RawMessageSource.Imap)));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }
}
