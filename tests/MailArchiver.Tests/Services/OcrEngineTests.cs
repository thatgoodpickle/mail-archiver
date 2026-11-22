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
using MimeKit;
using Xunit;
using Xunit.Abstractions;
using Opt = Microsoft.Extensions.Options.Options;

namespace MailArchiver.Tests.Services;

/// <summary>Where the OCR models and the synthetic test images are. CI fetches the models with tools/fetch-ocr-models.sh.</summary>
internal static class OcrTestEnv
{
    public static string? ModelDir
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("OCR_MODEL_DIR");
            return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "det.onnx")) && File.Exists(Path.Combine(d, "rec.onnx")) ? d : null;
        }
    }

    public static string Data(string file) => Path.Combine(AppContext.BaseDirectory, "TestData", "ocr", file);

    public static OnnxOcrEngine NewEngine(string? modelDir = null, long maxPixels = 25_000_000)
        => new(new MutableOptionsMonitor<AttachmentTextOptions>(new AttachmentTextOptions { Mode = "cpu", ModelDirectory = modelDir ?? ModelDir ?? "/nonexistent-ocr-models", Threads = 2, MaxImagePixels = maxPixels }),
            NullLogger<OnnxOcrEngine>.Instance);

    /// <summary>Share of the expected words found in the output, ignoring order and counting repeats (as tools/score-ocr.py does).</summary>
    public static double WordRecall(string truth, string got)
    {
        var want = truth.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var have = got.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var total = want.Values.Sum();
        var found = want.Sum(kv => Math.Min(kv.Value, have.TryGetValue(kv.Key, out var c) ? c : 0));
        return total == 0 ? 1 : (double)found / total;
    }
}

/// <summary>Runs only when the OCR models are available (OCR_MODEL_DIR). In CI (OCR_REQUIRE_MODELS=1) their absence is a failure, see below.</summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        if (OcrTestEnv.ModelDir == null) Skip = "OCR models not found (set OCR_MODEL_DIR; tools/fetch-ocr-models.sh fetches them)";
    }
}

public class OcrGeometryTests
{
    private static float[] Map(int w, int h, params (int X0, int Y0, int X1, int Y1, float P)[] rects)
    {
        var m = new float[w * h];
        foreach (var (x0, y0, x1, y1, p) in rects)
            for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++) m[y * w + x] = p;
        return m;
    }

    [Fact]
    public void AFilledBar_BecomesOneBox_GrownByTheUnclipDistance()
    {
        // 60 x 12 pixels of text-like probability: after unclip (1.5) each side grows by about w*h*1.5/(2*(w+h))
        var boxes = OcrGeometry.BoxesFromProbabilityMap(Map(200, 80, (40, 30, 99, 41, 0.9f)), 200, 80, 0.3, 0.6, 1.5);
        var box = Assert.Single(boxes);
        var xs = box.Corners.Select(c => c.X); var ys = box.Corners.Select(c => c.Y);
        Assert.InRange((xs.Min() + xs.Max()) / 2, 69, 71);
        Assert.InRange((ys.Min() + ys.Max()) / 2, 35, 36.5);
        Assert.InRange(xs.Max() - xs.Min(), 59 + 2 * 6, 59 + 2 * 8);    // 59 wide between pixel centres, grown on both sides
        Assert.InRange(ys.Max() - ys.Min(), 11 + 2 * 6, 11 + 2 * 8);
        Assert.InRange(box.Score, 0.89, 0.91);
    }

    [Fact]
    public void TwoSeparateBars_AreTwoBoxes()
    {
        var boxes = OcrGeometry.BoxesFromProbabilityMap(Map(200, 100, (10, 10, 120, 24, 0.9f), (10, 60, 90, 74, 0.9f)), 200, 100, 0.3, 0.6, 1.5);
        Assert.Equal(2, boxes.Count);
    }

    [Fact]
    public void WeakAndTinyBlobs_AreDropped()
    {
        var weak = Map(200, 80, (40, 30, 99, 41, 0.45f));   // above the pixel threshold, below the box score threshold
        Assert.Empty(OcrGeometry.BoxesFromProbabilityMap(weak, 200, 80, 0.3, 0.6, 1.5));
        var tiny = Map(200, 80, (40, 30, 41, 31, 0.9f));
        Assert.Empty(OcrGeometry.BoxesFromProbabilityMap(tiny, 200, 80, 0.3, 0.6, 1.5));
    }

    [Fact]
    public void ATiltedBar_GetsATiltedRectangle_NotItsBoundingBox()
    {
        // a bar 120 long and 8 thick, tilted by 10 degrees
        const int w = 300, h = 120; var m = new float[w * h];
        double a = 10 * Math.PI / 180, ux = Math.Cos(a), uy = Math.Sin(a);
        for (var t = 0; t < 120; t++)
            for (var k = -4; k < 4; k++)
            {
                int x = (int)Math.Round(60 + t * ux - k * uy), y = (int)Math.Round(40 + t * uy + k * ux);
                m[y * w + x] = 0.95f;
            }
        var box = Assert.Single(OcrGeometry.BoxesFromProbabilityMap(m, w, h, 0.3, 0.6, 0.0001));   // no growth: compare the raw rectangle
        double Side(int i, int j) => Math.Sqrt(Math.Pow(box.Corners[i].X - box.Corners[j].X, 2) + Math.Pow(box.Corners[i].Y - box.Corners[j].Y, 2));
        var longSide = Math.Max(Side(0, 1), Side(1, 2)); var shortSide = Math.Min(Side(0, 1), Side(1, 2));
        Assert.InRange(longSide, 110, 125);
        Assert.InRange(shortSide, 6, 12);   // an axis-aligned box around this bar would be about 118 x 29
    }

    [Fact]
    public void ConvexHull_AndMinAreaRect_OfARotatedSquare()
    {
        var pts = new List<OcrPoint> { new(0, 10), new(10, 0), new(20, 10), new(10, 20), new(10, 10), new(10, 5) };
        var hull = OcrGeometry.ConvexHull(pts);
        Assert.Equal(4, hull.Count);
        var r = OcrGeometry.MinAreaRect(hull);
        Assert.InRange(r.W * r.H, 199, 201);   // a 14.14 x 14.14 square
        Assert.InRange(Math.Abs(r.W - r.H), 0, 0.01);
    }
}

public class BgrImageTests
{
    private static BgrImage Gradient(int w, int h)
    {
        var img = new BgrImage(w, h);
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        { img.Data[(y * w + x) * 3] = (byte)x; img.Data[(y * w + x) * 3 + 1] = (byte)y; img.Data[(y * w + x) * 3 + 2] = 7; }
        return img;
    }

    [Fact]
    public void Resize_ToTheSameSize_KeepsThePixels()
    {
        var img = Gradient(20, 10);
        Assert.Equal(img.Data, img.Resize(20, 10).Data);
    }

    [Fact]
    public void Resize_Down_AveragesNeighbours()
    {
        var img = new BgrImage(4, 1);
        foreach (var (x, v) in new[] { (0, 0), (1, 100), (2, 200), (3, 100) }) { img.Data[x * 3] = (byte)v; img.Data[x * 3 + 1] = (byte)v; img.Data[x * 3 + 2] = (byte)v; }
        var half = img.Resize(2, 1);
        Assert.InRange(half.Data[0], 49, 51);     // (0 + 100) / 2
        Assert.InRange(half.Data[3], 149, 151);   // (200 + 100) / 2
    }

    [Fact]
    public void CropQuad_AxisAligned_ReturnsThatRegion()
    {
        var img = Gradient(50, 40);
        var crop = img.CropQuad(new(10, 5), new(30, 5), new(30, 20), new(10, 20))!;
        Assert.Equal(20, crop.Width); Assert.Equal(15, crop.Height);
        Assert.Equal(10, crop.Data[0]);          // blue holds x
        Assert.Equal(5, crop.Data[1]);           // green holds y
        Assert.Equal(11, crop.Data[(0 * 20 + 1) * 3]);
    }

    [Fact]
    public void CropQuad_TooSmall_IsNull() => Assert.Null(Gradient(20, 20).CropQuad(new(1, 1), new(2, 1), new(2, 2), new(1, 2)));

    [Fact]
    public void Rotate90CounterClockwise_MovesTheTopRightToTheTopLeft()
    {
        var img = new BgrImage(3, 2);
        for (var i = 0; i < 6; i++) { img.Data[i * 3] = (byte)(i + 1); }   // row 0: 1 2 3, row 1: 4 5 6
        var r = img.Rotate90CounterClockwise();
        Assert.Equal(2, r.Width); Assert.Equal(3, r.Height);
        // numpy.rot90 of [[1,2,3],[4,5,6]] is [[3,6],[2,5],[1,4]]
        Assert.Equal(new byte[] { 3, 6, 2, 5, 1, 4 }, Enumerable.Range(0, 6).Select(i => r.Data[i * 3]).ToArray());
    }
}

public class OcrDecodeAndLimitTests
{
    private static uint[] Table = Enumerable.Range(0, 256).Select(n => { var c = (uint)n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; return c; }).ToArray();
    private static uint Crc(byte[] b) { var c = 0xFFFFFFFFu; foreach (var x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFFu; }
    private static byte[] Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>A PNG that only claims to be width x height: signature, IHDR, a bogus IDAT. No pixel data exists.</summary>
    private static byte[] PngClaiming(uint width, uint height)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            ms.Write(Be((uint)data.Length));
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(body); ms.Write(Be(Crc(body)));
        }
        Chunk("IHDR", Be(width).Concat(Be(height)).Concat(new byte[] { 8, 0, 0, 0, 0 }).ToArray());
        Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x01, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x01 });
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void AnImageOverThePixelLimit_IsRefusedBeforeAnyPixelIsDecoded()
    {
        var d = OcrImageDecoder.Decode(PngClaiming(50_000, 50_000), 25_000_000);   // 2.5 billion pixels would be 10 GB
        Assert.Null(d.Image);
        Assert.Contains("too large", d.Error);
    }

    [Fact]
    public void GarbageAndEmptyInput_AreErrorsNotCrashes()
    {
        Assert.NotNull(OcrImageDecoder.Decode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 25_000_000).Error);
        Assert.NotNull(OcrImageDecoder.Decode(Array.Empty<byte>(), 25_000_000).Error);
    }

    [Fact]
    public void CtcDecode_CollapsesRepeatsAndDropsBlanks()
    {
        var chars = new[] { "", "a", "b", " " };
        var steps = new[] { 1, 1, 0, 1, 2, 2, 3 };
        var logits = new float[steps.Length * chars.Length];
        for (var t = 0; t < steps.Length; t++) logits[t * chars.Length + steps[t]] = 5f;
        Assert.Equal("aab ", OnnxOcrEngine.CtcDecode(logits, steps.Length, chars));
    }

    [Fact]
    public void JoinLines_KeepsReadingOrder()
    {
        var items = new List<(double Y, double X, double H, string Text)>
        {
            (100, 300, 20, "world"), (101, 10, 20, "hello"), (40, 10, 20, "first line")
        };
        Assert.Equal("first line\nhello world", OnnxOcrEngine.JoinLines(items));
    }

    [Fact]
    public async Task WithoutModels_TheEngineSaysSoAndReturnsAnErrorResult()
    {
        using var engine = OcrTestEnv.NewEngine("/nonexistent-ocr-models");
        Assert.NotNull(engine.Unavailable);
        var png = File.ReadAllBytes(OcrTestEnv.Data("01_clean_print.png"));
        var r = await engine.RecognizeAsync(png, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.NotNull(engine.Unavailable);   // stays unavailable, no crash
    }

    [Fact]
    public void InCi_TheModelsMustBeThere()
    {
        if (Environment.GetEnvironmentVariable("OCR_REQUIRE_MODELS") == "1")
            Assert.True(OcrTestEnv.ModelDir != null, "OCR_REQUIRE_MODELS=1 but det.onnx/rec.onnx were not found in OCR_MODEL_DIR");
    }
}

/// <summary>Quality of the engine on the synthetic images (known text). Thresholds are set from measured results.</summary>
public class OcrQualityTests
{
    private readonly ITestOutputHelper _out;
    public OcrQualityTests(ITestOutputHelper output) => _out = output;

    // file -> minimum word recall
    private static readonly (string File, double MinRecall)[] Cases =
    {
        ("01_clean_print", 0.9), ("03_invoice_table", 0.9), ("04_skewed_4deg", 0.9),
        ("05_noisy_photo", 0.9), ("06_accents", 0.9), ("09_white_on_dark", 0.9)
    };

    [OcrFact]
    public async Task ACancelledToken_StopsTheEngine()
    {
        using var engine = OcrTestEnv.NewEngine();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeAsync(File.ReadAllBytes(OcrTestEnv.Data("01_clean_print.png")), cts.Token));
    }

    [OcrFact]
    public async Task SyntheticImages_AreReadWithKnownAccuracy()
    {
        using var engine = OcrTestEnv.NewEngine();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var failures = new List<string>(); double sum = 0;
        foreach (var (file, min) in Cases)
        {
            var path = OcrTestEnv.Data(file + (file.StartsWith("05") ? ".jpg" : ".png"));
            var truth = File.ReadAllText(OcrTestEnv.Data(file + ".txt"));
            var t0 = sw.Elapsed;
            var r = await engine.RecognizeAsync(File.ReadAllBytes(path), CancellationToken.None);
            var recall = OcrTestEnv.WordRecall(truth, r.Text); sum += recall;
            _out.WriteLine($"OCRSCORE {file}: ok={r.Ok} recall={recall:P1} time={(sw.Elapsed - t0).TotalSeconds:F2}s error={r.Error}");
            if (!r.Ok || recall < min) failures.Add($"{file}: recall {recall:P1} (min {min:P0}) error={r.Error}");
        }
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        _out.WriteLine($"OCRSCORE mean recall {sum / Cases.Length:P1}; engine {engine.Version}");
        _out.WriteLine($"OCRSCORE memory: peak working set of the whole test process {proc.PeakWorkingSet64 / (1024 * 1024)} MB, managed heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }
}

/// <summary>The worker path end to end on real database rows: lean and full-storage emails, scanned PDFs, no engine, mode off.</summary>
[Collection(TestDbFixture.CollectionName)]
public class OcrProcessorTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ocrp-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _shas = new();

    public OcrProcessorTests(TestDbFixture fixture) => _fixture = fixture;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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

    private (EmailCoreService Svc, StrippedContentHydrator Hydrator, IntegrityService Integrity) Make(MailArchiverDbContext ctx)
    {
        var raw = Raw(); var opts = Opt.Create(raw);
        var fs = new FileSystemRawMessageStore(ctx, opts, new RawMessagePathResolver(raw.FileSystem, _root), NullLogger<FileSystemRawMessageStore>.Instance);
        var router = new RawMessageStoreRouter(ctx, opts, new PostgresRawMessageStore(ctx, opts), fs);
        var hydrator = new StrippedContentHydrator(ctx, new OriginalMessageReader(router));
        var svc = new EmailCoreService(ctx, NullLogger<EmailCoreService>.Instance,
            new DateTimeHelper(Opt.Create(new TimeZoneOptions { DisplayTimeZoneId = "Europe/Berlin" })),
            Opt.Create(new BatchOperationOptions()), Opt.Create(new DashboardOptions { CacheSeconds = 0 }),
            memoryCache: null, rawMessageOptions: opts, rawMessageStore: router, contentHydrator: hydrator,
            storageMode: Opt.Create(new StorageModeOptions { Mode = "Lean" }),
            leanIngest: new LeanIngest(fs, opts, NullLogger<LeanIngest>.Instance));
        return (svc, hydrator, new IntegrityService(ctx, router));
    }

    private static AttachmentTextProcessor NewProcessor(MailArchiverDbContext ctx, StrippedContentHydrator hydrator, IOcrEngine? engine, string mode = "cpu")
        => new(ctx, hydrator, new MutableOptionsMonitor<AttachmentTextOptions>(new AttachmentTextOptions { Mode = mode, MaxPages = 5 }), NullLogger<AttachmentTextProcessor>.Instance, engine);

    private static async Task<MailAccount> SeedAccountAsync(MailArchiverDbContext ctx)
    {
        var a = new MailAccount
        {
            Name = $"oc-{Guid.NewGuid():N}".Substring(0, 20), EmailAddress = $"{Guid.NewGuid():N}@test.local",
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

    private async Task<int> ArchiveLeanAsync(EmailCoreService svc, MailAccount account, byte[] attachment, string fileName, string type)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("", "a@example.test")); m.To.Add(new MailboxAddress("", "b@example.test"));
        m.Subject = "ocr " + Guid.NewGuid().ToString("N");
        m.MessageId = $"{Guid.NewGuid():N}@example.test";
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "plain body" } };
        mixed.Add(new MimePart(ContentType.Parse(type))
        {
            Content = new MimeContent(new MemoryStream(attachment)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
            ContentTransferEncoding = ContentEncoding.Base64
        });
        m.Body = mixed;
        using var ms = new MemoryStream(); m.WriteTo(ms);
        var bytes = ms.ToArray();
        using var read = new MemoryStream(bytes);
        Assert.True(await svc.ArchiveEmailAsync(account, MimeMessage.Load(read), false, "INBOX", new RawMessageCapture(bytes, RawMessageSource.Imap)));
        _shas.Add(RawMessageCodec.ComputeSha256(attachment));
        var ctx = NewContext();
        return await ctx.ArchivedEmails.Where(e => e.MailAccountId == account.Id && e.Subject == m.Subject).Select(e => e.Id).SingleAsync();
    }

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
        for (var i = 0; i < 100; i++) if (await p.ProcessBatchAsync() == 0) return;
        throw new Exception("the queue did not drain");
    }

    private static async Task<int[]> SearchAsync(EmailCoreService svc, MailAccount account, string term)
    {
        var (emails, _) = await svc.SearchEmailsAsync(term, null, null, null, null, null, 0, 50, new List<int> { account.Id });
        return emails.Select(e => e.Id).ToArray();
    }

    /// <summary>A one-page PDF whose only content is a full-page JPEG (what a scanner produces).</summary>
    private static byte[] ScanPdf(byte[] jpeg, int width, int height)
    {
        var ms = new MemoryStream(); var offsets = new List<long>();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b); }
        Write("%PDF-1.4\n");
        void Obj(int n, string head, byte[]? stream = null)
        {
            offsets.Add(ms.Position);
            Write($"{n} 0 obj\n{head}");
            if (stream != null) { Write("\nstream\n"); ms.Write(stream); Write("\nendstream"); }
            Write("\nendobj\n");
        }
        var content = Encoding.ASCII.GetBytes($"q {width} 0 0 {height} 0 0 cm /Im0 Do Q");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>");
        Obj(4, $"<< /Length {content.Length} >>", content);
        Obj(5, $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>", jpeg);
        var xref = ms.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    [OcrFact]
    public async Task LeanEmail_WithAnImage_BecomesSearchableByAWordInThePicture_AndIntegrityStaysValid()
    {
        var ctx = NewContext(); var account = await SeedAccountAsync(ctx);
        try
        {
            var (svc, hydrator, integrity) = Make(ctx);
            using var engine = OcrTestEnv.NewEngine();
            var png = File.ReadAllBytes(OcrTestEnv.Data("testwords.png"));
            var id = await ArchiveLeanAsync(svc, account, png, "photo.png", "image/png");

            Assert.Empty(await SearchAsync(svc, account, "zeppelin"));   // not before OCR
            await DrainAsync(NewProcessor(ctx, hydrator, engine));

            Assert.Equal(new[] { id }, await SearchAsync(svc, account, "zeppelin"));
            await using var read = NewContext();
            var text = await read.AttachmentTexts.SingleAsync(t => t.Sha256 == RawMessageCodec.ComputeSha256(png));
            Assert.Equal(AttachmentText.Done, text.Status); Assert.Equal(AttachmentText.Ocr, text.Method);
            Assert.StartsWith(OnnxOcrEngine.EngineName, text.Engine); Assert.Contains("det:", text.EngineVersion);
            Assert.Equal(IntegrityStatus.Valid, (await integrity.VerifyAsync(id)).Status);
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [OcrFact]
    public async Task FullStorageEmail_WithAnImage_IsReadToo()
    {
        var ctx = NewContext(); var account = await SeedAccountAsync(ctx);
        try
        {
            var (svc, hydrator, _) = Make(ctx);
            using var engine = OcrTestEnv.NewEngine();
            var png = File.ReadAllBytes(OcrTestEnv.Data("testwords.png"));
            var id = await InsertFullAsync(ctx, account, png, "photo.png", "image/png");
            await DrainAsync(NewProcessor(ctx, hydrator, engine));
            Assert.Equal(new[] { id }, await SearchAsync(svc, account, "harmonica"));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [OcrFact]
    public async Task ScannedPdf_WithAnEmbeddedJpeg_IsReadThroughItsPageImage()
    {
        var ctx = NewContext(); var account = await SeedAccountAsync(ctx);
        try
        {
            var (svc, hydrator, _) = Make(ctx);
            using var engine = OcrTestEnv.NewEngine();
            var pdf = ScanPdf(File.ReadAllBytes(OcrTestEnv.Data("scan.jpg")), 1100, 165);
            var id = await ArchiveLeanAsync(svc, account, pdf, "letter.pdf", "application/pdf");
            await DrainAsync(NewProcessor(ctx, hydrator, engine));
            Assert.Equal(new[] { id }, await SearchAsync(svc, account, "harpsichord"));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task WithoutModels_ImagesStayNeedsOcr_AndAreReadAfterARescanOnceTheEngineIsThere()
    {
        var ctx = NewContext(); var account = await SeedAccountAsync(ctx);
        try
        {
            var (svc, hydrator, _) = Make(ctx);
            var png = File.ReadAllBytes(OcrTestEnv.Data("testwords.png"));
            var id = await InsertFullAsync(ctx, account, png, "photo.png", "image/png");

            using var absent = OcrTestEnv.NewEngine("/nonexistent-ocr-models");
            await DrainAsync(NewProcessor(ctx, hydrator, absent));
            await using (var read = NewContext())
                Assert.Equal(AttachmentText.NeedsOcr, (await read.AttachmentTexts.SingleAsync(t => t.Sha256 == RawMessageCodec.ComputeSha256(png))).Status);

            if (OcrTestEnv.ModelDir == null) return;   // the second half needs the models

            using var engine = OcrTestEnv.NewEngine();
            var processor = NewProcessor(ctx, hydrator, engine);
            Assert.Equal(1, await processor.RescanNeedsOcrAsync());
            await DrainAsync(processor);
            Assert.Equal(new[] { id }, await SearchAsync(svc, account, "kumquat"));
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [Fact]
    public async Task ModeOff_NeverOcrs_EvenWithAnEngine()
    {
        var ctx = NewContext(); var account = await SeedAccountAsync(ctx);
        try
        {
            var (_, hydrator, _) = Make(ctx);
            using var engine = OcrTestEnv.NewEngine();
            var png = File.ReadAllBytes(OcrTestEnv.Data("testwords.png"));
            await InsertFullAsync(ctx, account, png, "photo.png", "image/png");
            await DrainAsync(NewProcessor(ctx, hydrator, engine, mode: "off"));
            await using var read = NewContext();
            Assert.Equal(AttachmentText.NeedsOcr, (await read.AttachmentTexts.SingleAsync(t => t.Sha256 == RawMessageCodec.ComputeSha256(png))).Status);
        }
        finally { await CleanupAsync(ctx, account); }
    }

    [OcrFact]
    public async Task ACorruptImage_IsFailedWithAReason_NotACrash()
    {
        var ctx = NewContext(); var account = await SeedAccountAsync(ctx);
        try
        {
            var (_, hydrator, _) = Make(ctx);
            using var engine = OcrTestEnv.NewEngine();
            var junk = Encoding.ASCII.GetBytes("this is not a png " + Guid.NewGuid());
            await InsertFullAsync(ctx, account, junk, "broken.png", "image/png");
            await DrainAsync(NewProcessor(ctx, hydrator, engine));
            await using var read = NewContext();
            var row = await read.AttachmentTexts.SingleAsync(t => t.Sha256 == RawMessageCodec.ComputeSha256(junk));
            Assert.Equal(AttachmentText.Failed, row.Status);
            Assert.False(string.IsNullOrEmpty(row.Error));
        }
        finally { await CleanupAsync(ctx, account); }
    }
}
