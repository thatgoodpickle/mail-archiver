using System.IO.Compression;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.Core;
using MailArchiver.Services.Shared;
using MailArchiver.Tests.Infrastructure;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xunit;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Synthetic attachments of every supported kind, built here (no real mail).</summary>
internal static class SyntheticAttachments
{
    public const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string PptxType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public static byte[] Pdf(string text)
    {
        var content = text.Length == 0 ? "" : $"BT /F1 18 Tf 20 100 Td ({text}) Tj ET";
        var objs = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>"
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {objs.Length + 1}/Root 1 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    public static byte[] Docx(string text)
        => Zip(("word/document.xml",
            $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>"));

    public static byte[] Xlsx(string text)
        => Zip(("xl/sharedStrings.xml", $"<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>{text}</t></si></sst>"),
               ("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row><c t=\"s\"><v>0</v></c><c><v>424242</v></c></row></sheetData></worksheet>"));

    public static byte[] Pptx(string text)
        => Zip(("ppt/slides/slide1.xml", $"<?xml version=\"1.0\"?><p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>"));

    public static byte[] Odt(string text)
        => Zip(("content.xml", $"<?xml version=\"1.0\"?><office:document-content xmlns:office=\"o\" xmlns:text=\"t\"><text:p>{text}</text:p></office:document-content>"));

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}

public class AttachmentTextExtractorTests
{
    private static AttachmentTextResult Run(byte[] bytes, string name, string type, int max = 100_000)
        => AttachmentTextExtractor.Extract(bytes, name, type, max, CancellationToken.None);

    [Fact]
    public void Pdf_ReturnsTheTextLayer()
    {
        var r = Run(SyntheticAttachments.Pdf("Quarterly zucchini report for the council"), "a.pdf", "application/pdf");
        Assert.Equal(AttachmentText.Done, r.Status);
        Assert.Contains("zucchini", r.Text);
    }

    [Fact]
    public void Pdf_WithoutText_NeedsOcr()
    {
        var r = Run(SyntheticAttachments.Pdf(""), "scan.pdf", "application/pdf");
        Assert.Equal(AttachmentText.NeedsOcr, r.Status);
    }

    [Fact]
    public void CorruptPdf_IsFailed_NotThrown()
    {
        var r = Run(Encoding.ASCII.GetBytes("%PDF-1.4 this is not a pdf"), "bad.pdf", "application/pdf");
        Assert.True(r.Status is AttachmentText.Failed or AttachmentText.NeedsOcr);
    }

    [Theory]
    [InlineData("docx", SyntheticAttachments.DocxType)]
    [InlineData("xlsx", SyntheticAttachments.XlsxType)]
    [InlineData("pptx", SyntheticAttachments.PptxType)]
    [InlineData("odt", "application/vnd.oasis.opendocument.text")]
    public void OfficeFiles_ReturnTheirText(string kind, string type)
    {
        var bytes = kind switch
        {
            "docx" => SyntheticAttachments.Docx("budget zucchini line"),
            "xlsx" => SyntheticAttachments.Xlsx("budget zucchini line"),
            "pptx" => SyntheticAttachments.Pptx("budget zucchini line"),
            _ => SyntheticAttachments.Odt("budget zucchini line")
        };
        var r = Run(bytes, "f." + kind, type);
        Assert.Equal(AttachmentText.Done, r.Status);
        Assert.Contains("budget zucchini line", r.Text);
        Assert.DoesNotContain("424242", r.Text); // cell values are not text
    }

    [Fact]
    public void Html_DropsTagsScriptsAndStyles_AndDecodesEntities()
    {
        var r = Run(SyntheticAttachments.Utf8("<html><style>p{color:red}</style><script>alert('x')</script><p>caf&eacute; <b>menu</b></p></html>"), "m.html", "text/html");
        Assert.Contains("café", r.Text);
        Assert.Contains("menu", r.Text);
        Assert.DoesNotContain("alert", r.Text);
        Assert.DoesNotContain("color", r.Text);
    }

    [Fact]
    public void AttachedMessage_GivesSubjectAndBody()
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "a@example.test"));
        m.To.Add(new MailboxAddress("", "b@example.test"));
        m.Subject = "forwarded rhubarb";
        m.Body = new TextPart("plain") { Text = "inside body kohlrabi" };
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        var r = Run(ms.ToArray(), "fw.eml", "message/rfc822");
        Assert.Contains("rhubarb", r.Text);
        Assert.Contains("kohlrabi", r.Text);
    }

    [Fact]
    public void Text_DropsNulAndControlCharacters_AndIsCapped()
    {
        var r = Run(SyntheticAttachments.Utf8("hello\0 world\u0007 " + new string('x', 5000)), "n.txt", "text/plain", max: 1000);
        Assert.DoesNotContain('\0', r.Text);
        Assert.DoesNotContain('\u0007', r.Text);
        Assert.True(r.Text.Length <= 1000);
        Assert.StartsWith("hello world", r.Text);
    }

    [Theory]
    [InlineData("scan.png", "image/png", AttachmentText.NeedsOcr)]
    [InlineData("data.bin", "application/octet-stream", AttachmentText.Unsupported)]
    public void ImagesWaitForOcr_UnknownTypesAreUnsupported(string name, string type, string expected)
        => Assert.Equal(expected, Run(new byte[] { 1, 2, 3 }, name, type).Status);

    [Fact]
    public void ZipBomb_IsBoundedByTheLimits_AndTheTextCap()
    {
        // ~40 MB of text inside the entry: over the per-entry limit and the text cap; must stop early, not run out of memory.
        var big = new string('a', 1000) + " ";
        var sb = new StringBuilder("<?xml version=\"1.0\"?><w:document xmlns:w=\"w\"><w:p><w:t>");
        for (var i = 0; i < 40_000; i++) sb.Append(big);
        sb.Append("</w:t></w:p></w:document>");
        var bytes = SyntheticAttachments.Zip(("word/document.xml", sb.ToString()));

        var r = Run(bytes, "bomb.docx", SyntheticAttachments.DocxType, max: 50_000);
        Assert.True(r.Text.Length <= 50_000);
    }

    [Fact]
    public void Cancellation_ReportsTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = AttachmentTextExtractor.Extract(SyntheticAttachments.Pdf("text"), "a.pdf", "application/pdf", 1000, cts.Token);
        Assert.Equal(AttachmentText.Failed, r.Status);
        Assert.Equal("timeout", r.Error);
    }
}

/// <summary>The worker end to end on real ingested synthetic mail: lean and full-storage emails, search, integrity, locking.</summary>
[Collection(TestDbFixture.CollectionName)]
public class AttachmentTextProcessorTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atext-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _shas = new();

    public AttachmentTextProcessorTests(TestDbFixture fixture) => _fixture = fixture;

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

    private RawMessageOptions Raw() => new()
    {
        Enabled = true, Storage = RawMessageStorage.FileSystem, MaxMessageSizeMb = 0,
        FileSystem = new RawMessageFileSystemOptions { BasePath = _root, HashNamed = true }
    };

    private (EmailCoreService Svc, StrippedContentHydrator Hydrator, IRawMessageStore Router) Make(MailArchiverDbContext ctx)
    {
        var raw = Raw();
        var opts = Opt.Create(raw);
        var fs = new FileSystemRawMessageStore(ctx, opts, new RawMessagePathResolver(raw.FileSystem, _root), NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
        var hydrator = new StrippedContentHydrator(ctx, new OriginalMessageReader(router));
        var svc = new EmailCoreService(ctx, NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Opt.Create(new BatchOperationOptions()), Opt.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null, rawMessageOptions: opts, rawMessageStore: router, contentHydrator: hydrator,
            storageMode: Opt.Create(new StorageModeOptions { Mode = "Lean" }),
            leanIngest: new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance));
        return (svc, hydrator, router);
    }

    private AttachmentTextProcessor NewProcessor(MailArchiverDbContext ctx, StrippedContentHydrator hydrator, AttachmentTextOptions? options = null)
        => new(ctx, hydrator, Opt.Create(options ?? new AttachmentTextOptions { Mode = "cpu" }), NullLogger<AttachmentTextProcessor>.Instance);

    private static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext ctx)
    {
        var a = new MailAccount
        {
            Name = $"at-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
            Provider = ProviderType.IMAP, IsEnabled = true, LastSync = DateTime.UtcNow
        };
        ctx.MailAccounts.Add(a);
        await ctx.SaveChangesAsync();
        return a;
    }

    private async Task CleanupAsync(MailArchiverDbContext ctx, MailAccount account)
    {
        ctx.ChangeTracker.Clear();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = false WHERE \"MailAccountId\" = {account.Id}");
        await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id).ExecuteDeleteAsync();
        await ctx.MailAccounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();
        foreach (var sha in _shas)
            await ctx.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM mail_archiver.\"AttachmentTexts\" WHERE \"Sha256\" = {sha}");
    }

    private static string Word() => "zucchini" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A lean email with one attachment, through the real ingest path.</summary>
    private async Task<int> ArchiveLeanAsync(EmailCoreService svc, MailAccount account, byte[] attachment, string fileName, string type)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "a@example.test"));
        m.To.Add(new MailboxAddress("", "b@example.test"));
        m.Subject = "mail " + Guid.NewGuid().ToString("N");
        m.MessageId = $"{Guid.NewGuid():N}@example.test";
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "plain body" } };
        mixed.Add(new MimePart(ContentType.Parse(type))
        {
            Content = new MimeContent(new MemoryStream(attachment)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
            ContentTransferEncoding = ContentEncoding.Base64
        });
        m.Body = mixed;
        using var ms = new MemoryStream();
        m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        Assert.True(await svc.ArchiveEmailAsync(account, MimeMessage.Load(read), false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));

        _shas.Add(RawMessageCodec.ComputeSha256(attachment));
        var ctx = NewContext();
        return await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id && e.Subject == m.Subject).Select(e => e.Id).SingleAsync();
    }

    /// <summary>A full-storage email (bytes in AttachmentContents), the upstream layout.</summary>
    private async Task<int> InsertFullAsync(MailArchiverDbContext ctx, MailAccount account, byte[] attachment, string fileName, string type)
    {
        var email = new ArchivedEmail
        {
            MailAccountId = account.Id, MessageId = Guid.NewGuid().ToString("N"), Subject = "full " + Guid.NewGuid().ToString("N"),
            From = "a@b.test", To = "c@d.test", Cc = "", Bcc = "", Body = "plain body", HtmlBody = "", FolderName = "INBOX",
            SentDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow, HasAttachments = true
        };
        email.Attachments.Add(new EmailAttachment { FileName = fileName, ContentType = type, Size = attachment.Length, Content = attachment });
        ctx.ArchivedEmails.Add(email);
        await ctx.SaveChangesAsync();
        _shas.Add(RawMessageCodec.ComputeSha256(attachment));
        return email.Id;
    }

    private static async Task DrainAsync(AttachmentTextProcessor p)
    {
        for (var i = 0; i < 100; i++)
            if (await p.ProcessBatchAsync() == 0) return;
        throw new Exception("the queue did not drain");
    }

    private static async Task<int[]> SearchAsync(EmailCoreService svc, MailAccount account, string term)
    {
        var (emails, _) = await svc.SearchEmailsAsync(term, null, null, null, null, null, 0, 50, new List<int> { account.Id });
        return emails.Select(e => e.Id).ToArray();
    }

    [Fact]
    public async Task LeanEmail_AttachmentWordIsSearchable_AndIntegrityStaysValid()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var word = Word();
            var (svc, hydrator, router) = Make(ctx);
            var id = await ArchiveLeanAsync(svc, account, SyntheticAttachments.Docx($"contract about {word} supplies"), "c.docx", SyntheticAttachments.DocxType);

            Assert.Empty(await SearchAsync(svc, account, word));   // not indexed yet

            var proc = NewProcessor(ctx, hydrator);
            await DrainAsync(proc);

            Assert.Equal(new[] { id }, await SearchAsync(svc, account, word));
            Assert.Equal(new[] { id }, await SearchAsync(svc, account, $"\"about {word} supplies\"")); // phrase path
            Assert.Empty(await SearchAsync(svc, account, "nosuchwordanywhere" + Guid.NewGuid().ToString("N")[..6]));

            await using var read = NewContext();
            var email = await read.ArchivedEmails.AsNoTracking().SingleAsync(e => e.Id == id);
            Assert.True(email.ContentStripped);
            Assert.NotNull(email.AttachmentTextAt);
            Assert.Contains(word, email.AttachmentText);

            var report = await new IntegrityService(read, router).VerifyAsync(id);
            Assert.Equal(IntegrityStatus.Valid, report.Status);
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task FullStorageEmail_IsReadFromTheContentTable()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var word = Word();
            var (svc, hydrator, _) = Make(ctx);
            var id = await InsertFullAsync(ctx, account, SyntheticAttachments.Pdf($"invoice for {word} crates"), "i.pdf", "application/pdf");

            await DrainAsync(NewProcessor(ctx, hydrator));

            Assert.Equal(new[] { id }, await SearchAsync(svc, account, word));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task SameAttachmentInTwoEmails_IsReadOnce_AndIndexedForBoth()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var word = Word();
            var (svc, hydrator, _) = Make(ctx);
            var bytes = SyntheticAttachments.Docx($"shared {word}");
            var a = await InsertFullAsync(ctx, account, bytes, "s.docx", SyntheticAttachments.DocxType);
            var b = await InsertFullAsync(ctx, account, bytes, "s-again.docx", SyntheticAttachments.DocxType);

            await DrainAsync(NewProcessor(ctx, hydrator));

            Assert.Equal(new[] { a, b }.OrderBy(x => x), (await SearchAsync(svc, account, word)).OrderBy(x => x));
            await using var read = NewContext();
            var sha = RawMessageCodec.ComputeSha256(bytes);
            Assert.Equal(1, await read.AttachmentTexts.CountAsync(t => t.Sha256 == sha));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task LockedEmail_StillGetsItsSearchText()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var word = Word();
            var (svc, hydrator, _) = Make(ctx);
            var id = await InsertFullAsync(ctx, account, SyntheticAttachments.Docx($"locked {word}"), "l.docx", SyntheticAttachments.DocxType);
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE mail_archiver.\"ArchivedEmails\" SET \"IsLocked\" = true WHERE \"Id\" = {id}");

            await DrainAsync(NewProcessor(ctx, hydrator));

            Assert.Equal(new[] { id }, await SearchAsync(svc, account, word));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task ScannedPdf_IsMarkedNeedsOcr_AndAddsNoSearchText()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var (_, hydrator, _) = Make(ctx);
            var bytes = SyntheticAttachments.Pdf("");
            var id = await InsertFullAsync(ctx, account, bytes, "scan.pdf", "application/pdf");

            await DrainAsync(NewProcessor(ctx, hydrator));

            await using var read = NewContext();
            var sha = RawMessageCodec.ComputeSha256(bytes);
            Assert.Equal(AttachmentText.NeedsOcr, (await read.AttachmentTexts.SingleAsync(t => t.Sha256 == sha)).Status);
            var email = await read.ArchivedEmails.AsNoTracking().SingleAsync(e => e.Id == id);
            Assert.Null(email.AttachmentText);
            Assert.NotNull(email.AttachmentTextAt);   // done: not looked at again
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task FailedResults_AreRetriedAfterARescan()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var word = Word();
            var (svc, hydrator, _) = Make(ctx);
            var bytes = SyntheticAttachments.Docx($"retry {word}");
            var id = await InsertFullAsync(ctx, account, bytes, "r.docx", SyntheticAttachments.DocxType);
            var sha = RawMessageCodec.ComputeSha256(bytes);

            // Limit of 0 MB: every attachment is "too large" and fails.
            await DrainAsync(NewProcessor(ctx, hydrator, new AttachmentTextOptions { Mode = "cpu", MaxAttachmentMb = 0 }));
            await using (var read = NewContext())
                Assert.Equal(AttachmentText.Failed, (await read.AttachmentTexts.SingleAsync(t => t.Sha256 == sha)).Status);
            Assert.Empty(await SearchAsync(svc, account, word));

            var proc = NewProcessor(ctx, hydrator);
            Assert.True(await proc.RescanAsync(all: false) >= 1);
            await DrainAsync(proc);

            Assert.Equal(new[] { id }, await SearchAsync(svc, account, word));
            var status = await proc.GetStatusAsync();
            Assert.Contains(status.Texts, t => t.Status == AttachmentText.Done);
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task ModeOff_WorkerDoesNothing()
    {
        var ctx = NewContext();
        var account = await SeedAccountAsync(ctx);
        try
        {
            var word = Word();
            var (svc, _, _) = Make(ctx);
            var bytes = SyntheticAttachments.Docx($"off {word}");
            await InsertFullAsync(ctx, account, bytes, "o.docx", SyntheticAttachments.DocxType);

            var services = new ServiceCollection();
            services.AddScoped(_ => NewContext());
            var worker = new AttachmentTextWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                Opt.Create(new AttachmentTextOptions()), NullLogger<AttachmentTextWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            await worker.ExecuteTask!;   // returns at once when off

            Assert.Empty(await SearchAsync(svc, account, word));
            await using var read = NewContext();
            var sha = RawMessageCodec.ComputeSha256(bytes);
            Assert.Equal(0, await read.AttachmentTexts.CountAsync(t => t.Sha256 == sha));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public void Defaults_AreOff()
    {
        Assert.True(new AttachmentTextOptions().IsOff);
        Assert.True(new AttachmentTextOptions { Mode = "OFF" }.IsOff);
        Assert.False(new AttachmentTextOptions { Mode = "cpu" }.IsOff);
    }
}
