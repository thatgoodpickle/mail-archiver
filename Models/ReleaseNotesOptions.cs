namespace MailArchiver.Models
{
    /// <summary>
    /// Configuration options for the release notes / version update splash screen.
    /// Release notes and the update hint are read from the GitHub repository named by
    /// <see cref="Owner"/>/<see cref="Repository"/> (default: this fork).
    /// </summary>
    public class ReleaseNotesOptions
    {
        public const string ReleaseNotes = "ReleaseNotes";

        /// <summary>
        /// Enable or disable the version update splash screen.
        /// Default: true
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>GitHub owner whose releases are used. Default: thatgoodpickle (this fork).</summary>
        public string Owner { get; set; } = "thatgoodpickle";

        /// <summary>GitHub repository whose releases are used. Default: mail-archiver.</summary>
        public string Repository { get; set; } = "mail-archiver";

        /// <summary>
        /// HTTP request timeout in seconds for GitHub API calls.
        /// Default: 10
        /// </summary>
        public int RequestTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Application version string used to match against GitHub release tags.
        /// When empty, the version will be read from the compiled assembly.
        /// The update hint requires this value; without it no hint is shown.
        /// </summary>
        public string? AppVersion { get; set; }

        /// <summary>
        /// Show administrators a small hint when a newer stable release exists on GitHub.
        /// Default: true
        /// </summary>
        public bool UpdateHintEnabled { get; set; } = true;

        /// <summary>
        /// How often the GitHub release list is refreshed, in hours.
        /// Also controls how soon a new release becomes visible in a long-running instance.
        /// Default: 6
        /// </summary>
        public int UpdateCheckIntervalHours { get; set; } = 6;
    }
}