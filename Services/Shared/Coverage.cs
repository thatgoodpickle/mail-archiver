using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Shared
{
    public enum CoverageBucket { Archived, DeletedByYou, Quarantined, FilteredOut, NotYetSeen }

    /// <summary>
    /// What a provider adapter reports for one message on the server (keys only, no body). Gmail: <see cref="GmailMessageId"/> (X-GM-MSGID);
    /// plain IMAP: <see cref="MessageId"/> + <see cref="Size"/>, with folder + UIDVALIDITY + UID as the tiebreak.
    /// </summary>
    public sealed record ServerMessageKey(string? GmailMessageId, string? MessageId, long? Size, string Folder, long UidValidity, long Uid);

    /// <summary>What the archive knows, indexed for the classification (built once per check from the database).</summary>
    public sealed class CoverageHeld
    {
        // archived
        public HashSet<string> ArchivedGmailIds { get; } = new();
        /// <summary>(Message-ID, size) of archived rows WITHOUT a Gmail id (archived before the labels feature): the Gmail fallback.</summary>
        public HashSet<(string, long)> ArchivedKeyNoGmailId { get; } = new();
        /// <summary>(Message-ID, size) of every archived row: used when the server gave no Gmail id.</summary>
        public HashSet<(string, long)> ArchivedKeyAny { get; } = new();
        /// <summary>(Message-ID, size, folder): plain IMAP.</summary>
        public HashSet<(string, long, string)> ArchivedKeyFolder { get; } = new();

        // deleted by you (markers)
        public HashSet<string> MarkerGmailIds { get; } = new();
        public HashSet<(string, long)> MarkerKeyNoGmailId { get; } = new();
        public HashSet<(string, long)> MarkerKeyAny { get; } = new();
        public HashSet<(string, long, string)> MarkerKeyFolder { get; } = new();

        // quarantined / failing
        public HashSet<string> ProblemGmailIds { get; } = new();
        public HashSet<(string, long, long)> ProblemLocations { get; } = new();   // folder, UIDVALIDITY, UID

        /// <summary>
        /// Gmail ids the account's search filter KEEPS (the result of the filter search, a SET). Null = no filter. The filter (default
        /// -label:Marketing) returns what is archived, so "filtered out" is every server message that is NOT in this set.
        /// </summary>
        public HashSet<string>? FilterKeepGmailIds { get; set; }

        public void AddArchived(string? gmailId, string? messageId, long? size, string? folder)
        {
            if (!string.IsNullOrEmpty(gmailId)) ArchivedGmailIds.Add(gmailId);
            if (!string.IsNullOrEmpty(messageId) && size is > 0)
            {
                ArchivedKeyAny.Add((messageId, size.Value));
                if (string.IsNullOrEmpty(gmailId)) ArchivedKeyNoGmailId.Add((messageId, size.Value));
                ArchivedKeyFolder.Add((messageId, size.Value, folder ?? string.Empty));
            }
        }

        public void AddMarker(DeletedMessageMarker m)
        {
            if (!string.IsNullOrEmpty(m.GmailMessageId)) MarkerGmailIds.Add(m.GmailMessageId);
            if (!string.IsNullOrEmpty(m.MessageId) && m.Size > 0)
            {
                MarkerKeyAny.Add((m.MessageId, m.Size));
                if (string.IsNullOrEmpty(m.GmailMessageId)) MarkerKeyNoGmailId.Add((m.MessageId, m.Size));
                MarkerKeyFolder.Add((m.MessageId, m.Size, m.Folder ?? string.Empty));
            }
        }

        public void AddProblem(SyncProblemMessage p)
        {
            if (!string.IsNullOrEmpty(p.GmailMessageId)) ProblemGmailIds.Add(p.GmailMessageId);
            ProblemLocations.Add((p.FolderName, p.UidValidity, p.Uid));
        }
    }

    /// <summary>
    /// The coverage check's classification: each server message lands in exactly ONE bucket by a fixed order, so it can never be counted
    /// twice (for example archived and later labelled Marketing): archived, then deleted by you, then quarantined, then filtered out,
    /// then not yet seen. Pure: no database, no server.
    /// </summary>
    public static class CoverageClassifier
    {
        public static CoverageBucket Classify(ServerMessageKey key, CoverageHeld held, bool gmail)
        {
            var hasGid = gmail && !string.IsNullOrEmpty(key.GmailMessageId);
            var idKey = !string.IsNullOrEmpty(key.MessageId) && key.Size is > 0 ? (key.MessageId!, key.Size!.Value) : default((string, long)?);

            // 1 archived
            if (hasGid ? held.ArchivedGmailIds.Contains(key.GmailMessageId!) || (idKey != null && held.ArchivedKeyNoGmailId.Contains(idKey.Value))
                       : idKey != null && (gmail ? held.ArchivedKeyAny.Contains(idKey.Value) : held.ArchivedKeyFolder.Contains((idKey.Value.Item1, idKey.Value.Item2, key.Folder))))
                return CoverageBucket.Archived;

            // 2 deleted by you (the same rules as the sync: DeletionMarkerRules)
            if (hasGid ? held.MarkerGmailIds.Contains(key.GmailMessageId!) || (idKey != null && held.MarkerKeyNoGmailId.Contains(idKey.Value))
                       : idKey != null && (gmail ? held.MarkerKeyAny.Contains(idKey.Value) : held.MarkerKeyFolder.Contains((idKey.Value.Item1, idKey.Value.Item2, key.Folder))))
                return CoverageBucket.DeletedByYou;

            // 3 quarantined (or failing): by Gmail id, else by folder + UIDVALIDITY + UID
            if ((hasGid && held.ProblemGmailIds.Contains(key.GmailMessageId!)) || held.ProblemLocations.Contains((key.Folder, key.UidValidity, key.Uid)))
                return CoverageBucket.Quarantined;

            // 4 filtered out: Gmail only, and only when there is a filter; not in the set of ids the filter keeps
            if (gmail && held.FilterKeepGmailIds != null && !(hasGid && held.FilterKeepGmailIds.Contains(key.GmailMessageId!)))
                return CoverageBucket.FilteredOut;

            return CoverageBucket.NotYetSeen;
        }
    }

    public sealed record CoverageNotSeen(ServerMessageKey Key, string? SearchLink);

    public sealed class CoverageResult
    {
        public bool Gmail { get; init; }
        public int ServerTotal { get; init; }
        public Dictionary<CoverageBucket, int> Buckets { get; init; } = new();
        public int Sum => Buckets.Values.Sum();
        /// <summary>The integrity line: the buckets add up to the server total (every message in exactly one bucket).</summary>
        public bool Balanced => Sum == ServerTotal;
        public IReadOnlyList<CoverageNotSeen> NotYetSeen { get; init; } = Array.Empty<CoverageNotSeen>();
        public int NotYetSeenListed => NotYetSeen.Count;
        /// <summary>Gmail: archived emails whose Gmail id the server no longer lists (the archive is now the only copy).</summary>
        public int ArchivedNoLongerOnServer { get; init; }
        /// <summary>Gmail: Gmail ids that more than one archived email carries (should be 0).</summary>
        public int ArchivedTwice { get; init; }
        public IReadOnlyList<string> FoldersRead { get; init; } = Array.Empty<string>();
        public string? FilterText { get; init; }
        public DateTime At { get; init; } = DateTime.UtcNow;
    }

    /// <summary>What a provider adapter returns: the keys of every server message in the synced folders, and (Gmail with a filter) the keep set.</summary>
    public sealed record ServerListing(IReadOnlyList<ServerMessageKey> Keys, HashSet<string>? FilterKeepGmailIds, IReadOnlyList<string> FoldersRead);

    /// <summary>Reads keys from a mail server. READ-ONLY: no flag, no move, no delete, no append, folders opened ReadOnly.</summary>
    public interface IServerKeySource
    {
        Task<ServerListing> ListAsync(MailAccount account, CancellationToken cancellationToken = default);
    }

    public interface ICoverageService
    {
        Task<CoverageResult> RunAsync(MailAccount account, int notSeenLimit = 200, CancellationToken cancellationToken = default);
    }

    public sealed class CoverageService : ICoverageService
    {
        private readonly MailArchiverDbContext _db;
        private readonly IServerKeySource _source;
        public CoverageService(MailArchiverDbContext db, IServerKeySource source) { _db = db; _source = source; }

        public async Task<CoverageResult> RunAsync(MailAccount account, int notSeenLimit = 200, CancellationToken ct = default)
        {
            var gmail = GmailAccount.Is(account);
            var listing = await _source.ListAsync(account, ct);

            var held = new CoverageHeld { FilterKeepGmailIds = listing.FilterKeepGmailIds };
            var archived = await _db.ArchivedEmails.AsNoTracking().Where(e => e.MailAccountId == account.Id)
                .Select(e => new { e.GmailMessageId, e.MessageId, e.FolderName, Size = e.RawMessage != null ? (long?)e.RawMessage.Size : null })
                .ToListAsync(ct);
            foreach (var a in archived) held.AddArchived(a.GmailMessageId, a.MessageId, a.Size, a.FolderName);
            foreach (var m in await _db.DeletedMessageMarkers.AsNoTracking().Where(m => m.AccountId == account.Id).ToListAsync(ct)) held.AddMarker(m);
            foreach (var p in await _db.SyncProblemMessages.AsNoTracking().Where(p => p.MailAccountId == account.Id && p.Status != SyncProblemMessage.Superseded).ToListAsync(ct)) held.AddProblem(p);

            var buckets = Enum.GetValues<CoverageBucket>().ToDictionary(b => b, _ => 0);
            var notSeen = new List<CoverageNotSeen>();
            foreach (var key in listing.Keys)
            {
                var b = CoverageClassifier.Classify(key, held, gmail);
                buckets[b]++;
                if (b == CoverageBucket.NotYetSeen && notSeen.Count < notSeenLimit) notSeen.Add(new CoverageNotSeen(key, SearchLink(account, key, gmail)));
            }

            var serverGids = listing.Keys.Where(k => !string.IsNullOrEmpty(k.GmailMessageId)).Select(k => k.GmailMessageId!).ToHashSet();
            var noLonger = gmail ? archived.Count(a => !string.IsNullOrEmpty(a.GmailMessageId) && !serverGids.Contains(a.GmailMessageId!)) : 0;
            var twice = gmail ? archived.Where(a => !string.IsNullOrEmpty(a.GmailMessageId)).GroupBy(a => a.GmailMessageId).Count(g => g.Count() > 1) : 0;

            return new CoverageResult
            {
                Gmail = gmail, ServerTotal = listing.Keys.Count, Buckets = buckets, NotYetSeen = notSeen,
                ArchivedNoLongerOnServer = noLonger, ArchivedTwice = twice, FoldersRead = listing.FoldersRead,
                FilterText = GmailAccount.EffectiveFilter(account)
            };
        }

        /// <summary>A link that finds the message on the server side: Gmail's search by Message-ID. Other servers have none.</summary>
        public static string? SearchLink(MailAccount account, ServerMessageKey key, bool gmail)
            => gmail && !string.IsNullOrEmpty(key.MessageId)
                ? "https://mail.google.com/mail/u/" + Uri.EscapeDataString(account.EmailAddress) + "/#search/" + Uri.EscapeDataString("rfc822msgid:" + key.MessageId!.Trim('<', '>'))
                : null;
    }
}
