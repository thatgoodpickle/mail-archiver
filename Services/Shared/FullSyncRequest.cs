using MailArchiver.Data;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// Audit finding 1: asking for a Full resync means "read this account's whole mailbox again". That includes dropping the
    /// sync checkpoints (the per-folder resume watermarks): a stale watermark would make the full search skip every message
    /// below it, and the run would then still end clean and move LastSync forward.
    ///
    /// The checkpoints are cleared where the resync is REQUESTED, not by ignoring them whenever a run is a full sync: a full
    /// sync that is paused by Gmail's bandwidth limit keeps LastSync at 1970 over many runs and has to resume from its
    /// checkpoints each time.
    /// </summary>
    internal static class FullSyncRequest
    {
        public static readonly DateTime EpochUtc = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Puts the account in the "full sync" state: clears its checkpoints (when asked) and sets LastSync to 1970. Saves.
        /// </summary>
        /// <param name="clearCheckpoints">
        /// True for a manual Full resync (the user asked to start over). For the automatic periodic full sync pass
        /// <see cref="ShouldClearForAutomatic"/>.
        /// </param>
        public static async Task RequestAsync(MailArchiverDbContext context, IBandwidthService bandwidth, int accountId, bool clearCheckpoints, CancellationToken ct = default)
        {
            if (clearCheckpoints)
                await bandwidth.ClearCheckpointsAsync(accountId);
            var account = await context.MailAccounts.FindAsync(new object[] { accountId }, ct);
            if (account == null) return;
            account.LastSync = EpochUtc;
            await context.SaveChangesAsync(ct);
        }

        /// <summary>
        /// The automatic periodic full sync starts over only when the account is not already in the middle of a full sync. LastSync at
        /// 1970 means an earlier full sync was paused (bandwidth limit, timeout, cancel) and is still resuming from its checkpoints:
        /// restarting it every interval would stop a multi-day full sync from ever finishing.
        /// </summary>
        public static bool ShouldClearForAutomatic(DateTime lastSync) => lastSync != EpochUtc;
    }
}
