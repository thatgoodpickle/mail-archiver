using MailArchiver.Services.Shared;

namespace MailArchiver.Models.ViewModels
{
    /// <summary>Everything the Integrity &amp; Storage page shows. A section that could not be loaded has an entry in <see cref="Errors"/> instead of failing the page.</summary>
    public class IntegrityStorageViewModel
    {
        public IntegrityRun? LastRun { get; set; }
        public string? AnchorLine { get; set; }
        public string? PublicKeyFingerprint { get; set; }
        public IReadOnlyList<ManifestInfo> Manifests { get; set; } = Array.Empty<ManifestInfo>();
        public OriginalBinStatus? BinStatus { get; set; }
        public IReadOnlyList<OriginalBinItem> BinItems { get; set; } = Array.Empty<OriginalBinItem>();
        public AttachmentTextStatus? TextStatus { get; set; }
        public IReadOnlyList<SyncProblemMessage> SyncProblems { get; set; } = Array.Empty<SyncProblemMessage>();
        public int QuarantinedCount { get; set; }
        public Dictionary<int, string> AccountNames { get; set; } = new();
        public IReadOnlyList<SettingView> Settings { get; set; } = Array.Empty<SettingView>();
        public Dictionary<string, string> Errors { get; } = new();

        /// <summary>Whether the admin has a local password. False for single sign-on, where risky changes cannot be made on this page.</summary>
        public bool HasLocalPassword { get; set; } = true;

        // The page shown again after a save that did not go through: what was typed, a message per bad row, and why.
        public Dictionary<string, string> SettingErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PostedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PostedResets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? SaveMessage { get; set; }
        /// <summary>Set when the password was missing or wrong: the page opens the confirmation again with this message.</summary>
        public string? PasswordProblem { get; set; }
    }
}
