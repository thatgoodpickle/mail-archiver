using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    /// <summary>What Gmail says about one message: its labels (system labels start with a backslash), its own message id and thread id.</summary>
    public sealed record GmailMeta(IReadOnlyList<string> Labels, string? MessageId, string? ThreadId);

    public static class GmailLabels
    {
        /// <summary>The Marketing label or any sub-label of it (Marketing/Anything), case-insensitive.</summary>
        public static bool IsMarketing(string? label)
            => label != null && (label.Equals("Marketing", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Marketing/", StringComparison.OrdinalIgnoreCase));

        public static bool IsMarketing(IEnumerable<string>? labels) => labels != null && labels.Any(IsMarketing);

        /// <summary>Gmail's Sent label: the message was sent from this mailbox.</summary>
        public static bool IsSent(IEnumerable<string>? labels) => labels != null && labels.Any(l => l.Equals("\\Sent", StringComparison.OrdinalIgnoreCase));

        public static bool SameSet(IReadOnlyCollection<string>? a, IReadOnlyCollection<string>? b)
            => (a ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual((b ?? Array.Empty<string>()).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public interface IGmailMetaService
    {
        /// <summary>
        /// Records what Gmail says about a message on the email(s) already archived for it (found by Gmail's message id, else by Message-ID).
        /// Returns how many emails changed. Locked emails keep their content: only the labels columns change (and Sent can only ever be added).
        /// </summary>
        Task<int> ApplyAsync(int accountId, string? messageId, GmailMeta meta, CancellationToken cancellationToken = default);

        /// <summary>The same for many messages at once, with a fixed number of queries per call (the daily label refresh).</summary>
        Task<int> ApplyBatchAsync(int accountId, IReadOnlyList<(string? MessageId, GmailMeta Meta)> items, CancellationToken cancellationToken = default);
    }

    public sealed class GmailMetaService : IGmailMetaService
    {
        private readonly MailArchiverDbContext _db;
        private readonly IAuditChain? _chain;
        private readonly IOptionsMonitor<IntegrityOptions> _options;

        public GmailMetaService(MailArchiverDbContext db, IOptionsMonitor<IntegrityOptions> options, IAuditChain? chain = null)
        {
            _db = db; _options = options; _chain = chain;
        }

        public Task<int> ApplyAsync(int accountId, string? messageId, GmailMeta meta, CancellationToken ct = default)
            => ApplyBatchAsync(accountId, new[] { (messageId, meta) }, ct);

        private sealed class HeldRow
        {
            public int Id; public string MessageId = string.Empty; public List<string>? Labels; public string? GmailMessageId; public string? GmailThreadId;
            public bool IsOutgoing; public bool IsLocked;
        }

        /// <summary>
        /// Audit finding 19: the matching rows of a whole batch are read with two queries (by Gmail id, by Message-ID) instead of one or two per
        /// message; the same decision as before is then made in memory. Only a row that really changes costs a write.
        /// </summary>
        public async Task<int> ApplyBatchAsync(int accountId, IReadOnlyList<(string? MessageId, GmailMeta Meta)> items, CancellationToken ct = default)
        {
            var todo = items.Where(i => !(string.IsNullOrEmpty(i.Meta.MessageId) && string.IsNullOrEmpty(i.MessageId))).ToList();
            if (todo.Count == 0) return 0;

            var gids = todo.Select(i => i.Meta.MessageId).Where(g => !string.IsNullOrEmpty(g)).Distinct().ToList();
            var mids = todo.Select(i => i.MessageId).Where(m => m != null).Distinct().ToList();
            IQueryable<HeldRow> Rows(IQueryable<ArchivedEmail> q) => q.Select(e => new HeldRow
            {
                Id = e.Id, MessageId = e.MessageId, Labels = e.Labels, GmailMessageId = e.GmailMessageId, GmailThreadId = e.GmailThreadId,
                IsOutgoing = e.IsOutgoing, IsLocked = e.IsLocked
            });
            var query = _db.ArchivedEmails.AsNoTracking().Where(e => e.MailAccountId == accountId);
            // One object per row, shared by both indexes, so an update seen through one is seen through the other.
            var known = new Dictionary<int, HeldRow>();
            HeldRow One(HeldRow r) => known.TryGetValue(r.Id, out var k) ? k : known[r.Id] = r;
            var byGid = new Dictionary<string, List<HeldRow>>();
            if (gids.Count > 0)
                foreach (var r in (await Rows(query.Where(e => gids.Contains(e.GmailMessageId))).ToListAsync(ct)).Select(One))
                    AddTo(byGid, r.GmailMessageId!, r);
            var byMid = new Dictionary<string, List<HeldRow>>();
            if (mids.Count > 0)
                foreach (var r in (await Rows(query.Where(e => mids.Contains(e.MessageId))).ToListAsync(ct)).Select(One))
                    AddTo(byMid, r.MessageId, r);

            var changed = 0;
            var now = AuditChainHash.TruncateToMicroseconds(DateTime.UtcNow);
            foreach (var (messageId, meta) in todo)
            {
                var gid = meta.MessageId;
                // Audit finding 8: a row is matched by its Gmail id when the server message has one. The Message-ID is only the way to find a row
                // that does not hold a Gmail id yet, and only when exactly one such row exists: two Gmail messages can share a Message-ID, and
                // matching by it would write one message's labels and Gmail id onto the other.
                var hasGid = !string.IsNullOrEmpty(gid);
                List<HeldRow> rows = new();
                if (hasGid) rows = byGid.TryGetValue(gid!, out var g) ? g.ToList() : new();
                if (hasGid && rows.Count == 0 && messageId != null)
                {
                    var candidates = byMid.TryGetValue(messageId, out var c) ? c.Where(r => r.GmailMessageId == null).ToList() : new();
                    if (candidates.Count == 1) rows = candidates;
                }
                else if (!hasGid && messageId != null)
                {
                    // No Gmail id from the server: labels can still be matched by Message-ID when it is unambiguous; ids are never overwritten.
                    var candidates = byMid.TryGetValue(messageId, out var c) ? c.ToList() : new();
                    if (candidates.Count == 1) rows = candidates;
                }

                var labels = meta.Labels.ToList();
                var sent = GmailLabels.IsSent(labels);
                foreach (var r in rows)
                {
                    var labelsChanged = !GmailLabels.SameSet(r.Labels, labels);
                    var idsChanged = hasGid && (r.GmailMessageId != meta.MessageId || r.GmailThreadId != meta.ThreadId);
                    if (labelsChanged || idsChanged)
                    {
                        if (hasGid)
                            await _db.ArchivedEmails.Where(e => e.Id == r.Id).ExecuteUpdateAsync(u => u
                                .SetProperty(e => e.Labels, labels)
                                .SetProperty(e => e.GmailMessageId, meta.MessageId)
                                .SetProperty(e => e.GmailThreadId, meta.ThreadId)
                                .SetProperty(e => e.LabelsCapturedAt, now), ct);
                        else
                            await _db.ArchivedEmails.Where(e => e.Id == r.Id).ExecuteUpdateAsync(u => u
                                .SetProperty(e => e.Labels, labels)
                                .SetProperty(e => e.LabelsCapturedAt, now), ct);
                        changed++;
                        if (labelsChanged && _chain != null && _options.CurrentValue.AuditChainEnabled && _options.CurrentValue.ChainLabelChanges)
                            await _chain.AppendAsync("email.labels.changed", r.Id, null, new { old = r.Labels ?? new List<string>(), @new = labels }, ct);
                        // Keep the in-memory copy current: a later item of the same batch must see what an earlier one wrote.
                        var hadGid = r.GmailMessageId != null;
                        r.Labels = labels;
                        if (hasGid)
                        {
                            r.GmailMessageId = meta.MessageId; r.GmailThreadId = meta.ThreadId;
                            if (!hadGid) AddTo(byGid, gid!, r);
                        }
                    }
                    // A message first seen in All Mail looks incoming; Gmail's Sent label says otherwise. Only ever added, never for a locked email.
                    if (sent && !r.IsOutgoing && !r.IsLocked)
                    {
                        await _db.ArchivedEmails.Where(e => e.Id == r.Id && !e.IsLocked).ExecuteUpdateAsync(u => u.SetProperty(e => e.IsOutgoing, true), ct);
                        r.IsOutgoing = true;
                    }
                }
            }
            return changed;
        }

        private static void AddTo(Dictionary<string, List<HeldRow>> map, string key, HeldRow row)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<HeldRow>();
            if (!list.Contains(row)) list.Add(row);
        }
    }
}
