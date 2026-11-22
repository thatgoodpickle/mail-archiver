using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Shared
{
    /// <summary>Result of comparing the database's archive id with the marker file in the originals folder.</summary>
    public sealed record ArchiveIdentityCheck(bool Ok, string Code, string Message, string? DatabaseId, string? MarkerId)
    {
        public const string Matches = "Matches";
        public const string Created = "Created";
        public const string NotAdopted = "NotAdopted";
        public const string FilesWithoutDatabase = "FilesWithoutDatabase";
        public const string DatabaseDiffers = "DatabaseDiffers";
        public const string MarkerMissing = "MarkerMissing";
        public const string Mismatch = "Mismatch";
    }

    public interface IArchiveIdentityService
    {
        /// <summary>
        /// Compares the database's archive id with the marker file <c>.archive-id</c> in the originals folder. A fresh archive
        /// (no stored originals and no files) gets both created. Anything unexplained is NOT ok, and the Trash does not run.
        /// </summary>
        Task<ArchiveIdentityCheck> CheckAsync(string baseFolder, CancellationToken cancellationToken = default);

        /// <summary>
        /// The explicit admin step for a legitimate situation the check refuses (upgrade of an existing archive, restored or
        /// moved volume): declares THIS database to be the owner of the originals in the folder. Creates the database id if there
        /// is none and writes the marker file. Returns the check after adopting.
        /// </summary>
        Task<ArchiveIdentityCheck> AdoptAsync(string baseFolder, CancellationToken cancellationToken = default);
    }

    public sealed class ArchiveIdentityService : IArchiveIdentityService
    {
        public const string MarkerFile = ".archive-id";
        private readonly MailArchiverDbContext _db;

        public ArchiveIdentityService(MailArchiverDbContext db) => _db = db;

        private static string? ReadMarker(string baseFolder)
        {
            try
            {
                var p = Path.Combine(baseFolder, MarkerFile);
                return File.Exists(p) ? File.ReadAllText(p).Trim() is { Length: > 0 } t ? t : null : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        private static void WriteMarker(string baseFolder, string id)
        {
            Directory.CreateDirectory(baseFolder);
            var p = Path.Combine(baseFolder, MarkerFile);
            var tmp = p + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, id + "\n");
            File.Move(tmp, p, overwrite: true);
        }

        /// <summary>True when at least one live hash-named original exists (the bin and temp files do not count).</summary>
        private static bool HasLiveFiles(string baseFolder)
        {
            if (!Directory.Exists(baseFolder)) return false;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
            foreach (var f in Directory.EnumerateFiles(baseFolder, "*.eml.gz", options))
            {
                var rel = Path.GetRelativePath(baseFolder, f).Replace('\\', '/');
                if (rel.StartsWith(OriginalCleanupOptions.BinFolder + "/", StringComparison.Ordinal)) continue;
                if (OriginalBin.TryParseLive(rel, out _)) return true;
            }
            return false;
        }

        private async Task<string?> DatabaseIdAsync(CancellationToken ct)
            => await _db.ArchiveIdentities.AsNoTracking().Select(i => i.ArchiveId).FirstOrDefaultAsync(ct);

        private async Task<string> CreateDatabaseIdAsync(CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("D");
            // Two starts at once: the second insert fails on the key; read what won.
            try
            {
                _db.ArchiveIdentities.Add(new ArchiveIdentity { Id = 1, ArchiveId = id, CreatedAt = DateTime.UtcNow });
                await _db.SaveChangesAsync(ct);
                return id;
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                return await DatabaseIdAsync(ct) ?? throw new InvalidOperationException("Could not create the archive identity.");
            }
        }

        public async Task<ArchiveIdentityCheck> CheckAsync(string baseFolder, CancellationToken ct = default)
        {
            var dbId = await DatabaseIdAsync(ct);
            var marker = ReadMarker(baseFolder);
            var hasRows = await _db.RawMessages.AsNoTracking().AnyAsync(ct);
            var hasFiles = HasLiveFiles(baseFolder);

            if (dbId == null && marker == null)
            {
                if (!hasRows && !hasFiles)
                {
                    var id = await CreateDatabaseIdAsync(ct);
                    WriteMarker(baseFolder, id);
                    return new ArchiveIdentityCheck(true, ArchiveIdentityCheck.Created, "A new archive identity was created.", id, id);
                }
                if (hasRows)
                    return new ArchiveIdentityCheck(false, ArchiveIdentityCheck.NotAdopted,
                        "This archive has stored originals but no identity yet (an upgrade, or a restore). Check that this is the right database and the right originals folder, then choose \"This is the right archive\" on the Integrity & Storage page. Until then the Trash does not run.", null, null);
                return new ArchiveIdentityCheck(false, ArchiveIdentityCheck.FilesWithoutDatabase,
                    "The originals folder holds files but the database has no stored originals. This may be an empty or wrong database. The Trash does not run.", null, null);
            }
            if (dbId == null)
                return new ArchiveIdentityCheck(false, ArchiveIdentityCheck.DatabaseDiffers,
                    "The originals folder belongs to an archive with a different identity than this database (this database has none). It may be a new, empty or restored database. The Trash does not run.", null, marker);
            if (marker == null)
            {
                if (!hasFiles)
                {
                    WriteMarker(baseFolder, dbId);
                    return new ArchiveIdentityCheck(true, ArchiveIdentityCheck.Created, "The marker file was created in the empty originals folder.", dbId, dbId);
                }
                return new ArchiveIdentityCheck(false, ArchiveIdentityCheck.MarkerMissing,
                    "The originals folder has files but no marker file (.archive-id). The folder may have been replaced or restored. The Trash does not run until an administrator confirms it is the right folder.", dbId, null);
            }
            if (!string.Equals(dbId, marker, StringComparison.Ordinal))
                return new ArchiveIdentityCheck(false, ArchiveIdentityCheck.Mismatch,
                    "The originals folder belongs to a different archive than this database (their identities differ). The database may be wrong or older than the files. The Trash does not run.", dbId, marker);
            return new ArchiveIdentityCheck(true, ArchiveIdentityCheck.Matches, "The database and the originals folder belong together.", dbId, marker);
        }

        public async Task<ArchiveIdentityCheck> AdoptAsync(string baseFolder, CancellationToken ct = default)
        {
            var id = await DatabaseIdAsync(ct) ?? await CreateDatabaseIdAsync(ct);
            WriteMarker(baseFolder, id);
            return await CheckAsync(baseFolder, ct);
        }
    }
}
