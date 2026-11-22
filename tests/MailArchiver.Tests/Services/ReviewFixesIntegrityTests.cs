using System.IO.Compression;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Review findings F-03 (what Valid covers), F-04 (the chain decides), F-05 (Missing when the chain says stored) and F-10 (damaged originals are Invalid).</summary>
[Collection(TestDbFixture.CollectionName)]
public class ReviewFixesIntegrityTests : IAsyncLifetime, IDisposable
{
    private readonly ReviewKit _kit;
    public ReviewFixesIntegrityTests(TestDbFixture fixture) => _kit = new ReviewKit(fixture);
    public Task InitializeAsync() => _kit.ResetChainAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _kit.Dispose();

    private async Task<(MailArchiverDbContext Ctx, MailAccount Account, int EmailId, byte[] Bytes, ReviewKit.Wired Wired)> ArchiveAsync(
        bool lean, bool withPdf = true, bool withChain = false)
    {
        var ctx = _kit.NewContext(withChain);
        var account = await ReviewKit.SeedAccountAsync(ctx);
        var (msg, bytes, _) = ReviewKit.BuildMail(withPdf: withPdf);
        var wired = _kit.Make(ctx, lean);
        Assert.True(await wired.Svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
        // The chain interceptor appends after the save; give the (awaited) append a moment to be visible.
        ctx.ChangeTracker.Clear();
        var id = await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).Select(e => e.Id).SingleAsync();
        return (ctx, account, id, bytes, wired);
    }

    private static Task SqlAsync(MailArchiverDbContext ctx, string sql) => ctx.Database.ExecuteSqlRawAsync(sql);

    // ---------------- F-03: what the user sees must be proven ----------------

    [Theory]
    [InlineData(false, "UPDATE mail_archiver.\"ArchivedEmails\" SET \"HtmlBody\" = '<p>forged</p>' WHERE \"Id\" = {id}", "HTML body")]
    [InlineData(false, "UPDATE mail_archiver.\"ArchivedEmails\" SET \"SentDate\" = \"SentDate\" + interval '3 days' WHERE \"Id\" = {id}", "sent date")]
    [InlineData(false, "UPDATE mail_archiver.\"ArchivedEmails\" SET \"FromDisplayName\" = 'Somebody Else' WHERE \"Id\" = {id}", "display name")]
    [InlineData(false, "UPDATE mail_archiver.\"ArchivedEmails\" SET \"ToDisplayNames\" = 'Somebody Else' WHERE \"Id\" = {id}", "display names")]
    [InlineData(false, "UPDATE mail_archiver.\"EmailAttachments\" SET \"FileName\" = 'renamed.pdf' WHERE \"ArchivedEmailId\" = {id}", "file name")]
    [InlineData(false, "UPDATE mail_archiver.\"EmailAttachments\" SET \"ContentType\" = 'application/x-evil' WHERE \"ArchivedEmailId\" = {id}", "content type")]
    [InlineData(false, "UPDATE mail_archiver.\"EmailAttachments\" SET \"Size\" = 5 WHERE \"ArchivedEmailId\" = {id}", "size")]
    [InlineData(false, "DELETE FROM mail_archiver.\"EmailAttachments\" WHERE \"ArchivedEmailId\" = {id}", "missing from the database copy")]
    [InlineData(true, "DELETE FROM mail_archiver.\"EmailAttachments\" WHERE \"ArchivedEmailId\" = {id}", "missing from the database copy")]
    [InlineData(true, "UPDATE mail_archiver.\"ArchivedEmails\" SET \"HtmlBody\" = '<p>forged</p>' WHERE \"Id\" = {id}", "lean email carries an HTML body")]
    [InlineData(true, "UPDATE mail_archiver.\"ArchivedEmails\" SET \"ContentStripped\" = false WHERE \"Id\" = {id}", "HTML body")]   // flag flipped: no longer waved through
    [InlineData(true, "UPDATE mail_archiver.\"ArchivedEmails\" SET \"ContentStripped\" = false, \"HtmlBody\" = '<p>forged</p>' WHERE \"Id\" = {id}", "HTML body")]
    [InlineData(true, "UPDATE mail_archiver.\"EmailAttachments\" SET \"Size\" = 5 WHERE \"ArchivedEmailId\" = {id}", "size")]
    public async Task F03_ChangedDatabaseCopy_IsInconsistent(bool lean, string sqlTemplate, string expectedFindingFragment)
    {
        var (ctx, account, id, _, wired) = await ArchiveAsync(lean);
        try
        {
            var before = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Valid, before.Status);   // the untouched email is Valid with every new check on
            Assert.Empty(before.Findings);

            await SqlAsync(ctx, sqlTemplate.Replace("{id}", id.ToString()));

            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Inconsistent, report.Status);
            Assert.Contains(report.Findings, f => f.Contains(expectedFindingFragment, StringComparison.OrdinalIgnoreCase));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F03_FullMode_ChangedAttachmentBytes_AreCaught()
    {
        var (ctx, account, id, _, wired) = await ArchiveAsync(lean: false);
        try
        {
            // The bytes a download would serve are changed; the recorded Hash column is not.
            await SqlAsync(ctx, $@"UPDATE mail_archiver.""AttachmentContents"" SET ""Content"" = decode('deadbeef', 'hex')
                WHERE ""Id"" IN (SELECT ""AttachmentContentId"" FROM mail_archiver.""EmailAttachments"" WHERE ""ArchivedEmailId"" = {id})");

            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Inconsistent, report.Status);
            Assert.Contains(report.Findings, f => f.Contains("do not match their recorded hash") || f.Contains("different size"));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F03_ANotLeanEmailWithoutAttachmentBytes_IsInconsistent()
    {
        var (ctx, account, id, _, wired) = await ArchiveAsync(lean: true);
        try
        {
            // Flag flipped and the place in the original erased: nothing proves the attachment any more.
            await SqlAsync(ctx, $@"UPDATE mail_archiver.""ArchivedEmails"" SET ""ContentStripped"" = false WHERE ""Id"" = {id};
                UPDATE mail_archiver.""EmailAttachments"" SET ""PartPath"" = NULL, ""Sha256"" = NULL WHERE ""ArchivedEmailId"" = {id}");

            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Inconsistent, report.Status);
            Assert.Contains(report.Findings, f => f.Contains("neither stored bytes nor a place"));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    // ---------------- F-04: the chain decides ----------------

    [Fact]
    public async Task F04_AnEmailStoredBeforeTheChain_IsUnchained_AndStillValid()
    {
        var (ctx, account, id, _, wired) = await ArchiveAsync(lean: true, withChain: false);
        try
        {
            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Valid, report.Status);
            Assert.Equal(IntegrityChainState.Unchained, report.ChainState);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F04_AChainedEmail_IsChained_AndValid()
    {
        var (ctx, account, id, _, wired) = await ArchiveAsync(lean: true, withChain: true);
        try
        {
            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Valid, report.Status);
            Assert.Equal(IntegrityChainState.Chained, report.ChainState);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F04_AForgedOriginalWithItsRowUpdatedToMatch_IsAChainMismatch()
    {
        var (ctx, account, id, bytes, wired) = await ArchiveAsync(lean: true, withChain: true);
        try
        {
            // An attacker who can write the originals and the database stores a forged copy under ITS OWN hash and
            // points the row at it. Everything else in the email still agrees, so the file/row checks alone say Valid.
            var forged = bytes.Concat(new byte[] { 13, 10, 13, 10 }).ToArray();
            var forgedSha = RawMessageCodec.ComputeSha256(forged);
            var rel = $"{forgedSha[..2]}/{forgedSha.Substring(2, 2)}/{forgedSha}.eml.gz";
            var abs = Path.Combine(_kit.Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            await File.WriteAllBytesAsync(abs, RawMessageCodec.Compress(forged));
            await SqlAsync(ctx, $@"UPDATE mail_archiver.""RawMessages"" SET ""Sha256"" = '{forgedSha}', ""Size"" = {forged.Length}, ""StoragePath"" = '{rel}' WHERE ""ArchivedEmailId"" = {id}");

            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.ChainMismatch, report.Status);
            Assert.Equal(IntegrityChainState.Mismatch, report.ChainState);
            Assert.Contains(report.Findings, f => f.Contains("audit chain recorded"));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F05_ARowDeletedWhileTheChainSaysStored_IsMissing_NotNotStored()
    {
        var (ctx, account, id, _, wired) = await ArchiveAsync(lean: false, withChain: true);
        try
        {
            await SqlAsync(ctx, $@"DELETE FROM mail_archiver.""RawMessages"" WHERE ""ArchivedEmailId"" = {id}");
            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Missing, report.Status);
            Assert.Contains(report.Findings, f => f.Contains("audit chain records an original stored"));
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    // ---------------- F-10 / F-27: damaged originals are Invalid, never an error; no server paths ----------------

    private async Task<string> OriginalPathAsync(MailArchiverDbContext ctx, int id)
        => Path.Combine(_kit.Root, await ctx.RawMessages.Where(r => r.ArchivedEmailId == id).Select(r => r.StoragePath!).SingleAsync());

    [Fact]
    public async Task F10_ANonGzipOriginal_IsInvalid_NotAnException()
    {
        var (ctx, account, id, _, wired) = await ArchiveAsync(lean: true);
        try
        {
            await File.WriteAllBytesAsync(await OriginalPathAsync(ctx, id), System.Text.Encoding.ASCII.GetBytes("this is not gzip at all"));
            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Invalid, report.Status);
            Assert.DoesNotContain(report.Findings, f => f.Contains(_kit.Root));   // F-27: no server paths
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F10_ATruncatedGzipOriginal_IsInvalid()
    {
        var (ctx, account, id, bytes, wired) = await ArchiveAsync(lean: true);
        try
        {
            var gz = RawMessageCodec.Compress(bytes);
            await File.WriteAllBytesAsync(await OriginalPathAsync(ctx, id), gz.Take(gz.Length / 2).ToArray());
            Assert.Equal(IntegrityStatus.Invalid, (await wired.Integrity.VerifyAsync(id)).Status);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task F10_ADecompressionBombOriginal_IsInvalid_AndNeverAllocatedPastTheRecordedSize()
    {
        var (ctx, account, id, bytes, wired) = await ArchiveAsync(lean: true);
        try
        {
            // 40 MB of zeros compress to a few KB; the row records the real (small) size.
            await File.WriteAllBytesAsync(await OriginalPathAsync(ctx, id), RawMessageCodec.Compress(new byte[40 * 1024 * 1024]));
            var report = await wired.Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Invalid, report.Status);
        }
        finally { await ReviewKit.CleanupAsync(ctx, account); }
    }

    [Fact]
    public void F10_DecompressRefusesOutputLargerThanTheRecordedSize()
    {
        var big = RawMessageCodec.Compress(new byte[1024 * 1024]);
        Assert.Throws<InvalidDataException>(() => RawMessageCodec.Decompress(big, expectedSize: 1000));
        Assert.Equal(1024 * 1024, RawMessageCodec.Decompress(big, expectedSize: 1024 * 1024).Length);
        Assert.Equal(1024 * 1024, RawMessageCodec.Decompress(big).Length);   // no size recorded: the absolute ceiling applies
    }

    // ---------------- F-13: two starts at once make one run ----------------

    [Fact]
    public async Task F13_ConcurrentStarts_ReturnTheSameRun_AndNoRunIsMarkedInterrupted()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _kit.NewContext());
        services.AddScoped<IRawMessageStore>(sp =>
        {
            var ctx = sp.GetRequiredService<MailArchiverDbContext>();
            return _kit.Make(ctx, lean: true).Store;
        });
        services.AddScoped<IIntegrityService>(sp => new IntegrityService(sp.GetRequiredService<MailArchiverDbContext>(), sp.GetRequiredService<IRawMessageStore>()));
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStopping).Returns(CancellationToken.None);
        var runner = new IntegrityRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), lifetime.Object, NullLogger<IntegrityRunner>.Instance);

        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => runner.StartAsync("test")));
        await runner.WaitForCurrentAsync();

        Assert.Single(runs.Select(r => r.Id).Distinct());
        await using var read = _kit.NewContext();
        var run = await read.IntegrityRuns.AsNoTracking().SingleAsync(r => r.Id == runs[0].Id);
        Assert.Equal(IntegrityRun.Completed, run.Status);
    }
}
