using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// The values of the PostgreSQL setting <c>mailarchiver.delete_reason</c>, read by the BEFORE DELETE trigger on
    /// ArchivedEmails (delete memory, see doc/DELETE-MEMORY-SPEC.md). The trigger writes a deletion marker ONLY for
    /// <see cref="User"/> and <see cref="Retention"/>. Anything else (including no value at all) writes no marker,
    /// so a code path that forgets to say why makes its mail come back visibly instead of silently never returning.
    /// </summary>
    public static class DeleteReasons
    {
        /// <summary>A person (or an API/MCP client acting for one) deleted the email on purpose. Marker.</summary>
        public const string User = "user";
        /// <summary>The local retention policy removed the email. Marker.</summary>
        public const string Retention = "retention";
        /// <summary>The whole account is being deleted or cleared (full reset). No marker.</summary>
        public const string Account = "account";
        /// <summary>Rollback of a just-inserted email. NEVER a marker.</summary>
        public const string Internal = "internal";

        /// <summary>True for the reasons that leave a marker (the same rule as the trigger).</summary>
        public static bool WritesMarker(string? reason) => reason == User || reason == Retention;
    }

    public static class DeleteReasonScope
    {
        /// <summary>
        /// Sets <c>mailarchiver.delete_reason</c> with SET LOCAL semantics (<c>set_config(..., true)</c>): it lasts until the
        /// current transaction ends. Throws when there is no transaction, because outside one the value would silently
        /// vanish before the DELETE runs.
        /// </summary>
        public static async Task SetAsync(DbContext db, string reason, CancellationToken cancellationToken = default)
        {
            if (db.Database.CurrentTransaction == null)
                throw new InvalidOperationException("The delete reason can only be set inside a transaction (SET LOCAL).");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('mailarchiver.delete_reason', {reason}, true)", cancellationToken);
        }

        /// <summary>
        /// Runs <paramref name="action"/> (which does the delete and saves it) in ONE transaction together with the reason, so the
        /// trigger sees the reason in the same transaction as the delete, the marker and the chain entry. An already running
        /// transaction (tests, callers that own one) is reused and left to its owner; the reason is cleared again afterwards.
        /// </summary>
        public static async Task<T> RunAsync<T>(DbContext db, string reason, Func<Task<T>> action, CancellationToken cancellationToken = default)
        {
            var outer = db.Database.CurrentTransaction;
            if (outer != null)
            {
                // The caller owns the transaction. A savepoint keeps the "all or nothing" of the action: before this change one
                // SaveChanges was undone as a whole by EF's own savepoint when it failed; now the action may hold several.
                await outer.CreateSavepointAsync("delete_reason_scope", cancellationToken);
                await SetAsync(db, reason, cancellationToken);
                try
                {
                    var r = await action();
                    await outer.ReleaseSavepointAsync("delete_reason_scope", cancellationToken);
                    return r;
                }
                catch (Exception)
                {
                    try { await outer.RollbackToSavepointAsync("delete_reason_scope", CancellationToken.None); } catch (Exception) { }
                    throw;
                }
                finally
                {
                    // Best effort: when the action failed inside the transaction it may be aborted already.
                    try { await db.Database.ExecuteSqlRawAsync("SELECT set_config('mailarchiver.delete_reason', '', true)", CancellationToken.None); }
                    catch (Exception) { }
                }
            }

            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await SetAsync(db, reason, cancellationToken);
            var result = await action();
            await tx.CommitAsync(cancellationToken);
            return result;
        }

        public static Task RunAsync(DbContext db, string reason, Func<Task> action, CancellationToken cancellationToken = default)
            => RunAsync<int>(db, reason, async () => { await action(); return 0; }, cancellationToken);
    }
}
