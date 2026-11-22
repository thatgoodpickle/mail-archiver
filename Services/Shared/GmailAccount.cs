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
}
