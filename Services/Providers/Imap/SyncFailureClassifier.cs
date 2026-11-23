using MailArchiver.Services.Shared;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Providers.Imap
{
    /// <summary>
    /// Audit finding 3: only a failure that belongs to the MESSAGE counts towards the 3-strikes quarantine (it could not be parsed, the
    /// server refuses it on every attempt, it contains bytes the database rejects). Everything that belongs to the environment must not:
    /// a lost connection, Gmail's own rate and bandwidth limits, a cancel or timeout, a refused or failed store (an unwritable originals
    /// folder, a database hiccup), a failed deletion-marker check. Such a message is still a failure of the run (LastSync and the resume
    /// watermark stay where they are, so it is tried again), but it is never put aside.
    /// </summary>
    public static class SyncFailureClassifier
    {
        public static bool CountsTowardsQuarantine(Exception ex)
        {
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is OperationCanceledException or TimeoutException or ArchiveNotStoredException or DeletionMarkerCheckException
                    or ServiceNotConnectedException or ServiceNotAuthenticatedException or MailKit.Security.AuthenticationException
                    or MailKit.Security.SslHandshakeException or System.IO.IOException or System.Net.Sockets.SocketException)
                    return false;
            }
            if (ImapMailSyncService.IsConnectionLoss(ex) || ImapMailSyncService.IsTransientImapError(ex))
                return false;

            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is ImapCommandException ice && IsServerLimit(ice.Message + " " + ice.ResponseText))
                    return false;
            }

            // A database error is the environment's, except the one that IS about the message: bytes the database refuses (invalid UTF-8, NUL).
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            var isDb = false;
            for (var cur = ex; cur != null; cur = cur.InnerException)
                if (cur is DbUpdateException or Npgsql.NpgsqlException) { isDb = true; break; }
            if (isDb)
                return inner.Message.Contains("UTF8") || inner.Message.Contains("0x00") || inner.Message.Contains("22021");

            return true;
        }

        private static bool IsServerLimit(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var needle in new[] { "bandwidth", "exceeded", "too many", "[ALERT]", "[THROTTLED]", "[OVERQUOTA]", "[LIMIT]", "[UNAVAILABLE]", "quota", "temporarily", "rate limit", "try again", "throttl" })
                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
