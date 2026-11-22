using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using MailArchiver.Models;
using MimeKit;
using UglyToad.PdfPig;

namespace MailArchiver.Services.Shared
{
    public sealed record AttachmentTextResult(string Status, string Text, string Method, string Engine, string EngineVersion, string? Error = null);

    /// <summary>
    /// Reads the text layer out of an attachment: PDF (PdfPig), Word/Excel/PowerPoint and OpenDocument files
    /// (their XML, no macros or scripts executed), plain text, HTML and attached messages. Pages or files with
    /// no text layer (scans, photos) are reported as NeedsOcr for a later OCR engine. Everything read here comes
    /// from untrusted mail: it is only ever returned as text for the search index, bounded in size and time.
    /// </summary>
    public static class AttachmentTextExtractor
    {
        public const string EngineName = "textlayer";
        public const string EngineVersionValue = "1";

        private const long MaxEntryBytes = 20L * 1024 * 1024;   // per zip entry, decompressed
        private const int MaxZipEntries = 2000;
        private const int MinUsefulChars = 20;                  // fewer than this in a PDF = a scan

        public static AttachmentTextResult Extract(byte[] bytes, string? fileName, string? contentType, int maxChars, CancellationToken ct)
        {
            try
            {
                var kind = Classify(fileName, contentType);
                string text;
                switch (kind)
                {
                    case Kind.Pdf:
                        text = ReadPdf(bytes, maxChars, ct);
                        return text.Length < MinUsefulChars
                            ? Make(AttachmentText.NeedsOcr, text)
                            : Make(AttachmentText.Done, text);
                    case Kind.Docx: text = ReadOffice(bytes, maxChars, ct, e => e.StartsWith("word/") && e.EndsWith(".xml") && IsWordPart(e), "t"); break;
                    case Kind.Xlsx: text = ReadOffice(bytes, maxChars, ct, e => e == "xl/sharedStrings.xml" || (e.StartsWith("xl/worksheets/") && e.EndsWith(".xml")), "t"); break;
                    case Kind.Pptx: text = ReadOffice(bytes, maxChars, ct, e => e.StartsWith("ppt/slides/slide") && e.EndsWith(".xml"), "t"); break;
                    case Kind.OpenDocument: text = ReadOffice(bytes, maxChars, ct, e => e == "content.xml", null); break;
                    case Kind.Html: text = StripHtml(Decode(bytes, maxChars)); break;
                    case Kind.Message: text = ReadMessage(bytes, maxChars); break;
                    case Kind.Text: text = Decode(bytes, maxChars); break;
                    case Kind.Image:
                        return new AttachmentTextResult(AttachmentText.NeedsOcr, string.Empty, AttachmentText.TextLayer, EngineName, EngineVersionValue);
                    default:
                        return new AttachmentTextResult(AttachmentText.Unsupported, string.Empty, AttachmentText.TextLayer, EngineName, EngineVersionValue);
                }
                return Make(AttachmentText.Done, text);
            }
            catch (OperationCanceledException)
            {
                return Failed("timeout");
            }
            catch (Exception ex)
            {
                // Corrupt or hostile files end up here; the message is kept short and never contains file content.
                return Failed(ex.GetType().Name + ": " + Truncate(ex.Message, 200));
            }

            AttachmentTextResult Make(string status, string t)
                => new(status, Clean(t, maxChars), AttachmentText.TextLayer, EngineName, EngineVersionValue);
        }

        private static AttachmentTextResult Failed(string error)
            => new(AttachmentText.Failed, string.Empty, AttachmentText.TextLayer, EngineName, EngineVersionValue, error);

        public enum OcrSource { None, Image, Pdf }

        /// <summary>What an item needing OCR is: a picture, a PDF whose pages are pictures, or nothing the OCR engine reads.</summary>
        public static OcrSource OcrSourceOf(string? fileName, string? contentType)
        {
            var kind = Classify(fileName, contentType);
            return kind == Kind.Image ? OcrSource.Image : kind == Kind.Pdf ? OcrSource.Pdf : OcrSource.None;
        }

        private enum Kind { Unknown, Pdf, Docx, Xlsx, Pptx, OpenDocument, Html, Message, Text, Image }

        private static Kind Classify(string? fileName, string? contentType)
        {
            var ct = (contentType ?? string.Empty).Trim().ToLowerInvariant();
            var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

            if (ct == "application/pdf" || ext == ".pdf") return Kind.Pdf;
            if (ct.Contains("wordprocessingml") || ext is ".docx" or ".docm" or ".dotx") return Kind.Docx;
            if (ct.Contains("spreadsheetml") || ext is ".xlsx" or ".xlsm" or ".xltx") return Kind.Xlsx;
            if (ct.Contains("presentationml") || ext is ".pptx" or ".pptm" or ".potx") return Kind.Pptx;
            if (ct.StartsWith("application/vnd.oasis.opendocument") || ext is ".odt" or ".ods" or ".odp") return Kind.OpenDocument;
            if (ct == "text/html" || ct == "application/xhtml+xml" || ext is ".html" or ".htm") return Kind.Html;
            if (ct == "message/rfc822" || ext == ".eml") return Kind.Message;
            if (ct.StartsWith("text/") || ct is "application/json" or "application/xml" or "application/csv") return Kind.Text;
            if (ext is ".txt" or ".csv" or ".md" or ".json" or ".xml" or ".log" or ".ics" or ".tsv") return Kind.Text;
            if (ct.StartsWith("image/") || ext is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp" or ".gif" or ".webp") return Kind.Image;
            return Kind.Unknown;
        }

        // Body, headers, footers, footnotes and comments of a Word file; not styles, themes or settings.
        private static bool IsWordPart(string entry)
            => entry == "word/document.xml" || entry.StartsWith("word/header") || entry.StartsWith("word/footer")
               || entry == "word/footnotes.xml" || entry == "word/endnotes.xml" || entry == "word/comments.xml";

        private static string ReadPdf(byte[] bytes, int maxChars, CancellationToken ct)
        {
            var sb = new StringBuilder();
            using var doc = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                sb.Append(page.Text).Append('\n');
                if (sb.Length >= maxChars) break;
            }
            return sb.ToString();
        }

        /// <summary>Concatenates the text nodes (<paramref name="textElement"/> = local name, or null for all text) of matching zip entries.</summary>
        private static string ReadOffice(byte[] bytes, int maxChars, CancellationToken ct, Func<string, bool> entryFilter, string? textElement)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries) throw new InvalidDataException("too many entries");

            var sb = new StringBuilder();
            foreach (var entry in zip.Entries.Where(e => entryFilter(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                using var raw = entry.Open();
                using var limited = new LimitedStream(raw, MaxEntryBytes);   // a zip bomb stops here, not in memory
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
                using var xml = XmlReader.Create(limited, settings);
                string? current = null;
                var steps = 0;
                try
                {
                    while (xml.Read())
                    {
                        switch (xml.NodeType)
                        {
                            case XmlNodeType.Element:
                                current = xml.LocalName;
                                break;
                            case XmlNodeType.Text:
                            case XmlNodeType.CDATA:
                            case XmlNodeType.SignificantWhitespace:
                                // Office files: only the contents of <t> are text (the rest are values and formulas).
                                if (textElement == null || current == textElement) sb.Append(xml.Value).Append(' ');
                                break;
                            case XmlNodeType.EndElement:
                                current = null;
                                if (xml.LocalName is "p" or "row" or "h") sb.Append('\n');
                                break;
                        }
                        if (sb.Length >= maxChars) return sb.ToString();
                        if ((++steps & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                    }
                }
                catch (Exception ex) when (ex is LimitExceededException || ex.InnerException is LimitExceededException)
                {
                    // Over the size limit: keep what was read so far and move on.
                }
            }
            return sb.ToString();
        }

        private static string Decode(byte[] bytes, int maxChars)
        {
            var n = (int)Math.Min(bytes.Length, (long)maxChars * 4);
            // BOM-aware; invalid bytes become U+FFFD, which Clean() drops.
            using var reader = new StreamReader(new MemoryStream(bytes, 0, n), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxChars];
            var read = reader.ReadBlock(buffer, 0, maxChars);
            return new string(buffer, 0, read);
        }

        private static readonly Regex ScriptOrStyle = new(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

        private static string StripHtml(string html)
            => WebUtility.HtmlDecode(Tags.Replace(ScriptOrStyle.Replace(html, " "), " "));

        private static string ReadMessage(byte[] bytes, int maxChars)
        {
            var message = MimeMessage.Load(new MemoryStream(bytes));
            var sb = new StringBuilder();
            sb.Append(message.Subject).Append('\n');
            sb.Append(message.TextBody ?? (message.HtmlBody != null ? StripHtml(message.HtmlBody) : string.Empty));
            return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
        }

        /// <summary>
        /// Makes the text safe for the database and the search vector: no NUL or control characters (PostgreSQL
        /// text cannot hold NUL), no unpaired surrogates, runs of whitespace collapsed, at most <paramref name="maxChars"/>.
        /// </summary>
        public static string Clean(string text, int maxChars)
        {
            var sb = new StringBuilder(Math.Min(text.Length, maxChars));
            var lastWasSpace = true;
            for (var i = 0; i < text.Length && sb.Length < maxChars; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                { sb.Append(c).Append(text[++i]); lastWasSpace = false; continue; }
                if (char.IsSurrogate(c) || c == '�') continue;
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    if (!lastWasSpace) { sb.Append(c == '\n' ? '\n' : ' '); lastWasSpace = true; }
                    continue;
                }
                sb.Append(c); lastWasSpace = false;
            }
            return sb.ToString().Trim();
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

        private sealed class LimitExceededException : IOException { }

        /// <summary>Read-only stream that stops with an exception after a fixed number of bytes.</summary>
        private sealed class LimitedStream : Stream
        {
            private readonly Stream _inner; private long _left;
            public LimitedStream(Stream inner, long limit) { _inner = inner; _left = limit; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left <= 0) throw new LimitExceededException();
                var n = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
                _left -= n;
                return n;
            }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
