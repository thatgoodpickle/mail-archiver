namespace MailArchiver.Models
{
    /// <summary>
    /// "RawMessages:OriginalCleanup" (fork, review F-11): a recycle bin for hash-named originals.
    /// <para>
    /// Hash-named originals are shared by byte-identical emails and are not deleted together with an email.
    /// When the collector finds one that NO RawMessages row refers to, it MOVES it into a bin folder
    /// (<c>.bin/</c> under the originals folder, same layout). It stays there for
    /// <see cref="BinRetentionDays"/> days and can be restored; only then is it deleted for good. Binned files
    /// that a row refers to again are moved back. Off by default.
    /// </para>
    /// <para>Honest limit: enabling this means deleted emails are really erased after the retention period, and
    /// from then on the original can only be recovered from a backup.</para>
    /// </summary>
    public class OriginalCleanupOptions
    {
        public const string BinFolder = ".bin";

        /// <summary>
        /// ON by default (switch off with RawMessages__OriginalCleanup__Enabled=false). It acts only on hash-named
        /// originals, so it does nothing for an archive that does not use filesystem storage with HashNamed (lean mode does).
        /// Files that are already unreferenced when it first runs are moved into the bin (reversible), never deleted
        /// outright, and nothing is purged before BinRetentionDays after it was binned.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Days an original stays in the bin before it is deleted permanently. Minimum 1.</summary>
        public int BinRetentionDays { get; set; } = 30;

        /// <summary>Hours between runs. Default 1, so a deleted email's original reaches the bin soon. Minimum 1.</summary>
        public int IntervalHours { get; set; } = 1;

        /// <summary>Upper bound of files moved into the bin, and (separately) of files purged, per run. Minimum 1.</summary>
        public int MaxFilesPerRun { get; set; } = 1000;

        /// <summary>Delay before the first scheduled run, in minutes, so startup settles first.</summary>
        public int StartupDelayMinutes { get; set; } = 15;

        /// <summary>
        /// Whether the collector can act at all: originals on the filesystem, hash-named. Not a startup error when it
        /// is false (the setting is on by default), the collector then stays idle and says why.
        /// </summary>
        public static bool IsApplicable(RawMessageOptions raw, out string reason)
        {
            reason = string.Empty;
            if (!raw.Enabled) reason = "RawMessages:Enabled is false (no originals are stored).";
            else if (!raw.WritesToFileSystem) reason = "originals are not stored on the filesystem (RawMessages:Storage).";
            else if (!raw.FileSystem.HashNamed) reason = "RawMessages:FileSystem:HashNamed is false (it only ever moves hash-named files).";
            return reason.Length == 0;
        }

        /// <summary>Startup problems with the numeric settings; empty when valid. Only checked while enabled.</summary>
        public static IReadOnlyList<string> Validate(RawMessageOptions raw)
        {
            var p = new List<string>();
            var o = raw.OriginalCleanup;
            if (!o.Enabled) return p;
            if (o.BinRetentionDays < 1) p.Add("RawMessages:OriginalCleanup:BinRetentionDays must be at least 1.");
            if (o.IntervalHours < 1) p.Add("RawMessages:OriginalCleanup:IntervalHours must be at least 1.");
            if (o.MaxFilesPerRun < 1) p.Add("RawMessages:OriginalCleanup:MaxFilesPerRun must be at least 1.");
            return p;
        }
    }

    /// <summary>An original that sits in the bin. <see cref="StoragePath"/> is where it lives when it is live; <see cref="BinPath"/> where it is now. Both are relative to the originals folder, forward slashes.</summary>
    public class OriginalBinEntry
    {
        public string StoragePath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string BinPath { get; set; } = string.Empty;
        public DateTime BinnedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Size of the stored (compressed) file in bytes.</summary>
        public long Size { get; set; }
    }

    /// <summary>One pass of the collector.</summary>
    public class OriginalCleanupRun
    {
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string Failed = "Failed";

        public int Id { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public string Status { get; set; } = Running;
        public int BinRetentionDays { get; set; }
        public int Scanned { get; set; }
        public int Referenced { get; set; }
        public int Binned { get; set; }
        public int Restored { get; set; }
        public int Purged { get; set; }
        public int Deferred { get; set; }
        public int Errors { get; set; }
        public string? StartedBy { get; set; }
        public string? Error { get; set; }
    }
}
