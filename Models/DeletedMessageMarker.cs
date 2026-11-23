namespace MailArchiver.Models
{
    /// <summary>
    /// Delete memory (doc/DELETE-MEMORY-SPEC.md): what the archive keeps of an email that was deleted ON PURPOSE (reason user or
    /// retention), so a later sync does not archive it again from the same account. Written by the database (the email.deleted
    /// trigger) in the same transaction as the delete and its chain entry; nothing but identifiers is kept: NO subject, sender or
    /// body. Markers belong to the account (FK, ON DELETE CASCADE): deleting an account is a full reset.
    /// </summary>
    public class DeletedMessageMarker
    {
        public int Id { get; set; }
        public int AccountId { get; set; }

        /// <summary>The folder the email was archived in (outside Gmail it is part of the key; Gmail matches by <see cref="GmailMessageId"/>).</summary>
        public string Folder { get; set; } = string.Empty;

        public string MessageId { get; set; } = string.Empty;

        /// <summary>Size of the stored original in bytes; 0 when the original was not stored (then only <see cref="GmailMessageId"/> can match).</summary>
        public long Size { get; set; }

        /// <summary>SHA-256 of the stored original, when there was one: the backstop after a download.</summary>
        public string? Sha256 { get; set; }

        /// <summary>Gmail's X-GM-MSGID of the message, when it was recorded.</summary>
        public string? GmailMessageId { get; set; }

        public DateTime DeletedAt { get; set; }

        /// <summary>Sequence number of the email.deleted entry in the audit chain that records this deletion.</summary>
        public long ChainSeq { get; set; }

        /// <summary>"user" or "retention".</summary>
        public string Reason { get; set; } = string.Empty;
    }
}
