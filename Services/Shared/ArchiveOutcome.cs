namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// What happened to one email handed to an archive path (fork, review finding F-01). The old bool
    /// could not tell "already archived" from "refused" or "failed to save", so a refused email was
    /// counted as processed and the sync moved past it for good.
    /// </summary>
    public enum ArchiveOutcome
    {
        /// <summary>Stored as a new email.</summary>
        Archived,
        /// <summary>Already in the archive; nothing to do.</summary>
        Duplicate,
        /// <summary>Not stored because RawMessages:Required found no verified original to keep.</summary>
        Refused,
        /// <summary>Not stored because saving failed.</summary>
        Failed
    }

    /// <summary>
    /// Thrown by a sync loop when an email was Refused or Failed, so the loop's existing failure handling
    /// runs: the email is counted in FailedEmails, the folder watermark and LastSync stay where they are,
    /// and the email is read again on the next run.
    /// </summary>
    public sealed class ArchiveNotStoredException : Exception
    {
        public ArchiveOutcome Outcome { get; }

        public ArchiveNotStoredException(ArchiveOutcome outcome)
            : base(outcome == ArchiveOutcome.Refused
                ? "The email was not archived: RawMessages:Required is set and no verified original could be kept. It is read again on the next sync."
                : "The email was not archived because saving it failed. It is read again on the next sync.")
        {
            Outcome = outcome;
        }
    }
}
