using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Core;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Integrity verification against real ingested (synthetic) mail, in lean and full storage.</summary>
[Collection(TestDbFixture.CollectionName)]
public class IntegrityServiceTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "integ-" + Guid.NewGuid().ToString("N"));

    public IntegrityServiceTests(TestDbFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString, npgsql => npgsql.CommandTimeout(120))
            .AddInterceptors(new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    private static (MimeMessage Message, byte[] Bytes, byte[] Pdf) BuildMail()
    {
        var pdf = Enumerable.Range(0, 3000).Select(i => (byte)(i * 11 + 3)).ToArray();
        var alt = new MultipartAlternative { new TextPart("plain") { Text = "integrity plain body" }, new TextPart("html") { Text = "<p>integrity html</p>" } };
        var mixed = new Multipart("mixed") { alt, new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(pdf)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "i.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64
        } };
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "from@example.test"));
        m.To.Add(new MailboxAddress("", "to@example.test"));
        m.Cc.Add(new MailboxAddress("", "cc@example.test"));
        m.Subject = "integrity " + Guid.NewGuid().ToString("N");
        m.MessageId = $"{Guid.NewGuid():N}@example.test";
        m.Body = mixed;
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        return (MimeMessage.Load(read), bytes, pdf);
    }

    private (EmailCoreService Svc, IntegrityService Integrity) Make(MailArchiverDbContext ctx, bool lean)
    {
        var raw = new RawMessageOptions
        {
            Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
            FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = lean }
        };
        var opts = Opt.Create(raw);
        var paths = new RawMessagePathResolver(raw.FileSystem, _root);
        var fs = new FileSystemRawMessageStore(ctx, opts, paths, NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
        var svc = new EmailCoreService(ctx, NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Opt.Create(new BatchOperationOptions()), Opt.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null, rawMessageOptions: opts, rawMessageStore: router,
            contentHydrator: new StrippedContentHydrator(ctx, new OriginalMessageReader(router)),
            storageMode: Opt.Create(new StorageModeOptions { Mode = lean ? "Lean" : "Standard" }),
            leanIngest: new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance));
        return (svc, new IntegrityService(ctx, router));
    }

    private async Task<(MailArchiverDbContext Ctx, MailAccount Account, int EmailId, byte[] Pdf)> ArchiveAsync(bool lean, bool withOriginal = true)
    {
        var ctx = NewContext();
        var account = new MailAccount
        {
            Name = $"i-{Guid.NewGuid():N}".Substring(0, 18), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(account);
        await ctx.SaveChangesAsync();

        var (msg, bytes, pdf) = BuildMail();
        var (svc, _) = Make(ctx, lean);
        Assert.True(await svc.ArchiveEmailAsync(account, msg, false, "INBOX",
            withOriginal ? new RawMessageCapture(bytes, RawMessageSource.Imap) : null));
        ctx.ChangeTracker.Clear();
        var id = await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).Select(e => e.Id).SingleAsync();
        return (ctx, account, id, pdf);
    }

    private static async Task CleanupAsync(MailArchiverDbContext ctx, MailAccount account, byte[] pdf)
    {
        ctx.ChangeTracker.Clear();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
        await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
        await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
        var sha = RawMessageCodec.ComputeSha256(pdf);
        await ctx.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM mail_archiver.\"AttachmentContents\" WHERE \"Hash\" = {sha}");
        await ctx.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UntouchedEmail_IsValid(bool lean)
    {
        var (ctx, account, id, pdf) = await ArchiveAsync(lean);
        try
        {
            var report = await Make(ctx, lean).Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Valid, report.Status);
            Assert.Empty(report.Findings);
            Assert.Equal(lean, report.Lean);
            Assert.Equal(RawMessageSource.Imap, report.Source);
        }
        finally { await CleanupAsync(ctx, account, pdf); }
    }

    [Theory]
    [InlineData(true, "Subject")]
    [InlineData(false, "Subject")]
    [InlineData(true, "Body")]
    [InlineData(false, "From")]
    [InlineData(false, "To")]
    public async Task ChangedDatabaseField_IsInconsistent(bool lean, string column)
    {
        var (ctx, account, id, pdf) = await ArchiveAsync(lean);
        try
        {
            var sql = $"UPDATE mail_archiver.\"ArchivedEmails\" SET \"{column}\" = 'tampered' WHERE \"Id\" = {id}";
            await ctx.Database.ExecuteSqlRawAsync(sql);

            var report = await Make(ctx, lean).Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Inconsistent, report.Status);
            Assert.Single(report.Findings);
        }
        finally { await CleanupAsync(ctx, account, pdf); }
    }

    [Fact]
    public async Task Lean_ChangedAttachmentHash_IsInconsistent()
    {
        var (ctx, account, id, pdf) = await ArchiveAsync(lean: true);
        try
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE mail_archiver.\"EmailAttachments\" SET \"Sha256\" = {new string('0', 64)} WHERE \"ArchivedEmailId\" = {id}");
            var report = await Make(ctx, true).Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Inconsistent, report.Status);
            Assert.Contains(report.Findings, f => f.Contains("recorded hash"));
        }
        finally { await CleanupAsync(ctx, account, pdf); }
    }

    [Fact]
    public async Task ModifiedOriginalFile_IsInvalid()
    {
        var (ctx, account, id, pdf) = await ArchiveAsync(lean: true);
        try
        {
            var path = await ctx.RawMessages.Where(r => r.ArchivedEmailId == id).Select(r => r.StoragePath).SingleAsync();
            var absolute = Path.Combine(_root, path!);
            await File.WriteAllBytesAsync(absolute, RawMessageCodec.Compress(System.Text.Encoding.ASCII.GetBytes("Subject: forged\r\n\r\nforged\r\n")));

            var report = await Make(ctx, true).Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Invalid, report.Status);
        }
        finally { await CleanupAsync(ctx, account, pdf); }
    }

    [Fact]
    public async Task DeletedOriginalFile_IsMissing()
    {
        var (ctx, account, id, pdf) = await ArchiveAsync(lean: true);
        try
        {
            var path = await ctx.RawMessages.Where(r => r.ArchivedEmailId == id).Select(r => r.StoragePath).SingleAsync();
            File.Delete(Path.Combine(_root, path!));

            var report = await Make(ctx, true).Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Missing, report.Status);
        }
        finally { await CleanupAsync(ctx, account, pdf); }
    }

    [Fact]
    public async Task EmailArchivedWithoutAnOriginal_IsNotStored()
    {
        var (ctx, account, id, pdf) = await ArchiveAsync(lean: false, withOriginal: false);
        try
        {
            var report = await Make(ctx, false).Integrity.VerifyAsync(id);
            Assert.Equal(IntegrityStatus.NotStored, report.Status);
        }
        finally { await CleanupAsync(ctx, account, pdf); }
    }
}
