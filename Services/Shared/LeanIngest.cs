using MailArchiver.Models;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailArchiver.Services.Shared
{
    /// <summary>Where one attachment sits in the verified original and the hash of its decoded bytes.</summary>
    public sealed record LeanAttachmentRecord(string PartPath, string Sha256);

    /// <summary>
    /// A verified original ready to be attached to a lean email row: the file is written, read back
    /// and its SHA-256 matches; every attachment was found again in it by hash.
    /// </summary>
    public sealed record LeanIngestPlan(RawMessage Row, IReadOnlyList<LeanAttachmentRecord> Attachments);

    public interface ILeanIngest
    {
        /// <summary>
        /// Writes the original (hash-named, write-once), reads it back from disk, parses it and finds
        /// each of <paramref name="attachmentBytes"/> (decoded, in the same order) in it. Returns
        /// null on ANY failure, logging the reason; the caller then stores the email in full.
        /// </summary>
        Task<LeanIngestPlan?> TryPrepareAsync(RawMessageCapture capture, IReadOnlyList<byte[]> attachmentBytes);
    }

    public sealed class LeanIngest : ILeanIngest
    {
        private readonly FileSystemRawMessageStore _store;
        private readonly RawMessageOptions _options;
        private readonly ILogger<LeanIngest> _logger;

        public LeanIngest(FileSystemRawMessageStore store, IOptions<RawMessageOptions> options, ILogger<LeanIngest> logger)
        {
            _store = store;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<LeanIngestPlan?> TryPrepareAsync(RawMessageCapture capture, IReadOnlyList<byte[]> attachmentBytes)
        {
            // Never let a failure here lose mail: every problem means "store it in full".
            try
            {
                if (!_options.FileSystem.HashNamed || !_options.WritesToFileSystem)
                    return Fail("filesystem storage with hash-named files is not configured");

                var row = _store.CreateRow(0, capture);
                if (row == null || !row.IsFileSystemBacked)
                    return Fail("the original is not storable (disabled, empty or over the size limit)");

                await _store.PersistAsync(row, capture);

                var verified = await _store.ReadWrittenAsync(row);
                if (verified == null)
                    return Fail($"the original written to {row.StoragePath} could not be read back with a matching SHA-256");

                var records = new List<LeanAttachmentRecord>(attachmentBytes.Count);
                if (attachmentBytes.Count > 0)
                {
                    using var stream = new MemoryStream(verified, writable: false);
                    var message = await MimeMessage.LoadAsync(ParserOptions.Default, stream);

                    // Decode every part of the file just written and index it by hash.
                    var candidates = new List<(string Path, string Sha, long Length, bool Used)>();
                    foreach (var (path, entity) in MimePartLocator.Enumerate(message))
                    {
                        if (entity is not MimePart part || part.Content == null) continue;
                        using var ms = new MemoryStream();
                        await part.Content.DecodeToAsync(ms);
                        var bytes = ms.ToArray();
                        candidates.Add((path, RawMessageCodec.ComputeSha256(bytes), bytes.LongLength, false));
                    }

                    foreach (var attachment in attachmentBytes)
                    {
                        var sha = RawMessageCodec.ComputeSha256(attachment);
                        var index = candidates.FindIndex(c => !c.Used && c.Sha == sha && c.Length == attachment.LongLength);
                        if (index < 0)
                            return Fail("an attachment could not be found again in the stored original");

                        var c = candidates[index];
                        candidates[index] = c with { Used = true };
                        records.Add(new LeanAttachmentRecord(c.Path, sha));
                    }
                }

                return new LeanIngestPlan(row, records);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lean ingest failed; the email will be stored in full");
                return null;
            }
        }

        private LeanIngestPlan? Fail(string reason)
        {
            _logger.LogWarning("Lean ingest not possible, the email is stored in full: {Reason}", reason);
            return null;
        }
    }
}
