using System.Text;
using MailArchiver.Services.Shared;
using MimeKit;
using Moq;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>
/// Tests for <see cref="MimePartLocator"/> and <see cref="OriginalMessageReader"/>.
/// All messages are synthetic; no real mail is used anywhere in the test suite.
/// </summary>
public class OriginalMessageReaderTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static MimeMessage Reparse(MimeMessage message)
    {
        // Round-trip through bytes so the tests work on a parsed message, like production does.
        using var ms = new MemoryStream();
        message.WriteTo(ms);
        ms.Position = 0;
        return MimeMessage.Load(ms);
    }

    private static IEnumerable<MimePart> AllParts(MimeEntity entity)
    {
        if (entity is MimePart p) yield return p;
        else if (entity is Multipart m)
            foreach (var child in m)
                foreach (var x in AllParts(child)) yield return x;
        else if (entity is MessagePart mp && mp.Message?.Body != null)
            foreach (var x in AllParts(mp.Message.Body)) yield return x;
    }

    private static MimeMessage BuildComplexMessage(byte[] pdf, byte[] png, byte[] dupA, byte[] dupB, byte[] unnamed)
    {
        var related = new MultipartRelated();
        related.Add(new TextPart("html") { Text = "<p>hi</p><img src=\"cid:logo1\">" });
        var inline = new MimePart("image", "png")
        {
            Content = new MimeContent(new MemoryStream(png)),
            ContentId = "logo1",
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
            ContentTransferEncoding = ContentEncoding.Base64
        };
        related.Add(inline);

        var alternative = new MultipartAlternative
        {
            new TextPart("plain") { Text = "hi" },
            related
        };

        // An attached message/rfc822 with its own attachment, to test nested paths.
        var inner = new MimeMessage();
        inner.From.Add(new MailboxAddress("", "inner@example.test"));
        inner.Subject = "inner";
        var innerMixed = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "inner body" },
            new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(unnamed)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64
            }
        };
        inner.Body = innerMixed;

        var mixed = new Multipart("mixed")
        {
            alternative,
            new MimePart("application", "pdf")
            {
                Content = new MimeContent(new MemoryStream(pdf)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "report.pdf" },
                ContentTransferEncoding = ContentEncoding.Base64
            },
            new MimePart("text", "plain")
            {
                Content = new MimeContent(new MemoryStream(dupA)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "same.txt" },
                ContentTransferEncoding = ContentEncoding.QuotedPrintable
            },
            new MimePart("text", "plain")
            {
                Content = new MimeContent(new MemoryStream(dupB)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "same.txt" },
                ContentTransferEncoding = ContentEncoding.Base64
            },
            new MessagePart("rfc822") { Message = inner }
        };

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("", "a@example.test"));
        message.To.Add(new MailboxAddress("", "b@example.test"));
        message.Subject = "synthetic";
        message.Body = mixed;
        return message;
    }

    [Fact]
    public void Locator_RoundTrips_EveryPart_InNestedMessage()
    {
        var message = Reparse(BuildComplexMessage(
            Bytes("PDFDATA"), new byte[] { 1, 2, 3, 4 }, Bytes("one"), Bytes("two"), new byte[] { 9, 9 }));

        var parts = AllParts(message.Body).ToList();
        Assert.True(parts.Count >= 6);

        foreach (var part in parts)
        {
            var path = MimePartLocator.GetPath(message, part);
            Assert.NotNull(path);
            Assert.Same(part, MimePartLocator.Resolve(message, path!));
        }
    }

    [Fact]
    public void Locator_Distinguishes_DuplicateFileNames()
    {
        var message = Reparse(BuildComplexMessage(
            Bytes("PDFDATA"), new byte[] { 1 }, Bytes("first copy"), Bytes("second copy"), new byte[] { 2 }));

        var same = AllParts(message.Body).Where(p => p.FileName == "same.txt").ToList();
        Assert.Equal(2, same.Count);

        var p0 = MimePartLocator.GetPath(message, same[0]);
        var p1 = MimePartLocator.GetPath(message, same[1]);
        Assert.NotEqual(p0, p1);
    }

    [Fact]
    public async Task ReadAttachment_DecodesBase64_AndQuotedPrintable_ToOriginalBytes()
    {
        var pdf = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var dupA = Bytes("line one =\r\nline two with = sign and é".Replace("é", "e"));
        var message = Reparse(BuildComplexMessage(pdf, new byte[] { 5 }, dupA, Bytes("b"), new byte[] { 7 }));

        var pdfPart = AllParts(message.Body).Single(p => p.FileName == "report.pdf");
        var pdfRead = await OriginalMessageReader.ReadAttachmentAsync(message, MimePartLocator.GetPath(message, pdfPart)!);
        Assert.NotNull(pdfRead);
        Assert.Equal(pdf, pdfRead!.Bytes);
        Assert.Equal(RawMessageCodec.ComputeSha256(pdf), pdfRead.Sha256);

        var qpPart = AllParts(message.Body).First(p => p.FileName == "same.txt");
        var qpRead = await OriginalMessageReader.ReadAttachmentAsync(message, MimePartLocator.GetPath(message, qpPart)!);
        Assert.Equal(dupA, qpRead!.Bytes);
    }

    [Fact]
    public async Task ReadAttachment_FindsInlineCidImage_AndUnnamedAttachmentInEmbeddedMessage()
    {
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 1 };
        var unnamed = new byte[] { 0, 1, 2, 3, 250, 251 };
        var message = Reparse(BuildComplexMessage(Bytes("x"), png, Bytes("a"), Bytes("b"), unnamed));

        var cidPart = AllParts(message.Body).Single(p => p.ContentId == "logo1");
        var cid = await OriginalMessageReader.ReadAttachmentAsync(message, MimePartLocator.GetPath(message, cidPart)!);
        Assert.Equal(png, cid!.Bytes);

        var nested = AllParts(message.Body).Single(p => p.ContentType.MimeType == "application/octet-stream");
        var nestedPath = MimePartLocator.GetPath(message, nested)!;
        Assert.Contains("/m/", nestedPath); // inside the attached message
        var read = await OriginalMessageReader.ReadAttachmentAsync(message, nestedPath);
        Assert.Equal(unnamed, read!.Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("b/c99")]
    [InlineData("b/m")]
    [InlineData("b/cX")]
    [InlineData("b/c0/c0/c0/c0/c0")]
    public void Resolve_ReturnsNull_ForPathsThatDoNotExist(string path)
    {
        var message = Reparse(BuildComplexMessage(Bytes("x"), new byte[] { 1 }, Bytes("a"), Bytes("b"), new byte[] { 2 }));
        Assert.Null(MimePartLocator.Resolve(message, path));
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenNoOriginalStored()
    {
        var store = new Mock<IRawMessageStore>();
        store.Setup(s => s.ReadAsync(42)).ReturnsAsync((RawMessageContent?)null);

        var reader = new OriginalMessageReader(store.Object);
        Assert.Null(await reader.ReadAsync(42));
    }

    [Fact]
    public async Task ReadAsync_ParsesStoredBytes_AndKeepsHashAndSource()
    {
        var raw = Bytes("From: a@example.test\r\nTo: b@example.test\r\nSubject: hello\r\n" +
                        "Content-Type: text/plain\r\n\r\nbody text\r\n");
        var hash = RawMessageCodec.ComputeSha256(raw);
        var store = new Mock<IRawMessageStore>();
        store.Setup(s => s.ReadAsync(7)).ReturnsAsync(new RawMessageContent(raw, hash, "imap", "filesystem"));

        var result = await new OriginalMessageReader(store.Object).ReadAsync(7);

        Assert.NotNull(result);
        Assert.Equal("hello", result!.Message.Subject);
        Assert.Equal(hash, result.Sha256);
        Assert.Equal("imap", result.Source);
    }

    [Fact]
    public async Task ReadAsync_Propagates_IntegrityFailure_FromTheStore()
    {
        var store = new Mock<IRawMessageStore>();
        store.Setup(s => s.ReadAsync(9)).ThrowsAsync(new RawMessageIntegrityException(9, "aaa", "bbb"));

        await Assert.ThrowsAsync<RawMessageIntegrityException>(() => new OriginalMessageReader(store.Object).ReadAsync(9));
    }
}
