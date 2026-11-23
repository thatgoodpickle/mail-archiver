using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    public sealed record SyncFailureResult(int FailCount, bool Quarantined);

    public interface ISyncProblemService
    {
        /// <summary>The problem messages of one folder, by UID (for the folder's current UIDVALIDITY only).</summary>
        Task<IReadOnlyDictionary<long, SyncProblemMessage>> LoadAsync(int accountId, string folderName, long uidValidity, CancellationToken cancellationToken = default);

        /// <summary>Counts one more failure of a message; quarantines it once the count reaches the limit.</summary>
        Task<SyncFailureResult> RecordFailureAsync(int accountId, string folderName, long uidValidity, long uid, string? messageId, string reason, CancellationToken cancellationToken = default, string? gmailMessageId = null);

        /// <summary>A message was archived after all: its problem record goes.</summary>
        Task ResolveAsync(int accountId, string folderName, long uidValidity, long uid, CancellationToken cancellationToken = default);

        /// <summary>Puts quarantined messages back into the normal flow (count reset), so the next sync tries them. Null id = all of the account (or all accounts).</summary>
        Task<int> RetryAsync(int? id, int? accountId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<SyncProblemMessage>> ListAsync(int limit, CancellationToken cancellationToken = default);
        Task<int> CountQuarantinedAsync(int? accountId = null, CancellationToken cancellationToken = default);
    }

    public sealed class SyncProblemService : ISyncProblemService
    {
        private readonly MailArchiverDbContext _db;
        private readonly IOptionsMonitor<SyncQuarantineOptions> _options;

        public SyncProblemService(MailArchiverDbContext db, IOptionsMonitor<SyncQuarantineOptions> options)
        {
            _db = db; _options = options;
        }

        private int Limit => Math.Max(1, _options.CurrentValue.QuarantineAfterFailures);

        private static string Clip(string? s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);

        public async Task<IReadOnlyDictionary<long, SyncProblemMessage>> LoadAsync(int accountId, string folderName, long uidValidity, CancellationToken ct = default)
            => (await _db.SyncProblemMessages.AsNoTracking()
                .Where(p => p.MailAccountId == accountId && p.FolderName == folderName && p.UidValidity == uidValidity)
                .ToListAsync(ct)).ToDictionary(p => p.Uid);

        public async Task<SyncFailureResult> RecordFailureAsync(int accountId, string folderName, long uidValidity, long uid, string? messageId, string reason, CancellationToken ct = default, string? gmailMessageId = null)
        {
            var now = DateTime.UtcNow;
            var row = await _db.SyncProblemMessages.FirstOrDefaultAsync(p =>
                p.MailAccountId == accountId && p.FolderName == folderName && p.UidValidity == uidValidity && p.Uid == uid, ct);
            if (row == null)
            {
                row = new SyncProblemMessage { MailAccountId = accountId, FolderName = folderName, UidValidity = uidValidity, Uid = uid, FirstSeen = now };
                _db.SyncProblemMessages.Add(row);
            }
            row.FailCount++;
            row.LastSeen = now;
            row.Reason = Clip(reason, 2000);
            if (!string.IsNullOrEmpty(gmailMessageId)) row.GmailMessageId = Clip(gmailMessageId, 40);
            if (!string.IsNullOrEmpty(messageId) && messageId != "Unknown" && messageId != "No Message-ID") row.MessageId = Clip(messageId, 500);
            if (row.FailCount >= Limit && row.Status != SyncProblemMessage.Quarantined)
            {
                row.Status = SyncProblemMessage.Quarantined;
                row.QuarantinedAt = now;
            }
            await _db.SaveChangesAsync(ct);
            return new SyncFailureResult(row.FailCount, row.Status == SyncProblemMessage.Quarantined);
        }

        public async Task ResolveAsync(int accountId, string folderName, long uidValidity, long uid, CancellationToken ct = default)
            => await _db.SyncProblemMessages.Where(p => p.MailAccountId == accountId && p.FolderName == folderName && p.UidValidity == uidValidity && p.Uid == uid).ExecuteDeleteAsync(ct);

        public async Task<int> RetryAsync(int? id, int? accountId, CancellationToken ct = default)
        {
            var q = _db.SyncProblemMessages.Where(p => p.Status == SyncProblemMessage.Quarantined);
            if (id != null) q = q.Where(p => p.Id == id);
            if (accountId != null) q = q.Where(p => p.MailAccountId == accountId);
            return await q.ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Status, SyncProblemMessage.Failing)
                .SetProperty(p => p.FailCount, 0)
                .SetProperty(p => p.QuarantinedAt, (DateTime?)null), ct);
        }

        public async Task<IReadOnlyList<SyncProblemMessage>> ListAsync(int limit, CancellationToken ct = default)
            => await _db.SyncProblemMessages.AsNoTracking().OrderByDescending(p => p.Status == SyncProblemMessage.Quarantined).ThenByDescending(p => p.LastSeen)
                .Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);

        public async Task<int> CountQuarantinedAsync(int? accountId = null, CancellationToken ct = default)
            => await _db.SyncProblemMessages.AsNoTracking().CountAsync(p => p.Status == SyncProblemMessage.Quarantined && (accountId == null || p.MailAccountId == accountId), ct);
    }
}
