using System.Text;
using MailArchiver.Services.Shared;
using Xunit;

namespace MailArchiver.Tests.Services;

/// <summary>Review finding F-15: the declared size of a picture inside a PDF is checked before it is decoded or copied.</summary>
public class ReviewFixesTextTests
{
    // A one-page PDF whose only picture DECLARES the given size but carries only a few bytes of data.
    private static byte[] PdfWithPicture(int width, int height)
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var objs = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>"),
            Encoding.ASCII.GetBytes("<< /Length 28 >>\nstream\nq 500 0 0 500 0 0 cm /Im0 Do Q\nendstream"),
            Encoding.ASCII.GetBytes($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Length {data.Length} >>\nstream\n")
                .Concat(data).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray(),
        };
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n"); ms.Write(objs[i]); W("\nendobj\n");
        }
        var xref = ms.Position;
        W($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:0000000000} 00000 n \n");
        W($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void F15_AHugeDeclaredPicture_IsSkippedBeforeAnyDecoding()
    {
        // 30000 x 30000 declared (900 million pixels) against a 1 million pixel limit.
        var result = PdfPageImages.Extract(PdfWithPicture(30000, 30000), maxPages: 5, CancellationToken.None, maxPixels: 1_000_000);

        Assert.Empty(result.Images);
        Assert.Contains(1, result.Skipped);   // the page is reported as not readable, not decoded, no exception
    }
}
