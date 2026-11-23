using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// When is a server message the same as one that was deleted on purpose (delete memory, doc/DELETE-MEMORY-SPEC.md)? Pure rules,
    /// kept apart from the sync so they can be tested without a mail server. Same Message-ID with ANOTHER size is not blocked (the same
    /// documented limit as the sync pre-check).
    /// </summary>
    public static class DeletionMarkerRules
    {
        /// <summary>
        /// Gmail: the X-GM-MSGID decides when both sides have one. Everything else (and a Gmail message or marker without an id):
        /// Message-ID + size, plus the folder outside Gmail (Gmail shows one message in many labels). A marker without a known size
        /// (the original was not stored) can only match by Gmail id.
        /// </summary>
        public static bool MatchesKey(DeletedMessageMarker marker, bool gmail, string? gmailId, string? messageId, long? size, string? folder)
        {
            if (gmail && !string.IsNullOrEmpty(gmailId) && !string.IsNullOrEmpty(marker.GmailMessageId))
                return string.Equals(marker.GmailMessageId, gmailId, StringComparison.Ordinal);

            if (marker.Size <= 0 || size is null || size <= 0 || string.IsNullOrEmpty(messageId)) return false;
            if (!string.Equals(marker.MessageId, messageId, StringComparison.Ordinal) || marker.Size != size) return false;
            return gmail || string.Equals(marker.Folder, folder ?? string.Empty, StringComparison.Ordinal);
        }

        public static bool MatchesHash(DeletedMessageMarker marker, string? sha256)
            => !string.IsNullOrEmpty(sha256) && !string.IsNullOrEmpty(marker.Sha256) && string.Equals(marker.Sha256, sha256, StringComparison.OrdinalIgnoreCase);

        public static DeletedMessageMarker? FirstMatch(IEnumerable<DeletedMessageMarker> markers, bool gmail, string? gmailId, string? messageId, long? size, string? folder)
            => markers.FirstOrDefault(m => MatchesKey(m, gmail, gmailId, messageId, size, folder));
    }

    /// <summary>What the server told about one message, as far as the marker rules need it.</summary>
    public sealed record MarkerProbe(uint Uid, string? GmailMessageId, string? MessageId, long? Size, DateTime Date);

    /// <summary>
    /// Check 1 (before the download) for one batch of server messages, FAIL CLOSED. It lives outside the sync's pre-check, whose try/catch
    /// fails open. If the marker table cannot be read, the server's description cannot be fetched, or a message of the batch was not
    /// described, <see cref="ThrowIfUnchecked"/> throws for the affected messages: the sync counts them as sync problems and never
    /// imports them.
    /// </summary>
    public sealed class MarkerBatchCheck
    {
        public static readonly MarkerBatchCheck None = new();

        /// <summary>The account has markers, so every message of the batch must have been checked.</summary>
        public bool Active { get; private set; }
        public Exception? Error { get; private set; }
        public HashSet<uint> Checked { get; } = new();
        public Dictionary<uint, (DeletedMessageMarker Marker, DateTime Date, string? MessageId)> Hits { get; } = new();

        /// <summary>
        /// Never throws (except for cancellation): any other failure is kept in <see cref="Error"/>. Does nothing (and describes nothing)
        /// when there is no marker service or the account has no markers.
        /// </summary>
        public static async Task<MarkerBatchCheck> RunAsync(IDeletionMarkerService? markers, int accountId, bool gmail, string folderName, int batchCount,
            Func<Task<IReadOnlyList<MarkerProbe>>> describeBatch, CancellationToken cancellationToken = default)
        {
            if (markers == null || batchCount == 0) return None;
            var result = new MarkerBatchCheck();
            try
            {
                if (await markers.CountAsync(accountId, cancellationToken) == 0) return None;
                result.Active = true;

                var probes = await describeBatch();
                var candidates = await markers.CandidatesAsync(accountId,
                    probes.Where(p => !string.IsNullOrEmpty(p.GmailMessageId)).Select(p => p.GmailMessageId!).ToList(),
                    probes.Where(p => !string.IsNullOrEmpty(p.MessageId)).Select(p => p.MessageId!).ToList(),
                    cancellationToken);

                foreach (var p in probes)
                {
                    result.Checked.Add(p.Uid);
                    var hit = DeletionMarkerRules.FirstMatch(candidates, gmail, p.GmailMessageId, p.MessageId, p.Size, folderName);
                    if (hit != null) result.Hits[p.Uid] = (hit, p.Date, p.MessageId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result.Error = ex;
                result.Hits.Clear();
            }
            return result;
        }

        /// <summary>Throws when this message could not be checked (fail closed).</summary>
        public void ThrowIfUnchecked(uint uid)
        {
            if (Error != null)
                throw new DeletionMarkerCheckException("The deletion-marker check could not be done, so the message was not imported: " + Error.Message, Error);
            if (Active && !Checked.Contains(uid))
                throw new DeletionMarkerCheckException("The server gave no description of this message for the deletion-marker check, so it was not imported.");
        }
    }

    /// <summary>Check 2 (after the download): the SHA-256 of the downloaded original against the markers. Never catches: a database error propagates.</summary>
    public static class DeletionMarkerHashCheck
    {
        public static Task<DeletedMessageMarker?> FindAsync(IDeletionMarkerService markers, int accountId, byte[] original, CancellationToken cancellationToken = default)
            => markers.FindByHashAsync(accountId, RawMessageCodec.ComputeSha256(original), cancellationToken);
    }

    /// <summary>
    /// The marker check could not be done. The sync treats it like any other failure of the message (counted as a sync problem, the
    /// message is NOT imported): a check that cannot be made never lets a message through.
    /// </summary>
    public sealed class DeletionMarkerCheckException : Exception
    {
        public DeletionMarkerCheckException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public interface IDeletionMarkerService
    {
        /// <summary>Number of markers of the account. Throws on a database error.</summary>
        Task<int> CountAsync(int accountId, CancellationToken cancellationToken = default);

        /// <summary>
        /// The markers that could match any of the given identifiers (a superset; the caller applies <see cref="DeletionMarkerRules"/>).
        /// Throws on a database error: callers must fail closed.
        /// </summary>
        Task<IReadOnlyList<DeletedMessageMarker>> CandidatesAsync(int accountId, IReadOnlyCollection<string> gmailIds, IReadOnlyCollection<string> messageIds,
            CancellationToken cancellationToken = default);

        /// <summary>The marker of an original with this SHA-256, if any. Throws on a database error: callers must fail closed.</summary>
        Task<DeletedMessageMarker?> FindByHashAsync(int accountId, string sha256, CancellationToken cancellationToken = default);
    }

    public sealed class DeletionMarkerService : IDeletionMarkerService
    {
        private readonly MailArchiverDbContext _db;
        public DeletionMarkerService(MailArchiverDbContext db) => _db = db;

        public Task<int> CountAsync(int accountId, CancellationToken ct = default)
            => _db.DeletedMessageMarkers.AsNoTracking().CountAsync(m => m.AccountId == accountId, ct);

        public async Task<IReadOnlyList<DeletedMessageMarker>> CandidatesAsync(int accountId, IReadOnlyCollection<string> gmailIds, IReadOnlyCollection<string> messageIds,
            CancellationToken ct = default)
        {
            var g = gmailIds.Distinct().ToList();
            var m = messageIds.Distinct().ToList();
            if (g.Count == 0 && m.Count == 0) return Array.Empty<DeletedMessageMarker>();
            return await _db.DeletedMessageMarkers.AsNoTracking()
                .Where(x => x.AccountId == accountId && ((x.GmailMessageId != null && g.Contains(x.GmailMessageId)) || m.Contains(x.MessageId)))
                .ToListAsync(ct);
        }

        public Task<DeletedMessageMarker?> FindByHashAsync(int accountId, string sha256, CancellationToken ct = default)
            => _db.DeletedMessageMarkers.AsNoTracking().FirstOrDefaultAsync(m => m.AccountId == accountId && m.Sha256 == sha256, ct);
    }
}
