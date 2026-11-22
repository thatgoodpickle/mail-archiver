namespace MailArchiver.Services.Shared
{
    /// <summary>Text recognised in one image. <see cref="Error"/> is set when the image could not be read at all.</summary>
    public sealed record OcrImageResult(string Text, string? Error = null)
    {
        public bool Ok => Error == null;
    }

    /// <summary>
    /// Reads text out of an image (scans, photos, screenshots). Used by the attachment text worker for items that have no
    /// text layer. The input is untrusted: implementations check size before decoding, bound time and memory, and report
    /// a bad image as an error result, never as a crash.
    /// </summary>
    public interface IOcrEngine
    {
        string Name { get; }

        /// <summary>Identifies engine and models, recorded with every result so a better engine can replace it later.</summary>
        string Version { get; }

        /// <summary>Null when the engine can run; otherwise the reason it cannot (models missing, runtime missing).</summary>
        string? Unavailable { get; }

        Task<OcrImageResult> RecognizeAsync(byte[] image, CancellationToken cancellationToken);
    }
}
