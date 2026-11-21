using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Shared;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Moq;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Synthetic messages only; the database context is never queried in these tests.</summary>
public class StrippedContentHydratorTests
{
    private static MailArchiverDbContext NewContext()
        => new(new DbContextOptionsBuilder<MailArchiverDbContext>().UseNpgsql("Host=unused;Database=unused").Options);

    private static (MimeMessage Message, byte[] Pdf, string PdfPath, string PdfSha) BuildOriginal()
    {
        var pdf = Enumerable.Range(0, 200).Select(i => (byte)(i * 7)).ToArray();
        var pdfPart = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(pdf)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "r.pdf" },
            ContentTransferEncoding = ContentEncoding.Base64
        };
        var alt = new MultipartAlternative
        {
            new TextPart("plain") { Text = "plain full text" },
            new TextPart("html") { Text = "<p>html full</p>" }
        };
        var mixed = new Multipart("mixed") { alt, pdfPart };
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("", "a@example.test"));
        msg.To.Add(new MailboxAddress("", "b@example.test"));
        msg.Subject = "s";
        msg.Body = mixed;

        using var ms = new MemoryStream();
        msg.WriteTo(ms);
        ms.Position = 0;
        var parsed = MimeMessage.Load(ms);
        var parsedPdf = ((Multipart)parsed.Body)[1];
        return (parsed, pdf, MimePartLocator.GetPath(parsed, parsedPdf)!, RawMessageCodec.ComputeSha256(pdf));
    }

    private static ArchivedEmail LeanEmail(string path, string sha) => new()
    {
        Id = 5, ContentStripped = true, Body = "truncated search text", HtmlBody = "", FolderName = "INBOX",
        Attachments = new List<EmailAttachment>
        {
            new() { Id = 9, ArchivedEmailId = 5, FileName = "r.pdf", ContentType = "application/pdf", PartPath = path, Sha256 = sha },
            new() { Id = 10, ArchivedEmailId = 5, FileName = "other.bin", ContentType = "application/octet-stream", PartPath = path, Sha256 = sha }
        }
    };

    private static StrippedContentHydrator Make(OriginalMessage? original, out Mock<IOriginalMessageReader> reader)
    {
        reader = new Mock<IOriginalMessageReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<int>())).ReturnsAsync(original);
        return new StrippedContentHydrator(NewContext(), reader.Object);
    }

    [Fact]
    public async Task NotStripped_IsANoOp_AndNeverReadsTheOriginal()
    {
        var hydrator = Make(null, out var reader);
        var email = new ArchivedEmail { Id = 1, ContentStripped = false, HtmlBody = "<b>kept</b>" };

        await hydrator.HydrateAsync(email);

        Assert.Equal("<b>kept</b>", email.HtmlBody);
        reader.Verify(r => r.ReadAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Stripped_RestoresBodiesAndAttachmentBytes_FromTheOriginal()
    {
        var (msg, pdf, path, sha) = BuildOriginal();
        var hydrator = Make(new OriginalMessage(msg, "x", "imap"), out _);
        var email = LeanEmail(path, sha);

        await hydrator.HydrateAsync(email);

        Assert.False(email.ContentStripped); // in memory only: the entity is detached
        Assert.Contains("html full", email.HtmlBody);
        Assert.Contains("plain full text", Encoding.UTF8.GetString(email.OriginalBodyText!));
        Assert.Equal("truncated search text", email.Body); // search text from the database is untouched
        Assert.All(email.Attachments, a => Assert.Equal(pdf, a.Content));
    }

    [Fact]
    public async Task Filter_LeavesOtherAttachmentsEmpty()
    {
        var (msg, pdf, path, sha) = BuildOriginal();
        var hydrator = Make(new OriginalMessage(msg, "x", "imap"), out _);
        var email = LeanEmail(path, sha);

        await hydrator.HydrateAsync(email, a => a.Id == 9);

        Assert.Equal(pdf, email.Attachments.Single(a => a.Id == 9).Content);
        Assert.Empty(email.Attachments.Single(a => a.Id == 10).Content);
    }

    [Fact]
    public async Task Stripped_WithoutAnOriginal_Throws_InsteadOfReturningAnEmptyEmail()
    {
        var hydrator = Make(null, out _);
        await Assert.ThrowsAsync<LeanOriginalMissingException>(() => hydrator.HydrateAsync(LeanEmail("b/c1", new string('a', 64))));
    }

    [Fact]
    public async Task HashMismatch_Throws()
    {
        var (msg, _, path, _) = BuildOriginal();
        var hydrator = Make(new OriginalMessage(msg, "x", "imap"), out _);
        await Assert.ThrowsAsync<LeanAttachmentIntegrityException>(() => hydrator.HydrateAsync(LeanEmail(path, new string('0', 64))));
    }

    [Fact]
    public async Task MissingPartOrMissingRecordedHash_Throws()
    {
        var (msg, _, _, sha) = BuildOriginal();
        var hydrator = Make(new OriginalMessage(msg, "x", "imap"), out _);
        await Assert.ThrowsAsync<LeanAttachmentIntegrityException>(() => hydrator.HydrateAsync(LeanEmail("b/c9", sha)));

        var noHash = LeanEmail("b/c1", sha);
        noHash.Attachments.First().Sha256 = null;
        await Assert.ThrowsAsync<LeanAttachmentIntegrityException>(() => hydrator.HydrateAsync(noHash));
    }

    [Fact]
    public async Task HydrateAttachment_FillsOne_AndVerifiesTheHash()
    {
        var (msg, pdf, path, sha) = BuildOriginal();
        var hydrator = Make(new OriginalMessage(msg, "x", "imap"), out _);
        var attachment = LeanEmail(path, sha).Attachments.First();
        attachment.ArchivedEmail = new ArchivedEmail { Id = 5, ContentStripped = true };

        await hydrator.HydrateAttachmentAsync(attachment);
        Assert.Equal(pdf, attachment.Content);
    }

    [Fact]
    public void MimeBuilder_RefusesAStrippedEmail()
    {
        var helper = new DateTimeHelper(Microsoft.Extensions.Options.Options.Create(new TimeZoneOptions()));
        var email = new ArchivedEmail { Id = 3, ContentStripped = true, Subject = "s", From = "a@example.test", To = "b@example.test" };
        Assert.Throws<InvalidOperationException>(() => ArchivedEmailMimeBuilder.Build(email, helper));
    }
}
