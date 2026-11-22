namespace MailArchiver.Models
{
    /// <summary>
    /// Text read out of one attachment, keyed by the attachment's SHA-256 so an attachment seen in many
    /// emails is read once. Derived data: it can be thrown away and rebuilt, and it is only ever used to
    /// build the search index. The text comes from untrusted mail and is never shown or acted upon.
    /// </summary>
    public class AttachmentText
    {
        public const string Done = "Done";
        /// <summary>A supported type with no text layer (scan, photo): waits for an OCR engine.</summary>
        public const string NeedsOcr = "NeedsOcr";
        /// <summary>No extractor for this type.</summary>
        public const string Unsupported = "Unsupported";
        /// <summary>Too large, or the original/attachment could not be read, or the extractor failed.</summary>
        public const string Failed = "Failed";

        public const string TextLayer = "textlayer";
        public const string Ocr = "ocr";

        public string Sha256 { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Status { get; set; } = Done;
        public string Method { get; set; } = TextLayer;
        public string Engine { get; set; } = string.Empty;
        public string EngineVersion { get; set; } = string.Empty;
        public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
        public string? Error { get; set; }
    }

    /// <summary>Settings of the attachment text worker ("Ocr" section). Off by default.</summary>
    public class AttachmentTextOptions
    {
        public const string Section = "Ocr";

        /// <summary>off (default: nothing is read), cpu or gpu. No OCR engine exists yet, so cpu and gpu both read the text layer only.</summary>
        public string Mode { get; set; } = "off";

        public bool IsOff => string.Equals(Mode, "off", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(Mode);

        /// <summary>Attachments read per round.</summary>
        public int BatchSize { get; set; } = 25;

        /// <summary>Seconds to wait when there was nothing to do.</summary>
        public int IdleDelaySeconds { get; set; } = 60;

        /// <summary>Delay before the first round, in minutes, so startup settles first.</summary>
        public int StartupDelayMinutes { get; set; } = 2;

        /// <summary>Attachments larger than this are not read (status Failed, "too large").</summary>
        public int MaxAttachmentMb { get; set; } = 50;

        /// <summary>
        /// Text kept per attachment and per email, in KB. Search builds one full-text vector from all fields
        /// of an email and PostgreSQL caps a vector at 1 MB, so this stays well below that.
        /// </summary>
        public int MaxTextKb { get; set; } = 128;

        /// <summary>Seconds allowed per attachment.</summary>
        public int TimeoutSeconds { get; set; } = 60;

        // ---- OCR engine (cpu mode) ----

        /// <summary>Directory holding det.onnx and rec.onnx. Empty = "ocr-models" next to the application.</summary>
        public string ModelDirectory { get; set; } = string.Empty;

        /// <summary>auto or cpu. cuda, rocm and openvino are not built yet and fall back to cpu with a log line.</summary>
        public string Provider { get; set; } = "auto";

        /// <summary>CPU threads the OCR engine may use (one image at a time).</summary>
        public int Threads { get; set; } = 2;

        /// <summary>Images with more pixels than this are not read (checked before decoding). Default 25 million.</summary>
        public long MaxImagePixels { get; set; } = 25_000_000;

        /// <summary>Seconds allowed per image (and per PDF page).</summary>
        public int TimeoutSecondsPerImage { get; set; } = 30;

        /// <summary>Pages read per scanned PDF; further pages are ignored.</summary>
        public int MaxPages { get; set; } = 20;

        /// <summary>True for cpu and gpu (gpu falls back to the CPU engine for now).</summary>
        public bool UsesOcr => string.Equals(Mode, "cpu", StringComparison.OrdinalIgnoreCase) || string.Equals(Mode, "gpu", StringComparison.OrdinalIgnoreCase);
    }
}
