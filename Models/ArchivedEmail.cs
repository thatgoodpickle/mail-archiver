namespace MailArchiver.Models
{
    public class ArchivedEmail
    {
        public int Id { get; set; }
        public int MailAccountId { get; set; }
        public string MessageId { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string HtmlBody { get; set; }
        public string? BodyUntruncatedText { get; set; }
        public string? BodyUntruncatedHtml { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Cc { get; set; }
        public string Bcc { get; set; }

        // Original display names from the email headers (e.g. "Max Mustermann")
        // Stored as comma-separated list parallel to From/To/Cc/Bcc address fields.
        // null when no display names were present. Used for faithful restore/export only.
        public string? FromDisplayName { get; set; }
        public string? ToDisplayNames { get; set; }
        public string? CcDisplayNames { get; set; }
        public string? BccDisplayNames { get; set; }
        public DateTime SentDate { get; set; } = DateTime.UtcNow;
        public DateTime ReceivedDate { get; set; } = DateTime.UtcNow;
        public bool IsOutgoing { get; set; }
        public bool HasAttachments { get; set; }
        public string FolderName { get; set; }

        // Raw email headers as stored in the original email
        // Contains all headers including Received, Return-Path, X-Headers, etc.
        public string? RawHeaders { get; set; }

        // Original body content with null bytes preserved (stored as byte array)
        // Only populated when the original body contained null bytes that needed to be cleaned
        // for PostgreSQL TEXT storage. Used for faithful export/restore of emails.
        public byte[]? OriginalBodyText { get; set; }
        public byte[]? OriginalBodyHtml { get; set; }

        // Compliance fields for integrity and immutability
        public string? ContentHash { get; set; }
        public DateTime? HashCreatedAt { get; set; }
        public bool IsLocked { get; set; } = false;

        // Lean storage: true when the HTML body, the untruncated body variants and the attachment
        // bytes are NOT kept in the database and are read from the stored original instead.
        // Plain-text Body, headers and list fields always stay. See doc/INTEGRITY-DESIGN.md.
        public bool ContentStripped { get; set; } = false;

        // Fork (attachment text, see AttachmentTextProcessor): text read out of the attachments, kept only
        // so search can find words that exist only inside an attachment. Derived and rebuildable; it is
        // never shown, exported, compared by the integrity check or restored. AttachmentTextAt is set once
        // the email's attachments have all been read (also when they held no text).
        public string? AttachmentText { get; set; }
        public DateTime? AttachmentTextAt { get; set; }

        // Fork (Gmail labels): the labels Gmail had on this message at LabelsCapturedAt (system labels start with a backslash, for
        // example \Sent, \Important). This is Gmail's metadata ABOUT the message, not part of the stored original, so it is not
        // covered by the original's hash: it is recorded as "labels as of the last sync" and refreshed by later syncs.
        public List<string>? Labels { get; set; }
        public string? GmailMessageId { get; set; }
        public string? GmailThreadId { get; set; }
        public DateTime? LabelsCapturedAt { get; set; }

        public virtual MailAccount MailAccount { get; set; }
        public virtual ICollection<EmailAttachment> Attachments { get; set; } = new List<EmailAttachment>();

        // Original message bytes; only present when RawMessages:Enabled was set at archive time
        public virtual RawMessage? RawMessage { get; set; }
    }
}
