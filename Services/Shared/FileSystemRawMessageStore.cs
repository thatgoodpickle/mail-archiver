using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// Stores original messages as gzip files next to the archive; the metadata row
    /// (checksum, size, capture source, relative path) stays in Postgres.
    /// </summary>
    public class FileSystemRawMessageStore : IRawMessageStore
    {
        private readonly MailArchiverDbContext _context;
        private readonly RawMessageOptions _options;
        private readonly RawMessagePathResolver _paths;
        private readonly ILogger<FileSystemRawMessageStore> _logger;

        public FileSystemRawMessageStore(
            MailArchiverDbContext context,
            IOptions<RawMessageOptions> options,
            RawMessagePathResolver paths,
            ILogger<FileSystemRawMessageStore> logger)
        {
            _context = context;
            _options = options.Value;
            _paths = paths;
            _logger = logger;
        }

        public string StorageType => RawMessageStorage.FileSystem;

        public RawMessage? CreateRow(int emailId, RawMessageCapture? capture)
        {
            if (_options.FileSystem.HashNamed && RawMessageCodec.ShouldStore(capture, _options))
            {
                var relative = _paths.GetRelativePathForHash(RawMessageCodec.ComputeSha256(capture!.Bytes));
                return RawMessageCodec.CreateFileSystemBacked(capture, _options, relative);
            }

            return RawMessageCodec.CreateFileSystemBacked(capture, _options, _paths.GetRelativePath(emailId));
        }

        /// <summary>
        /// Writes the message file. The row already carries the path, so a failure here leaves a
        /// row without a file; <see cref="ReadAsync"/> reports that as missing rather than
        /// serving something wrong.
        /// </summary>
        public async Task PersistAsync(RawMessage row, RawMessageCapture? capture)
        {
            if (!row.IsFileSystemBacked || capture == null || string.IsNullOrEmpty(row.StoragePath))
            {
                return;
            }

            var absolute = _paths.GetAbsolutePath(row.StoragePath);
            var directory = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // A hash-named file is written once. If it is already there (the same bytes arrived
            // before), it must hold exactly these bytes; it is verified, never overwritten.
            if (_options.FileSystem.HashNamed && File.Exists(absolute))
            {
                var existing = RawMessageCodec.Decompress(await File.ReadAllBytesAsync(absolute), row.Size);
                if (!string.Equals(RawMessageCodec.ComputeSha256(existing), row.Sha256, StringComparison.Ordinal))
                {
                    throw new IOException(
                        $"The existing original at {absolute} does not match its own name; refusing to overwrite it.");
                }

                // Fork (F-11): reused, so mark it as in use now; the original bin leaves recently written files alone.
                try { File.SetLastWriteTimeUtc(absolute, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* N-12: not our file (another user): the 24 h protection does not apply to it */ }
                return;
            }

            var compressed = RawMessageCodec.Compress(capture.Bytes);

            // Write to a temporary name and move into place, so an interrupted import cannot
            // leave a half-written file where a complete one is expected.
            // F-22: a temp name of its own, so two ingests of identical bytes (two accounts) cannot share one half-written file.
            var temp = $"{absolute}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, compressed);
            try
            {
                if (_options.FileSystem.HashNamed)
                {
                    try
                    {
                        File.Move(temp, absolute, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(absolute))
                    {
                        // The same bytes were written meanwhile: keep that file after checking it, drop ours.
                        File.Delete(temp);
                        var existing = RawMessageCodec.Decompress(await File.ReadAllBytesAsync(absolute), row.Size);
                        if (!string.Equals(RawMessageCodec.ComputeSha256(existing), row.Sha256, StringComparison.Ordinal))
                            throw new IOException("An existing original does not match its own name; refusing to use it.");
                        try { File.SetLastWriteTimeUtc(absolute, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* N-12: not our file (another user): the 24 h protection does not apply to it */ }   // F-11: in use now
                    }
                }
                else
                {
                    File.Move(temp, absolute, overwrite: true);
                }
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
        }

        /// <summary>
        /// Reads back the file a row points at, straight from disk, and returns the uncompressed
        /// bytes only when their SHA-256 equals the row's. Needs no database row, so it can prove a
        /// file was written correctly BEFORE the email row exists (lean ingest). Null when the file
        /// is missing or does not match.
        /// </summary>
        public async Task<byte[]?> ReadWrittenAsync(RawMessage row)
        {
            if (!row.IsFileSystemBacked || string.IsNullOrEmpty(row.StoragePath)) return null;

            var absolute = _paths.GetAbsolutePath(row.StoragePath);
            if (!File.Exists(absolute)) return null;

            var bytes = RawMessageCodec.Decompress(await File.ReadAllBytesAsync(absolute), row.Size);
            return string.Equals(RawMessageCodec.ComputeSha256(bytes), row.Sha256, StringComparison.Ordinal)
                ? bytes
                : null;
        }

        public async Task<bool> ExistsAsync(int emailId)
        {
            var row = await _context.RawMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ArchivedEmailId == emailId);

            if (row == null)
            {
                return false;
            }

            // A row whose file vanished is not a stored original; report it as absent so the
            // caller falls back to the rebuilt .eml instead of hitting a 500.
            return File.Exists(ResolvePath(row));
        }

        public async Task<RawMessageContent?> ReadAsync(int emailId)
        {
            var row = await _context.RawMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ArchivedEmailId == emailId);

            if (row == null)
            {
                return null;
            }

            var absolute = ResolvePath(row);
            if (!File.Exists(absolute))
            {
                _logger.LogWarning(
                    "Original message file for email {EmailId} is missing at {Path}", emailId, absolute);
                return null;
            }

            var compressed = await File.ReadAllBytesAsync(absolute);
            var bytes = RawMessageCodec.Decompress(compressed, row.Size);

            if (_options.FileSystem.VerifyChecksumOnRead)
            {
                var actual = RawMessageCodec.ComputeSha256(bytes);
                if (!string.Equals(actual, row.Sha256, StringComparison.Ordinal))
                {
                    _logger.LogError(
                        "Checksum mismatch for original message of email {EmailId} at {Path}: " +
                        "recorded {Expected}, file has {Actual}",
                        emailId, absolute, row.Sha256, actual);
                    throw new RawMessageIntegrityException(emailId, row.Sha256, actual);
                }
            }

            return new RawMessageContent(bytes, row.Sha256, row.Source, row.StorageType);
        }

        public async Task<string?> VerifyAsync(int emailId)
        {
            var row = await _context.RawMessages
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ArchivedEmailId == emailId);

            if (row == null)
            {
                return null;
            }

            var absolute = ResolvePath(row);
            if (!File.Exists(absolute))
            {
                return $"file missing at {absolute}";
            }

            var bytes = RawMessageCodec.Decompress(await File.ReadAllBytesAsync(absolute), row.Size);
            var actual = RawMessageCodec.ComputeSha256(bytes);

            return string.Equals(actual, row.Sha256, StringComparison.Ordinal)
                ? null
                : $"checksum mismatch (recorded {row.Sha256}, file has {actual})";
        }

        /// <summary>
        /// Absolute path of a row's file. Falls back to the path derived from the email id when
        /// the column is empty, which keeps a row readable after a manual database fix-up.
        /// </summary>
        private string ResolvePath(RawMessage row)
            => _paths.GetAbsolutePath(
                string.IsNullOrEmpty(row.StoragePath) ? _paths.GetRelativePath(row.ArchivedEmailId) : row.StoragePath);

        /// <summary>
        /// Deletes the message files of emails whose rows are already gone. The row is used when
        /// it still exists, otherwise the path is derived from the email id - which is exactly
        /// what <see cref="CreateRow"/> produced, so it finds every file this installation wrote.
        /// A file that cannot be removed is logged and left to the orphan collector.
        /// <para>
        /// Emails that still exist are skipped. That is what makes the call safe to place after
        /// a deletion that may have failed or rolled back: an original is never removed while
        /// its email is still in the archive.
        /// </para>
        /// </summary>
        public async Task DeleteAsync(IReadOnlyCollection<int> emailIds, CancellationToken cancellationToken = default)
        {
            if (emailIds.Count == 0)
            {
                return;
            }

            var ids = emailIds.ToList();

            // Only emails that are really gone; a failed or rolled-back deletion leaves its
            // email in place, and its original must survive with it.
            var stillPresent = await _context.ArchivedEmails
                .AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .Select(e => e.Id)
                .ToListAsync(cancellationToken);

            if (stillPresent.Count > 0)
            {
                ids = ids.Except(stillPresent).ToList();
            }

            if (ids.Count == 0)
            {
                return;
            }

            // A row that still exists wins over the derived path, because a migrated row can
            // carry a path that differs from the one the current shard depth produces. In the
            // normal delete flow the row is already gone: RawMessages cascades with
            // ArchivedEmails. This lookup therefore only helps for an orphaned metadata row,
            // and the derived path covers every file this installation wrote.
            var recorded = await _context.RawMessages
                .AsNoTracking()
                .Where(r => ids.Contains(r.ArchivedEmailId) && r.StorageType == RawMessageStorage.FileSystem)
                .Select(r => new { r.ArchivedEmailId, r.StoragePath })
                .ToListAsync(cancellationToken);

            var paths = new Dictionary<int, string>();
            foreach (var row in recorded)
            {
                paths[row.ArchivedEmailId] = _paths.GetAbsolutePath(
                    string.IsNullOrEmpty(row.StoragePath)
                        ? _paths.GetRelativePath(row.ArchivedEmailId)
                        : row.StoragePath);
            }

            foreach (var id in ids)
            {
                if (!paths.TryGetValue(id, out var absolute))
                {
                    absolute = _paths.GetAbsolutePath(_paths.GetRelativePath(id));
                }

                try
                {
                    if (File.Exists(absolute))
                    {
                        File.Delete(absolute);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex,
                        "Could not delete the original message file of email {EmailId} at {Path}; " +
                        "the orphan cleanup will retry", id, absolute);
                }
            }
        }
    }
}
