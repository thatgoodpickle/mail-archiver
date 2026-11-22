namespace MailArchiver.Models
{
    /// <summary>
    /// One admin-editable setting stored in the database (fork, "Integrity &amp; Storage" page). Only the settings in
    /// <see cref="MailArchiver.Services.Shared.SettingsCatalog"/> marked Editable are ever stored here; an environment
    /// variable for the same setting wins over the stored value (the page shows it as pinned).
    /// </summary>
    public class AppSetting
    {
        /// <summary>The configuration key, for example <c>RawMessages:OriginalCleanup:BinRetentionDays</c>.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>The value as text (invariant culture: numbers, true/false, or a fixed choice).</summary>
        public string Value { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>User name of the admin who last changed it.</summary>
        public string? UpdatedBy { get; set; }
    }
}
