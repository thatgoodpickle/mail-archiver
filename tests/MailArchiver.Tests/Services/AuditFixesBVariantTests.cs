using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Audit finding 18: a re-serialized copy and the normally fetched original of one server message are one email.</summary>
[Collection(TestDbFixture.CollectionName)]
public class AuditFixesBVariantTests : IDisposable
{
    private readonly ReviewKit _kit;
    public AuditFixesBVariantTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public void Dispose() => _kit.Dispose();

    private static (MimeKit.MimeMessage Message, byte[] Bytes) WithHeader(string header, byte[] bytes)
    {
        var all = System.Text.Encoding.ASCII.GetBytes(header + "\r\n").Concat(bytes).ToArray();
        using var ms = new MemoryStream(all);
        return (MimeKit.MimeMessage.Load(ms), all);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task F18_ARecoveredSerializedCopy_AndTheNormallyFetchedOriginal_AreOneEmail_NotTwo(bool serializedFirst)
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (m, b, _) = ReviewKit.BuildMail();
            var (real, realBytes) = WithHeader("Received: from mx.example.test by gmail with SMTP", b);
            var serialized = RawMessageCodec.Serialize(m);
            // The two copies really differ byte for byte (a server adds Received headers), so the hashes differ.
            Assert.NotEqual(RawMessageCodec.ComputeSha256(realBytes), RawMessageCodec.ComputeSha256(serialized));
            var wired = _kit.Make(ctx, lean: false, required: false, hashNamed: false);

            var firstCapture = serializedFirst ? new RawMessageCapture(serialized, RawMessageSource.Serialized) : new RawMessageCapture(realBytes, RawMessageSource.Imap);
            var secondCapture = serializedFirst ? new RawMessageCapture(realBytes, RawMessageSource.Imap) : new RawMessageCapture(serialized, RawMessageSource.Serialized);
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, serializedFirst ? m : real, false, "INBOX", firstCapture));
            Assert.Equal(ArchiveOutcome.Duplicate, await wired.Svc.ArchiveEmailOutcomeAsync(account, serializedFirst ? real : m, false, "INBOX", secondCapture));
            await using var check = _kit.NewContext();
            Assert.Equal(1, await check.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F18_TwoDifferentRealOriginalsWithTheSameMessageId_AreStillTwoEmails()
    {
        var ctx = _kit.NewContext();
        var account = await ReviewKit.SeedAccountAsync(ctx);
        try
        {
            var (m, b, _) = ReviewKit.BuildMail();
            var (other, otherBytes) = WithHeader("X-List-Footer: added by a list", b);
            var wired = _kit.Make(ctx, lean: false, required: false, hashNamed: false);
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, m, false, "INBOX", new RawMessageCapture(b, RawMessageSource.Imap)));
            Assert.Equal(ArchiveOutcome.Archived, await wired.Svc.ArchiveEmailOutcomeAsync(account, other, false, "INBOX", new RawMessageCapture(otherBytes, RawMessageSource.Imap)));
            await using var check = _kit.NewContext();
            Assert.Equal(2, await check.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }
}
