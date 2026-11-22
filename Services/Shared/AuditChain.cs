using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    /// <summary>The hash of one chain entry. Kept in one place so writer and verifier cannot drift.</summary>
    public static class AuditChainHash
    {
        /// <summary>PostgreSQL keeps microseconds, so the timestamp is cut to them before it is hashed and stored.</summary>
        public static DateTime TruncateToMicroseconds(DateTime t) => new(t.Ticks - t.Ticks % 10, DateTimeKind.Unspecified);

        public static string Compute(AuditChainEntry e)
        {
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{e.Seq}|{e.Timestamp:yyyy-MM-ddTHH:mm:ss.ffffff}|{e.Kind}|{e.EmailId}|{e.Sha256}|{e.PrevHash}|{e.Payload}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }
    }

    public interface IAuditChain
    {
        /// <summary>
        /// Appends one entry. Serialized across the whole application by a database lock, so the chain
        /// stays linear. Never throws: a failed append is logged at error level (an audit gap is
        /// reported by the verifier through the emails that have no entry), and returns null.
        /// </summary>
        Task<AuditChainEntry?> AppendAsync(string kind, int? emailId = null, string? sha256 = null, object? payload = null,
            CancellationToken cancellationToken = default);
    }

    public sealed class AuditChain : IAuditChain
    {
        private const long LockKey = 7283640101;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        private readonly IServiceScopeFactory _scopes;
        private readonly IOptions<IntegrityOptions> _options;
        private readonly ILogger<AuditChain> _logger;

        public AuditChain(IServiceScopeFactory scopes, IOptions<IntegrityOptions> options, ILogger<AuditChain> logger)
        {
            _scopes = scopes;
            _options = options;
            _logger = logger;
        }

        public async Task<AuditChainEntry?> AppendAsync(string kind, int? emailId = null, string? sha256 = null,
            object? payload = null, CancellationToken cancellationToken = default)
        {
            if (!_options.Value.AuditChainEnabled) return null;

            // F-21: the hash input is '|'-joined. Kind and Sha256 are code-controlled, so a '|' is a programming
            // error; refusing it here makes an ambiguous hash input unreachable (Payload is last and free text).
            if (kind.Contains('|') || (sha256 != null && sha256.Contains('|')))
                throw new ArgumentException("Audit chain kind and hash must not contain '|'.");

            try
            {
                // Own scope and context: callers may be inside a SaveChanges or a transaction of their own.
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                // F-20: never wait for the chain lock forever (a stuck session would stall every audited action).
                await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '10s'", cancellationToken);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);

                var last = await db.AuditChain.AsNoTracking().OrderByDescending(e => e.Seq).FirstOrDefaultAsync(cancellationToken);
                var entry = new AuditChainEntry
                {
                    Seq = (last?.Seq ?? 0) + 1,
                    Timestamp = AuditChainHash.TruncateToMicroseconds(DateTime.UtcNow),
                    Kind = kind,
                    EmailId = emailId,
                    Sha256 = sha256,
                    Payload = payload == null ? "{}" : JsonSerializer.Serialize(payload, Json),
                    PrevHash = last?.EntryHash ?? AuditChainEntry.GenesisHash
                };
                entry.EntryHash = AuditChainHash.Compute(entry);

                db.AuditChain.Add(entry);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return entry;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failedAppends);
                _logger.LogError(ex, "Audit chain append failed for '{Kind}' (email {EmailId}); the action itself was not blocked. " +
                    "The audit trail now has a gap for it (GET api/v1/integrity/chain/head shows the failure count).", kind, emailId);
                return null;
            }
        }

        private static long _failedAppends;

        /// <summary>Appends that failed since the application started (F-20): any value above 0 means audit entries are missing.</summary>
        public static long FailedAppends => Interlocked.Read(ref _failedAppends);
    }

    public sealed record AuditChainHead(long Seq, string Hash, DateTime? Timestamp);

    public sealed record AuditChainBreak(long Seq, string Reason);

    public sealed record AuditChainReport(
        bool Ok,
        long Entries,
        AuditChainHead? Head,
        AuditChainBreak? FirstBreak,
        IReadOnlyList<string> Warnings,
        long EmailsWithoutArchiveEntry,
        long EmailsDeletedWithRecord = 0);

    public interface IAuditChainVerifier
    {
        Task<AuditChainHead?> GetHeadAsync(CancellationToken cancellationToken = default);
        Task<AuditChainReport> VerifyAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Walks the chain in order and checks, for every entry: the sequence continues without a gap, the
    /// stored previous hash is the previous entry's hash, and the stored hash is what the entry's fields
    /// hash to. It also cross-checks that the hash recorded when an original was stored is still the one
    /// in its RawMessages row, and counts emails whose storage was never chained.
    /// </summary>
    public sealed class AuditChainVerifier : IAuditChainVerifier
    {
        private const int Batch = 1000;
        private readonly MailArchiverDbContext _db;

        public AuditChainVerifier(MailArchiverDbContext db) => _db = db;

        public async Task<AuditChainHead?> GetHeadAsync(CancellationToken cancellationToken = default)
        {
            var last = await _db.AuditChain.AsNoTracking().OrderByDescending(e => e.Seq).FirstOrDefaultAsync(cancellationToken);
            return last == null ? null : new AuditChainHead(last.Seq, last.EntryHash, last.Timestamp);
        }

        public async Task<AuditChainReport> VerifyAsync(CancellationToken cancellationToken = default)
        {
            long count = 0;
            long expectedSeq = 1;
            var prevHash = AuditChainEntry.GenesisHash;
            AuditChainBreak? broken = null;
            AuditChainHead? head = null;
            var warnings = new List<string>();
            long afterSeq = 0;

            while (broken == null)
            {
                var batch = await _db.AuditChain.AsNoTracking()
                    .Where(e => e.Seq > afterSeq).OrderBy(e => e.Seq).Take(Batch).ToListAsync(cancellationToken);
                if (batch.Count == 0) break;

                foreach (var e in batch)
                {
                    if (e.Seq != expectedSeq)
                    { broken = new AuditChainBreak(expectedSeq, $"Entry {expectedSeq} is missing (next entry is {e.Seq})."); break; }
                    if (!string.Equals(e.PrevHash, prevHash, StringComparison.Ordinal))
                    { broken = new AuditChainBreak(e.Seq, "Previous hash does not match the earlier entry."); break; }
                    if (!string.Equals(e.EntryHash, AuditChainHash.Compute(e), StringComparison.Ordinal))
                    { broken = new AuditChainBreak(e.Seq, "Entry content does not match its hash."); break; }

                    prevHash = e.EntryHash;
                    head = new AuditChainHead(e.Seq, e.EntryHash, e.Timestamp);
                    expectedSeq++;
                    count++;
                }
                afterSeq = batch[^1].Seq;
            }

            // Hashes recorded at storage time against the rows that hold them now.
            var changed = await _db.Database.SqlQueryRaw<int>(@"
                SELECT c.""EmailId"" AS ""Value"" FROM mail_archiver.""AuditChain"" c
                JOIN mail_archiver.""RawMessages"" r ON r.""ArchivedEmailId"" = c.""EmailId""
                WHERE c.""Kind"" = 'original.stored' AND c.""Sha256"" IS DISTINCT FROM r.""Sha256""
                LIMIT 20").ToListAsync(cancellationToken);
            foreach (var id in changed)
                warnings.Add($"Email {id}: the stored original's hash differs from the hash recorded when it was archived.");

            // F-05: an original that was stored and whose email is gone must be accounted for by an email.deleted entry.
            var vanished = await _db.Database.SqlQueryRaw<int>(@"
                SELECT DISTINCT c.""EmailId"" AS ""Value"" FROM mail_archiver.""AuditChain"" c
                WHERE c.""Kind"" = 'original.stored' AND c.""EmailId"" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM mail_archiver.""ArchivedEmails"" e WHERE e.""Id"" = c.""EmailId"")
                  AND NOT EXISTS (SELECT 1 FROM mail_archiver.""AuditChain"" d WHERE d.""Kind"" = 'email.deleted' AND d.""EmailId"" = c.""EmailId"")
                LIMIT 20").ToListAsync(cancellationToken);
            foreach (var id in vanished)
                warnings.Add($"Email {id} was removed without a deletion record in the chain.");

            // F-05: the email is there and the chain says an original was stored, but its record is gone.
            var rowGone = await _db.Database.SqlQueryRaw<int>(@"
                SELECT DISTINCT c.""EmailId"" AS ""Value"" FROM mail_archiver.""AuditChain"" c
                JOIN mail_archiver.""ArchivedEmails"" e ON e.""Id"" = c.""EmailId""
                WHERE c.""Kind"" = 'original.stored'
                  AND NOT EXISTS (SELECT 1 FROM mail_archiver.""RawMessages"" r WHERE r.""ArchivedEmailId"" = c.""EmailId"")
                LIMIT 20").ToListAsync(cancellationToken);
            foreach (var id in rowGone)
                warnings.Add($"Email {id}: the chain records an original stored, but its record in the database is gone.");

            var deletedCount = await _db.Database.SqlQueryRaw<long>(@"
                SELECT count(*) AS ""Value"" FROM mail_archiver.""AuditChain"" WHERE ""Kind"" = 'email.deleted'")
                .SingleAsync(cancellationToken);

            var unchained = await _db.Database.SqlQueryRaw<long>(@"
                SELECT count(*) AS ""Value"" FROM mail_archiver.""RawMessages"" r
                WHERE NOT EXISTS (SELECT 1 FROM mail_archiver.""AuditChain"" c
                                  WHERE c.""Kind"" = 'original.stored' AND c.""EmailId"" = r.""ArchivedEmailId"")")
                .SingleAsync(cancellationToken);

            return new AuditChainReport(broken == null && changed.Count == 0 && vanished.Count == 0 && rowGone.Count == 0,
                count, head, broken, warnings, unchained, deletedCount);
        }
    }

    /// <summary>
    /// Chains every original that gets stored, wherever it is stored from (IMAP, Graph, imports,
    /// storage migration): after a successful save, one "original.stored" entry per new RawMessages
    /// row with the email id and the SHA-256 of the original. Runs in its own database scope.
    /// </summary>
    public sealed class AuditChainInterceptor : SaveChangesInterceptor
    {
        private readonly IServiceProvider _services;
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, List<RawMessage>> _pending = new();
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContext, List<AccessLog>> _pendingAccess = new();

        // Resolved lazily: the interceptor is created while the DbContext options are built, and the
        // chain writer needs a DbContext of its own.
        public AuditChainInterceptor(IServiceProvider services) => _services = services;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var ctx = eventData.Context;
            if (ctx != null)
            {
                var added = ctx.ChangeTracker.Entries<RawMessage>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
                if (added.Count > 0) _pending.AddOrUpdate(ctx, added);

                // F-05: every access-log entry is chained, whichever code wrote it (several deletion and retention
                // paths add AccessLogs rows directly instead of going through AccessLogService).
                var access = ctx.ChangeTracker.Entries<AccessLog>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
                if (access.Count > 0) _pendingAccess.AddOrUpdate(ctx, access);
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            var ctx = eventData.Context;
            if (ctx != null && _pendingAccess.TryGetValue(ctx, out var accessRows))
            {
                _pendingAccess.Remove(ctx);
                var accessChain = _services.GetRequiredService<IAuditChain>();
                foreach (var log in accessRows)
                {
                    var detail = $"{log.EmailSubject}\n{log.EmailFrom}\n{log.SearchParameters}";
                    await accessChain.AppendAsync("access", log.EmailId, null, new
                    {
                        logId = log.Id,
                        user = log.Username,
                        type = log.Type.ToString(),
                        mailAccountId = log.MailAccountId,
                        at = log.Timestamp.ToString("O"),
                        detailSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(detail))).ToLowerInvariant()
                    }, cancellationToken);
                }
            }

            if (ctx != null && _pending.TryGetValue(ctx, out var rows))
            {
                _pending.Remove(ctx);
                var chain = _services.GetRequiredService<IAuditChain>();
                foreach (var row in rows)
                {
                    await chain.AppendAsync("original.stored", row.ArchivedEmailId, row.Sha256, new
                    {
                        source = row.Source.ToString(),
                        storage = row.StorageType,
                        size = row.Size
                    }, cancellationToken);
                }
            }
            return await base.SavedChangesAsync(eventData, result, cancellationToken);
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context != null) { _pending.Remove(eventData.Context); _pendingAccess.Remove(eventData.Context); }
            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }
    }
}
