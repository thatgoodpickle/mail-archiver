using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Core;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace MailArchiver.Tests.Services;

public class StorageModeOptionsTests
{
    private static RawMessageOptions Good() => new()
    {
        Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
        FileSystem = new RawMessageFileSystemOptions { HashNamed = true }
    };

    [Fact]
    public void Standard_NeedsNothing() => Assert.Empty(new StorageModeOptions().Validate(new RawMessageOptions()));

    [Fact]
    public void Lean_WithTheRightSettings_IsValid() => Assert.Empty(new StorageModeOptions { Mode = "lean" }.Validate(Good()));

    [Theory]
    [InlineData("disabled")]
    [InlineData("postgres")]
    [InlineData("notHashNamed")]
    [InlineData("sizeLimit")]
    public void Lean_RefusesEachMissingRequirement(string what)
    {
        var raw = Good();
        switch (what)
        {
            case "disabled": raw.Enabled = false; break;
            case "postgres": raw.Storage = RawMessageStorage.Postgres; break;
            case "notHashNamed": raw.FileSystem.HashNamed = false; break;
            case "sizeLimit": raw.MaxMessageSizeMb = 50; break;
        }
        Assert.Single(new StorageModeOptions { Mode = "Lean" }.Validate(raw));
    }

    [Fact]
    public void UnknownMode_IsRefused_NotTreatedAsStandard()
        => Assert.Single(new StorageModeOptions { Mode = "Leen" }.Validate(Good()));
}

public class RawMessageRequirementTests
{
    private static RawMessageCapture Cap(int n = 100) => new(new byte[n].Select((_, i) => (byte)(i + 1)).ToArray(), RawMessageSource.Imap);

    [Fact]
    public void NotRequired_NeverRefuses()
    {
        var o = new RawMessageOptions { Enabled = true };
        Assert.Null(RawMessageRequirement.RefusalReason(o, null));
    }

    [Fact]
    public void Required_RefusesMissingOrEmptyCapture()
    {
        var o = new RawMessageOptions { Enabled = true, Required = true };
        Assert.Equal("no_capture", RawMessageRequirement.RefusalReason(o, null));
        Assert.Equal("no_capture", RawMessageRequirement.RefusalReason(o, new RawMessageCapture(Array.Empty<byte>(), RawMessageSource.Imap)));
    }

    [Fact]
    public void Required_RefusesOverTheSizeLimit_AndAcceptsWithinIt()
    {
        var o = new RawMessageOptions { Enabled = true, Required = true, MaxMessageSizeMb = 1 };
        Assert.Equal("size", RawMessageRequirement.RefusalReason(o, Cap(1024 * 1024 + 1)));
        Assert.Null(RawMessageRequirement.RefusalReason(o, Cap(1024)));
    }

    [Fact]
    public void Required_WithoutEnabled_IsAStartupProblem()
    {
        Assert.Single(RawMessageRequirement.Validate(new RawMessageOptions { Required = true }));
        Assert.Empty(RawMessageRequirement.Validate(new RawMessageOptions { Required = true, Enabled = true }));
        Assert.Empty(RawMessageRequirement.Validate(new RawMessageOptions()));
    }
}

/// <summary>Lean ingest and the full archive -> read-back round trip. Synthetic mail only.</summary>
[Collection(TestDbFixture.CollectionName)]
public class LeanIngestTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lean-" + Guid.NewGuid().ToString("N"));

    public LeanIngestTests(TestDbFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Production registers the attachment de-duplication interceptor on the context; the shared
    // fixture context does not, so these tests build one that does (otherwise upstream's own
    // full-storage path would not persist attachment bytes in the test harness).
    private MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>()
            .UseNpgsql(_fixture.ConnectionString, npgsql => npgsql.CommandTimeout(120))
            .AddInterceptors(new AttachmentDeduplicationInterceptor(NullLogger<AttachmentDeduplicationInterceptor>.Instance))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    private static Task DeleteContentRowAsync(MailArchiverDbContext ctx, byte[] bytes)
    {
        var sha = RawMessageCodec.ComputeSha256(bytes);
        return ctx.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM mail_archiver.\"AttachmentContents\" WHERE \"Hash\" = {sha}");
    }

    private RawMessageOptions Options_() => new()
    {
        Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
        FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = true }
    };

    private static (MimeMessage Message, byte[] Bytes, byte[] Pdf) BuildMail(string html = "<p>html body</p>", bool withAttachment = true)
    {
        var pdf = Enumerable.Range(0, 5000).Select(i => (byte)(i * 13)).ToArray();
        var alt = new MultipartAlternative { new TextPart("plain") { Text = "plain body words" }, new TextPart("html") { Text = html } };
        var mixed = new Multipart("mixed") { alt };
        if (withAttachment)
        {
            mixed.Add(new MimePart("application", "pdf")
            {
                Content = new MimeContent(new MemoryStream(pdf)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "doc.pdf" },
                ContentTransferEncoding = ContentEncoding.Base64
            });
        }
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "a@example.test"));
        m.To.Add(new MailboxAddress("", "b@example.test"));
        m.Subject = "lean " + Guid.NewGuid().ToString("N");
        m.MessageId = $"{Guid.NewGuid():N}@example.test";
        m.Body = mixed;
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        return (MimeMessage.Load(read), bytes, pdf);
    }

    private (EmailCoreService Svc, StrippedContentHydrator Hydrator, LeanIngest Ingest) Make(MailArchiverDbContext ctx, RawMessageOptions raw)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(raw);
        var paths = new RawMessagePathResolver(raw.FileSystem, _root);
        var fs = new FileSystemRawMessageStore(ctx, opts, paths, NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
        var hydrator = new StrippedContentHydrator(ctx, new OriginalMessageReader(router));
        var ingest = new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance);
        var svc = new EmailCoreService(ctx,
            NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Microsoft.Extensions.Options.Options.Create(new BatchOperationOptions()),
            Microsoft.Extensions.Options.Options.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null,
            rawMessageOptions: opts,
            rawMessageStore: router,
            contentHydrator: hydrator,
            storageMode: Microsoft.Extensions.Options.Options.Create(new StorageModeOptions { Mode = "Lean" }),
            leanIngest: ingest);
        return (svc, hydrator, ingest);
    }

    private static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext ctx)
    {
        var a = new MailAccount
        {
            Name = $"lean-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(a);
        await ctx.SaveChangesAsync();
        return a;
    }

    private static async Task CleanupAsync(MailArchiverDbContext ctx, MailAccount account)
    {
        ctx.ChangeTracker.Clear();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
        await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
        await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Archive_Lean_KeepsOnlyIndexData_AndTheOriginalGivesEverythingBack()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, pdf) = BuildMail();
            var (svc, hydrator, _) = Make(ctx, Options_());

            Assert.True(await svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));

            ctx.ChangeTracker.Clear();
            var email = await ctx.ArchivedEmails.Include(e => e.Attachments).Include(e => e.RawMessage)
                .SingleAsync(e => e.MailAccountId == account.Id);

            Assert.True(email.ContentStripped);
            Assert.Equal(string.Empty, email.HtmlBody);
            Assert.Null(email.OriginalBodyHtml);
            Assert.Contains("plain body words", email.Body); // search text stays
            var att = Assert.Single(email.Attachments);
            Assert.Null(att.AttachmentContentId);           // no bytes, no shared content row
            Assert.Null(att.LegacyContent);
            Assert.Equal(0, await ctx.AttachmentContents.CountAsync(c => c.Hash == RawMessageCodec.ComputeSha256(pdf)));
            Assert.False(string.IsNullOrEmpty(att.PartPath));
            Assert.Equal(RawMessageCodec.ComputeSha256(pdf), att.Sha256);

            Assert.NotNull(email.RawMessage);
            Assert.Equal(RawMessageStorage.FileSystem, email.RawMessage!.StorageType);
            Assert.Equal(RawMessageCodec.ComputeSha256(bytes), email.RawMessage.Sha256);
            Assert.EndsWith($"{email.RawMessage.Sha256}.eml.gz", email.RawMessage.StoragePath);

            // the original brings everything back
            await hydrator.HydrateAsync(email);
            Assert.Contains("html body", email.HtmlBody);
            Assert.Equal(pdf, Assert.Single(email.Attachments).Content);
        }
        finally
        {
            await CleanupAsync(ctx, account);
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task Archive_Lean_WhenAnAttachmentCannotBeFoundInTheOriginal_StoresTheEmailInFull()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var (msg, _, pdf) = BuildMail();
            // a capture that is NOT the message (no attachment inside) simulates a mismatch
            var (_, otherBytes, _) = BuildMail(withAttachment: false);
            var (svc, _, _) = Make(ctx, Options_());

            Assert.True(await svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(otherBytes, RawMessageSource.Imap)));

            ctx.ChangeTracker.Clear();
            var email = await ctx.ArchivedEmails.Include(e => e.Attachments).ThenInclude(a => a.AttachmentContent)
                .SingleAsync(e => e.MailAccountId == account.Id);

            Assert.False(email.ContentStripped);
            Assert.Contains("html body", email.HtmlBody);
            var att = Assert.Single(email.Attachments);
            Assert.Equal(pdf, att.Content); // full bytes kept, nothing lost
            Assert.Null(att.PartPath);
        }
        finally
        {
            await CleanupAsync(ctx, account);
            await DeleteContentRowAsync(ctx, BuildMail().Pdf);
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task Archive_Lean_WithoutACapture_StoresTheEmailInFull()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var (msg, _, _) = BuildMail();
            var (svc, _, _) = Make(ctx, Options_());

            Assert.True(await svc.ArchiveEmailAsync(account, msg, false, "INBOX", rawMessage: null));

            ctx.ChangeTracker.Clear();
            var email = await ctx.ArchivedEmails.SingleAsync(e => e.MailAccountId == account.Id);
            Assert.False(email.ContentStripped);
            Assert.Contains("html body", email.HtmlBody);
        }
        finally
        {
            await CleanupAsync(ctx, account);
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryPrepare_ReturnsNull_WhenTheOriginalCannotBeWritten()
    {
        var ctx = NewContext();
        try
        {
            // the base path is a FILE, so no directory can be created under it
            Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
            await File.WriteAllTextAsync(_root, "not a directory");
            var (_, bytes, _) = BuildMail();
            var (_, _, ingest) = Make(ctx, Options_());

            Assert.Null(await ingest.TryPrepareAsync(new RawMessageCapture(bytes, RawMessageSource.Imap), new List<byte[]>()));
        }
        finally
        {
            try { File.Delete(_root); } catch { }
            await ctx.DisposeAsync();
        }
    }

    [Fact]
    public async Task Required_NoCapture_RefusesTheEmail_AndSavesNothing()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var (msg, _, _) = BuildMail();
            var raw = Options_(); raw.Required = true;
            var (svc, _, _) = Make(ctx, raw);

            Assert.False(await svc.ArchiveEmailAsync(account, msg, false, "INBOX", rawMessage: null));

            ctx.ChangeTracker.Clear();
            Assert.Equal(0, await ctx.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task Required_LeanVerificationFails_RefusesInsteadOfStoringInFull()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = BuildMail();
            var raw = Options_(); raw.Required = true;
            // Break the original on disk's destination so the verified write cannot succeed.
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, RawMessageCodec.ComputeSha256(bytes).Substring(0, 2)), "not a directory");
            var (svc, _, _) = Make(ctx, raw);

            Assert.False(await svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));

            ctx.ChangeTracker.Clear();
            Assert.Equal(0, await ctx.ArchivedEmails.CountAsync(e => e.MailAccountId == account.Id));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task Required_WithAnOriginal_ArchivesAsBefore()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var (msg, bytes, _) = BuildMail();
            var raw = Options_(); raw.Required = true;
            var (svc, _, _) = Make(ctx, raw);

            Assert.True(await svc.ArchiveEmailAsync(account, msg, false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));

            ctx.ChangeTracker.Clear();
            var email = await ctx.ArchivedEmails.SingleAsync(e => e.MailAccountId == account.Id);
            Assert.True(email.ContentStripped);
        }
        finally { await CleanupAsync(ctx, account); }
    }
}
