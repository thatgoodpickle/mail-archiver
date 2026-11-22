using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MailArchiver.Services.Shared
{
    public sealed record PdfPageImage(int Page, byte[] Bytes, string Format);

    /// <summary>
    /// Pulls the embedded pictures out of a PDF so a scan can be read by the OCR engine. A scanned page is usually one
    /// page-sized picture (JPEG, or a lossless one PdfPig can turn into PNG). Pictures of other kinds (bilevel fax
    /// formats, JPEG 2000) cannot be turned into an image here: the page is reported in <see cref="Skipped"/>.
    /// Page and picture counts and sizes are capped; the PDF is untrusted.
    /// </summary>
    public static class PdfPageImages
    {
        private const int MinSide = 100;               // smaller pictures are logos and icons
        private const int MaxPerPage = 4;
        private const long MaxBytesTotal = 120L * 1024 * 1024;

        public sealed record Result(IReadOnlyList<PdfPageImage> Images, IReadOnlyList<int> Skipped, int Pages);

        public static Result Extract(byte[] pdf, int maxPages, CancellationToken ct)
        {
            var images = new List<PdfPageImage>();
            var skipped = new List<int>();
            long total = 0; var pages = 0;

            using var doc = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (pages >= Math.Max(1, maxPages)) break;
                pages++;

                var found = 0; var failed = 0;
                foreach (var image in page.GetImages().OrderByDescending(i => (long)i.WidthInSamples * i.HeightInSamples))
                {
                    if (image.WidthInSamples < MinSide || image.HeightInSamples < MinSide) continue;
                    if (found >= MaxPerPage) break;

                    byte[]? bytes = null; string format = string.Empty;
                    var raw = image.RawBytes.ToArray();
                    if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF) { bytes = raw; format = "jpeg"; }
                    else if (image.TryGetPng(out var png) && png is { Length: > 0 }) { bytes = png; format = "png"; }

                    if (bytes == null) { failed++; continue; }
                    total += bytes.LongLength;
                    if (total > MaxBytesTotal) throw new InvalidDataException("embedded pictures too large");
                    images.Add(new PdfPageImage(pages, bytes, format));
                    found++;
                }
                if (found == 0 && failed > 0) skipped.Add(pages);
            }
            return new Result(images, skipped, pages);
        }
    }
}
