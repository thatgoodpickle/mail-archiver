using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Review findings F-01 (tri-state result), F-02 (calendar mail under Lean + Required) and F-19 (no ghost original row).</summary>
[Collection(TestDbFixture.CollectionName)]
public class ReviewFixesArchiveTests : IDisposable
{
    private readonly ReviewKit _kit;
    public ReviewFixesArchiveTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public void Dispose() => _kit.Dispose();

    [Fact]
    public async Task F01_RefusedIsNotADuplicate_AndTheSameMessageIsArchivedOnceTheCauseIsGone()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = ReviewKit.BuildMail();
            var wired = _kit.Make(ctx, lean: true, required: true);

            // First run: the provider delivered no original -> Refused, nothing saved.
            Assert.Equal(ArchiveOutcome.Refused, await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", rawMessage: null));
            ctx.ChangeTracker.Clear();
            Assert.Equal(0, await ctx.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id));

            // Next run: the same message, now with its original -> Archived (it was never treated as "already archived").
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
            // And now it IS a duplicate.
            Assert.Equal(ArchiveOutcome.Duplicate, await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public void F01_TheSyncExceptionSaysItWillBeReadAgain()
    {
        Assert.Contains("read again", new ArchiveNotStoredException(ArchiveOutcome.Refused).Message);
        Assert.Contains("read again", new ArchiveNotStoredException(ArchiveOutcome.Failed).Message);
    }

    [Fact]
    public async Task F01_TheBoolWrapperStillSaysTrueOnlyForANewEmail()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = ReviewKit.BuildMail();
            var wired = _kit.Make(ctx, lean: true, required: true);
            Assert.False(await wired.Svc.ArchiveEmailAsync(account, msg, false, "INBOX", rawMessage: null));
            Assert.True(await wired.Svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
            Assert.False(await wired.Svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task F02_CalendarMail_UnderLeanAndRequired_IsArchivedWithItsVerifiedOriginal(bool required)
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = ReviewKit.BuildMail(withCalendar: true);
            var wired = _kit.Make(ctx, lean: true, required: required);

            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));

            ctx.ChangeTracker.Clear();
            var email = await ctx.ArchivedEmails.Include(e => e.RawMessage).Include(e => e.Attachments).SingleAsync(e => e.MailAccountId == account.Id);
            Assert.False(email.ContentStripped);                       // stored in full: the .ics is not part of the original
            Assert.NotNull(email.RawMessage);                         // but the original was kept ...
            Assert.Equal(RawMessageCodec.ComputeSha256(bytes), email.RawMessage!.Sha256); // ... verified and hash-named
            Assert.EndsWith($"{email.RawMessage.Sha256}.eml.gz", email.RawMessage.StoragePath);
            Assert.Contains(email.Attachments, a => (a.FileName ?? "").EndsWith(".ics", StringComparison.OrdinalIgnoreCase));

            var report = await wired.Integrity.VerifyAsync(email.Id);
            Assert.Equal(IntegrityStatus.Valid, report.Status);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F19_UnderRequired_AFailedOriginalWrite_LeavesNoEmailAndNoGhostRow()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = ReviewKit.BuildMail();
            // Not lean: the original is written AFTER the email row is saved, so a write failure is possible.
            var wired = _kit.Make(ctx, lean: false, required: true, hashNamed: false);
            // Make the target directory impossible to create: a FILE where the shard directory must go.
            Directory.CreateDirectory(_kit.Root);
            foreach (var n in Enumerable.Range(0, 100)) File.WriteAllText(Path.Combine(_kit.Root, n.ToString("00")), "blocked");

            var outcome = await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap));
            Assert.Equal(ArchiveOutcome.Failed, outcome);

            ctx.ChangeTracker.Clear();
            Assert.Equal(0, await ctx.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id)); // taken back out
            Assert.Equal(0, await ctx.RawMessages.CountAsync(r => r.ArchivedEmail.MailAccountId == account.Id));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F08_WhenLeanCannotBeCompleted_ButTheOriginalIsVerified_UnderRequiredItIsStoredInFullNotRefused()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            // The parsed message carries an attachment that is NOT in the captured original (as after a header recovery
            // or an API-sourced copy): attachments cannot be found again, so lean is impossible, but the original is fine.
            var (msg, _, _) = ReviewKit.BuildMail(withPdf: true);
            var (_, otherBytes, _) = ReviewKit.BuildMail(withPdf: false);
            var wired = _kit.Make(ctx, lean: true, required: true);

            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", new RawMessageCapture(otherBytes, RawMessageSource.Imap)));

            ctx.ChangeTracker.Clear();
            var email = await ctx.ArchivedEmails.Include(e => e.RawMessage).SingleAsync(e => e.MailAccountId == account.Id);
            Assert.False(email.ContentStripped);
            Assert.Equal(RawMessageCodec.ComputeSha256(otherBytes), email.RawMessage!.Sha256);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Theory]
    [InlineData(false, ArchiveOutcome.Refused)]
    [InlineData(true, ArchiveOutcome.Archived)]
    public async Task F09_UnderRequired_AReserializedOriginalIsRefusedUnlessSerializedImportsAreAllowed(bool allow, ArchiveOutcome expected)
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = ReviewKit.BuildMail();
            var wired = _kit.Make(ctx, lean: true, required: true, allowSerialized: allow);
            Assert.Equal(expected, await wired.Svc.ArchiveEmailOutcomeAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Serialized)));

            if (expected == ArchiveOutcome.Archived)
            {
                ctx.ChangeTracker.Clear();
                var id = await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).Select(e => e.Id).SingleAsync();
                var report = await wired.Integrity.VerifyAsync(id);
                Assert.Equal(IntegrityStatus.Valid, report.Status);
                Assert.Contains(report.NotCompared, n => n.Contains("re-serialized"));   // labelled, not hidden
            }
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }
}
