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

        public async Task<int> ApplyAsync(int accountId, string? messageId, GmailMeta meta, CancellationToken ct = default)
        {
            var gid = meta.MessageId;
            if (string.IsNullOrEmpty(gid) && string.IsNullOrEmpty(messageId)) return 0;

            // Audit finding 8: a row is matched by its Gmail id when the server message has one. The Message-ID is only the way to find a row
            // that does not hold a Gmail id yet, and only when exactly one such row exists: two Gmail messages can share a Message-ID, and
            // matching by it would write one message's labels and Gmail id onto the other.
            var hasGid = !string.IsNullOrEmpty(gid);
            var query = _db.ArchivedEmails.AsNoTracking().Where(e => e.MailAccountId == accountId);
            var rows = hasGid
                ? await query.Where(e => e.GmailMessageId == gid)
                    .Select(e => new { e.Id, e.Labels, e.GmailMessageId, e.GmailThreadId, e.IsOutgoing, e.IsLocked }).ToListAsync(ct)
                : new();
            if (hasGid && rows.Count == 0 && messageId != null)
            {
                var candidates = await query.Where(e => e.GmailMessageId == null && e.MessageId == messageId)
                    .Select(e => new { e.Id, e.Labels, e.GmailMessageId, e.GmailThreadId, e.IsOutgoing, e.IsLocked }).ToListAsync(ct);
                if (candidates.Count == 1) rows = candidates;
            }
            else if (!hasGid && messageId != null)
            {
                // No Gmail id from the server: labels can still be matched by Message-ID when it is unambiguous; ids are never overwritten.
                var candidates = await query.Where(e => e.MessageId == messageId)
                    .Select(e => new { e.Id, e.Labels, e.GmailMessageId, e.GmailThreadId, e.IsOutgoing, e.IsLocked }).ToListAsync(ct);
                if (candidates.Count == 1) rows = candidates;
            }

            var changed = 0;
            var labels = meta.Labels.ToList();
            var sent = GmailLabels.IsSent(labels);
            var now = AuditChainHash.TruncateToMicroseconds(DateTime.UtcNow);
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
                }
                // A message first seen in All Mail looks incoming; Gmail's Sent label says otherwise. Only ever added, never for a locked email.
                if (sent && !r.IsOutgoing && !r.IsLocked)
                    await _db.ArchivedEmails.Where(e => e.Id == r.Id && !e.IsLocked).ExecuteUpdateAsync(u => u.SetProperty(e => e.IsOutgoing, true), ct);
            }
            return changed;
        }
    }
}
