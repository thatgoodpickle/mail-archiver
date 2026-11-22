using System.Security.Cryptography;
using System.Text;
using MailArchiver.Models;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using OrtSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// OCR with the PaddleOCR PP-OCRv5 models through ONNX Runtime on the CPU: a text detector (DB) finds lines, each line
    /// is cut out upright and a recogniser (CTC, Latin script) reads it. The pipeline settings come from the models' own
    /// inference.yml and were checked by measurement (see doc/INTEGRITY-DESIGN.md and tools/ocr-reference.py).
    /// One image at a time. Nothing here is loaded until the first image, so the application starts without the models.
    /// </summary>
    public sealed class OnnxOcrEngine : IOcrEngine, IDisposable
    {
        public const string EngineName = "paddleocr-ppocrv5-onnx";
        private const string PipelineVersion = "1";

        private const int DetLongSide = 960;        // long side of the detector input, never enlarged
        private const double DetThresh = 0.3, DetBoxThresh = 0.6, DetUnclip = 1.5;
        private const int MaxBoxes = 400;           // lines read per image
        private const int RecHeight = 48, RecMaxWidth = 3200;

        private static readonly float[] DetMean = { 0.485f, 0.456f, 0.406f };
        private static readonly float[] DetStd = { 0.229f, 0.224f, 0.225f };

        private readonly IOptionsMonitor<AttachmentTextOptions> _optionsMonitor;
        /// <summary>Per-image limits are read on every use; the session settings (threads, provider) when the session is created.</summary>
        private AttachmentTextOptions _options => _optionsMonitor.CurrentValue;
        private readonly ILogger<OnnxOcrEngine> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _loadLock = new();

        private InferenceSession? _det, _rec;
        private string[] _chars = Array.Empty<string>();
        private string _version = EngineName + " (models not loaded)";
        private string? _unavailable;
        private bool _loaded;

        public OnnxOcrEngine(IOptionsMonitor<AttachmentTextOptions> options, ILogger<OnnxOcrEngine> logger)
        {
            _optionsMonitor = options; _logger = logger;
        }

        public string Name => EngineName;
        public string Version => _version;

        private string ModelDir => string.IsNullOrWhiteSpace(_options.ModelDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "ocr-models") : _options.ModelDirectory;

        private static string DictPath => Path.Combine(AppContext.BaseDirectory, "OcrData", "latin_ppocrv5_dict.txt");

        /// <summary>Cheap check (files present) until the models are loaded; afterwards what loading found.</summary>
        public string? Unavailable
        {
            get
            {
                if (_unavailable != null) return _unavailable;
                if (_loaded) return null;
                if (!File.Exists(Path.Combine(ModelDir, "det.onnx")) || !File.Exists(Path.Combine(ModelDir, "rec.onnx")))
                    return $"OCR models not found in {ModelDir} (det.onnx, rec.onnx)";
                if (!File.Exists(DictPath)) return "OCR character dictionary missing";
                return null;
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_loadLock)
            {
                if (_loaded) return;
                if (_unavailable != null) throw new InvalidOperationException(_unavailable);
                try
                {
                    var detPath = Path.Combine(ModelDir, "det.onnx"); var recPath = Path.Combine(ModelDir, "rec.onnx");
                    var dict = File.ReadAllText(DictPath, Encoding.UTF8).Split('\n').ToList();
                    if (dict.Count > 0 && dict[^1].Length == 0) dict.RemoveAt(dict.Count - 1);
                    // classes: blank, the dictionary, then the space character
                    _chars = new[] { string.Empty }.Concat(dict.Select(d => d.TrimEnd('\r'))).Concat(new[] { " " }).ToArray();

                    var provider = (_options.Provider ?? "auto").Trim().ToLowerInvariant();
                    if (provider is not ("auto" or "cpu"))
                        _logger.LogWarning("Ocr:Provider '{Provider}' is not available in this build; using the CPU", provider);
                    if (string.Equals(_options.Mode, "gpu", StringComparison.OrdinalIgnoreCase))
                        _logger.LogWarning("Ocr:Mode=gpu: no GPU image variant is built yet; using the CPU engine");

                    OrtSessionOptions NewOptions() => new()
                    {
                        IntraOpNumThreads = Math.Clamp(_options.Threads, 1, 16),
                        InterOpNumThreads = 1,
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                        EnableMemoryPattern = false,    // input sizes vary from image to image
                        EnableCpuMemArena = false,      // return memory after each run instead of keeping it
                        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                    };
                    using (var so = NewOptions()) _det = new InferenceSession(detPath, so);
                    using (var so = NewOptions()) _rec = new InferenceSession(recPath, so);

                    var recClasses = _rec.OutputMetadata.Values.First().Dimensions.Last();
                    if (recClasses > 0 && recClasses != _chars.Length)
                        throw new InvalidDataException($"recogniser has {recClasses} classes but the dictionary gives {_chars.Length}");

                    _version = $"{EngineName}/{PipelineVersion} det:{Hash8(detPath)} rec-latin:{Hash8(recPath)}";
                    _loaded = true;
                    _logger.LogInformation("OCR engine ready: {Version}, {Threads} thread(s)", _version, _options.Threads);
                }
                catch (Exception ex)
                {
                    _det?.Dispose(); _rec?.Dispose(); _det = null; _rec = null;
                    _unavailable = "OCR engine could not be loaded: " + ex.GetType().Name;
                    _logger.LogError(ex, "OCR engine could not be loaded; images stay NeedsOcr");
                    throw new InvalidOperationException(_unavailable, ex);
                }
            }
        }

        private static string Hash8(string path)
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs))[..8].ToLowerInvariant();
        }

        public async Task<OcrImageResult> RecognizeAsync(byte[] image, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);   // one image at a time
            try
            {
                return await Task.Run(() => Recognize(image, cancellationToken), cancellationToken);
            }
            finally { _gate.Release(); }
        }

        private OcrImageResult Recognize(byte[] bytes, CancellationToken ct)
        {
            try
            {
                var decoded = OcrImageDecoder.Decode(bytes, _options.MaxImagePixels);
                if (decoded.Error != null) return new OcrImageResult(string.Empty, decoded.Error);
                var img = decoded.Image!;

                EnsureLoaded();
                ct.ThrowIfCancellationRequested();

                // 1. detector input: long side at most DetLongSide, sides rounded to multiples of 32
                var r = Math.Max(img.Width, img.Height) > DetLongSide ? (double)DetLongSide / Math.Max(img.Width, img.Height) : 1.0;
                var nw = Math.Max(32, (int)Math.Round(img.Width * r / 32.0) * 32);
                var nh = Math.Max(32, (int)Math.Round(img.Height * r / 32.0) * 32);
                var small = img.Resize(nw, nh);
                var detInput = new float[3 * nh * nw];
                for (var c = 0; c < 3; c++)
                    for (var i = 0; i < nh * nw; i++)
                        detInput[c * nh * nw + i] = (small.Data[i * 3 + c] / 255f - DetMean[c]) / DetStd[c];

                float[] map;
                using (var ro = new RunOptions())
                using (ct.Register(() => ro.Terminate = true))
                {
                    var input = NamedOnnxValue.CreateFromTensor(_det!.InputMetadata.Keys.First(), new DenseTensor<float>(detInput, new[] { 1, 3, nh, nw }));
                    using var results = _det.Run(new[] { input }, new[] { _det.OutputMetadata.Keys.First() }, ro);
                    map = results.First().AsTensor<float>().ToArray();
                }
                ct.ThrowIfCancellationRequested();

                // 2. boxes -> upright line crops, in the original image's coordinates
                var boxes = OcrGeometry.BoxesFromProbabilityMap(map, nw, nh, DetThresh, DetBoxThresh, DetUnclip, MaxBoxes);
                double sx = (double)img.Width / nw, sy = (double)img.Height / nh;
                var lines = new List<(double Y, double X, double H, string Text)>();
                foreach (var box in boxes)
                {
                    ct.ThrowIfCancellationRequested();
                    var pts = box.Corners.Select(p => new OcrPoint(p.X * sx, p.Y * sy)).ToArray();
                    // order: top-left = smallest x+y, bottom-right = largest, top-right = smallest y-x, bottom-left = largest y-x
                    var tl = pts.MinBy(p => p.X + p.Y); var br = pts.MaxBy(p => p.X + p.Y);
                    var tr = pts.MinBy(p => p.Y - p.X); var bl = pts.MaxBy(p => p.Y - p.X);
                    var crop = img.CropQuad(tl, tr, br, bl);
                    if (crop == null) continue;
                    if ((double)crop.Height / crop.Width >= 1.5) crop = crop.Rotate90CounterClockwise();

                    var text = Recognise(crop, ct);
                    if (text.Length == 0) continue;
                    lines.Add(((pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2, pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y), text));
                }
                return new OcrImageResult(JoinLines(lines));
            }
            catch (OperationCanceledException) { throw; }
            catch (OnnxRuntimeException ex) when (ct.IsCancellationRequested) { throw new OperationCanceledException("ocr cancelled", ex, ct); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("OCR ", StringComparison.Ordinal)) { return new OcrImageResult(string.Empty, ex.Message); }
            catch (Exception ex)
            {
                // short, content-free reason; the details go to the log
                _logger.LogWarning(ex, "OCR failed for an image");
                return new OcrImageResult(string.Empty, "ocr failed: " + ex.GetType().Name);
            }
        }

        private string Recognise(BgrImage crop, CancellationToken ct)
        {
            var w = Math.Clamp((int)Math.Ceiling(RecHeight * (double)crop.Width / crop.Height), 8, RecMaxWidth);
            var strip = crop.Resize(w, RecHeight);
            var input = new float[3 * RecHeight * w];
            for (var c = 0; c < 3; c++)
                for (var i = 0; i < RecHeight * w; i++)
                    input[c * RecHeight * w + i] = (strip.Data[i * 3 + c] / 255f - 0.5f) / 0.5f;

            float[] logits; int steps;
            using (var ro = new RunOptions())
            using (ct.Register(() => ro.Terminate = true))
            {
                var tensor = NamedOnnxValue.CreateFromTensor(_rec!.InputMetadata.Keys.First(), new DenseTensor<float>(input, new[] { 1, 3, RecHeight, w }));
                using var results = _rec.Run(new[] { tensor }, new[] { _rec.OutputMetadata.Keys.First() }, ro);
                var t = results.First().AsTensor<float>();
                steps = t.Dimensions[1];
                logits = t.ToArray();
            }
            return CtcDecode(logits, steps, _chars);
        }

        /// <summary>Greedy CTC: best class per step, repeated classes collapsed, blank (0) dropped.</summary>
        public static string CtcDecode(float[] logits, int steps, string[] chars)
        {
            var classes = chars.Length;
            var sb = new StringBuilder();
            var prev = 0;
            for (var t = 0; t < steps; t++)
            {
                var best = 0; var bestV = float.MinValue;
                var off = t * classes;
                for (var k = 0; k < classes; k++) if (logits[off + k] > bestV) { bestV = logits[off + k]; best = k; }
                if (best != 0 && best != prev) sb.Append(chars[best]);
                prev = best;
            }
            return sb.ToString();
        }

        /// <summary>Lines in reading order: top to bottom, pieces of one line left to right.</summary>
        public static string JoinLines(List<(double Y, double X, double H, string Text)> items)
        {
            items.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
            var lines = new List<List<(double X, string Text)>>();
            List<(double X, string Text)>? cur = null; double cy = 0, ch = 0;
            foreach (var it in items)
            {
                if (cur != null && Math.Abs(it.Y - cy) < 0.6 * ch) cur.Add((it.X, it.Text));
                else { cur = new List<(double, string)> { (it.X, it.Text) }; lines.Add(cur); cy = it.Y; ch = it.H; }
            }
            return string.Join("\n", lines.Select(l => string.Join(" ", l.OrderBy(p => p.X).Select(p => p.Text))));
        }

        public void Dispose()
        {
            _det?.Dispose(); _rec?.Dispose(); _gate.Dispose();
        }
    }

    /// <summary>Decodes untrusted image bytes. The pixel count is checked BEFORE any pixel is decoded.</summary>
    public static class OcrImageDecoder
    {
        public readonly record struct Decoded(BgrImage? Image, string? Error);

        public static Decoded Decode(byte[] bytes, long maxPixels)
        {
            try
            {
                using var stream = new SKMemoryStream(bytes);
                using var codec = SKCodec.Create(stream);
                if (codec == null) return new Decoded(null, "unsupported or corrupt image format");

                var info = codec.Info;
                if (info.Width < 8 || info.Height < 8) return new Decoded(null, "image too small to read");
                if ((long)info.Width * info.Height > maxPixels)
                    return new Decoded(null, $"image too large ({info.Width}x{info.Height} pixels)");

                using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                if (bitmap == null) return new Decoded(null, "image could not be decoded");

                var px = bitmap.GetPixelSpan();
                var rowBytes = bitmap.RowBytes;
                var img = new BgrImage(info.Width, info.Height);
                for (var y = 0; y < info.Height; y++)
                {
                    for (var x = 0; x < info.Width; x++)
                    {
                        var i = y * rowBytes + x * 4;
                        int a = px[i + 3];
                        // transparent areas count as white paper
                        img.Data[(y * info.Width + x) * 3 + 0] = (byte)((px[i + 0] * a + 255 * (255 - a)) / 255);
                        img.Data[(y * info.Width + x) * 3 + 1] = (byte)((px[i + 1] * a + 255 * (255 - a)) / 255);
                        img.Data[(y * info.Width + x) * 3 + 2] = (byte)((px[i + 2] * a + 255 * (255 - a)) / 255);
                    }
                }
                return new Decoded(img, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new Decoded(null, "image could not be decoded: " + ex.GetType().Name);
            }
        }
    }
}
