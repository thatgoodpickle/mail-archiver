using MailArchiver.Models;

namespace MailArchiver.Services.Shared
{
    /// <summary>Gmail-specific behaviour is keyed on the IMAP server name (imap.gmail.com, imap.googlemail.com).</summary>
    public static class GmailAccount
    {
        public static bool Is(MailAccount account)
        {
            var host = account.ImapServer ?? string.Empty;
            return host.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("googlemail.com", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The pure decision behind the envelope/size pre-check of the IMAP sync (second review N-11), kept apart so it can be tested.</summary>
    public static class SyncPreCheck
    {
        /// <summary>
        /// True when a message with this Message-ID and size is already archived and need not be downloaded again: a held email has the same
        /// Message-ID and the same stored size, and (outside Gmail) lives in the same folder. A different message that reuses a Message-ID has a
        /// different size, so it is still downloaded and then judged by its hash.
        /// </summary>
        public static bool AlreadyArchived(IEnumerable<(string MessageId, string FolderName, long? Size)> held, string messageId, long size, string folderName, bool gmail)
            => held.Any(k => k.MessageId == messageId && k.Size == size && (gmail || k.FolderName == folderName));
    }
}
