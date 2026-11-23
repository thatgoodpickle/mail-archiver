namespace MailArchiver.Models
{
    /// <summary>
    /// A message that could not be archived (second review N-02). Failures are counted per message (account, folder,
    /// UIDVALIDITY, UID). After <see cref="SyncQuarantineOptions.QuarantineAfterFailures"/> failures in a row the message is
    /// QUARANTINED: the folder checkpoint and the account's LastSync may move past it, it is not fetched again on its own, and it
    /// is listed (with the reason) until an administrator retries it. An account with quarantined messages is never reported as
    /// completely archived.
    /// </summary>
    public class SyncProblemMessage
    {
        public const string Failing = "Failing";
        public const string Quarantined = "Quarantined";

        /// <summary>
        /// Audit finding 14: the folder's UIDVALIDITY changed, so this UID no longer names the message. The row is history only: it is not
        /// listed, not counted as quarantined and not used by the coverage check. The messages are read again by the Full resync the sync
        /// requests when it finds such rows.
        /// </summary>
        public const string Superseded = "Superseded";

        public int Id { get; set; }
        public int MailAccountId { get; set; }
        public string FolderName { get; set; } = string.Empty;
        public long UidValidity { get; set; }
        public long Uid { get; set; }
        public string? MessageId { get; set; }

        /// <summary>Gmail's X-GM-MSGID of the message when it was first recorded (coverage check: a quarantined message must match its server message). UidValidity above is the folder's UIDVALIDITY.</summary>
        public string? GmailMessageId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int FailCount { get; set; }
        public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = Failing;
        public DateTime? QuarantinedAt { get; set; }
    }

    /// <summary>Settings of the problem-message quarantine ("Sync:Quarantine").</summary>
    public class SyncQuarantineOptions
    {
        public const string Section = "Sync:Quarantine";

        /// <summary>Failures in a row after which a message is quarantined and the sync moves on. Minimum 1.</summary>
        public int QuarantineAfterFailures { get; set; } = 3;
    }
}
