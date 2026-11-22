using MailArchiver.Models;

namespace MailArchiver.Services.Shared
{
    /// <summary>Gmail-specific behaviour is keyed on the IMAP server name (imap.gmail.com, imap.googlemail.com).</summary>
    public static class GmailAccount
    {
        /// <summary>The filter a Gmail account gets when none was ever set: mail tagged Marketing (or any sub-label) is not archived.</summary>
        public const string DefaultFilter = "-label:Marketing";

        /// <summary>
        /// The Gmail search the sync adds to its folder searches, or null for no filter. Null setting = the default for Gmail accounts,
        /// empty = explicitly none; other servers never filter.
        /// </summary>
        public static string? EffectiveFilter(MailAccount account)
        {
            if (!Is(account)) return null;
            var f = account.GmailSearchFilter == null ? DefaultFilter : account.GmailSearchFilter.Trim();
            return f.Length == 0 ? null : f;
        }

        /// <summary>True when only the folder with all mail is synced (the default for a Gmail account); the labels carry what the folders would have.</summary>
        public static bool AllMailOnly(MailAccount account) => Is(account) && (account.GmailAllMailOnly ?? true);

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
