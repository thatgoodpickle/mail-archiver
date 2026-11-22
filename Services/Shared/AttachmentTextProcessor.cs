using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Shared
{
    public sealed record AttachmentTextStatus(
        string Mode,
        IReadOnlyList<AttachmentTextCount> Texts,
        long PendingAttachments,
        long EmailsAwaitingIndex);

    public sealed record AttachmentTextCount(string Status, string Method, long Count);

    public interface IAttachmentTextProcessor
    {
        /// <summary>One round: reads up to BatchSize attachments that have no text yet, then fills the search text of the emails that are complete. Returns the amount of work done (0 = idle).</summary>
        Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default);

        Task<AttachmentTextStatus> GetStatusAsync(CancellationToken cancellationToken = default);

        /// <summary>Forgets failed results (all = false) or every result (all = true) so they are read again. Returns the number of results removed.</summary>
        Task<int> RescanAsync(bool all, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Reads attachment text for search (fork, OCR step A). Queue keyed by the attachment's SHA-256 (lean rows
    /// record it, full-storage rows get it from AttachmentContents), so an attachment that sits in many emails is
    /// read once. Resumable by construction: the queue is "attachments without a result", nothing is kept in memory.
    /// Only the text layer is read here; scans and photos get the status NeedsOcr for a later OCR engine.
    /// </summary>
    public sealed class AttachmentTextProcessor : IAttachmentTextProcessor
    {
        private const string EffectiveSha = @"COALESCE(a.""Sha256"", c.""Hash"")";

        private readonly MailArchiverDbContext _db;
        private readonly IStrippedContentHydrator _hydrator;
        private readonly AttachmentTextOptions _options;
        private readonly ILogger<AttachmentTextProcessor> _logger;

        public AttachmentTextProcessor(MailArchiverDbContext db, IStrippedContentHydrator hydrator,
            IOptions<AttachmentTextOptions> options, ILogger<AttachmentTextProcessor> logger)
        {
            _db = db; _hydrator = hydrator; _options = options.Value; _logger = logger;
        }

        private int MaxChars => Math.Max(1, _options.MaxTextKb) * 1024;

        public sealed class PendingRow
        {
            public string Sha { get; set; } = string.Empty;
            public int AttachmentId { get; set; }
        }

        public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
        {
            var work = 0;

            var pending = await _db.Database.SqlQueryRaw<PendingRow>($@"
                SELECT x.sha AS ""Sha"", MIN(x.id) AS ""AttachmentId""
                FROM (SELECT a.""Id"" AS id, {EffectiveSha} AS sha
                      FROM mail_archiver.""EmailAttachments"" a
                      LEFT JOIN mail_archiver.""AttachmentContents"" c ON c.""Id"" = a.""AttachmentContentId"") x
                WHERE x.sha IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM mail_archiver.""AttachmentTexts"" t WHERE t.""Sha256"" = x.sha)
                GROUP BY x.sha
                LIMIT {{0}}", Math.Max(1, _options.BatchSize)).ToListAsync(cancellationToken);

            foreach (var row in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ReadOneAsync(row.AttachmentId, cancellationToken);
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO mail_archiver.""AttachmentTexts""
                        (""Sha256"", ""Text"", ""Status"", ""Method"", ""Engine"", ""EngineVersion"", ""ExtractedAt"", ""Error"")
                    VALUES ({row.Sha}, {result.Text}, {result.Status}, {result.Method}, {result.Engine}, {result.EngineVersion}, {DateTime.UtcNow}, {result.Error})
                    ON CONFLICT (""Sha256"") DO NOTHING", cancellationToken);
                work++;
            }

            work += await IndexReadyEmailsAsync(cancellationToken);
            return work;
        }

        private async Task<AttachmentTextResult> ReadOneAsync(int attachmentId, CancellationToken ct)
        {
            try
            {
                // Tracking is off on purpose: for lean rows the hydrator fills the bytes in memory only.
                var att = await _db.EmailAttachments.AsNoTracking()
                    .Include(a => a.AttachmentContent)
                    .FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
                if (att == null) return Failed("attachment gone");

                if (att.Size > _options.MaxAttachmentMb * 1024L * 1024L) return Failed("too large");

                // No-op for emails that are not lean-stored; otherwise cuts the bytes out of the original
                // and checks them against the recorded SHA-256.
                await _hydrator.HydrateAttachmentAsync(att, ct);
                var bytes = att.Content;
                if (bytes.Length == 0) return Failed("no content");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
                return await Task.Run(() => AttachmentTextExtractor.Extract(bytes, att.FileName, att.ContentType, MaxChars, cts.Token), cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Failed("timeout");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Attachment {AttachmentId}: could not read its text ({Error})", attachmentId, ex.GetType().Name);
                return Failed(ex.GetType().Name);
            }
        }

        private static AttachmentTextResult Failed(string error)
            => new(AttachmentText.Failed, string.Empty, AttachmentText.TextLayer,
                AttachmentTextExtractor.EngineName, AttachmentTextExtractor.EngineVersionValue, error);

        /// <summary>
        /// Emails with attachments whose attachments all have a result get their search text: the texts of their
        /// distinct attachments, joined and cut to the limit. An email whose attachments held no text is marked
        /// done as well, so it is not looked at again.
        /// </summary>
        private async Task<int> IndexReadyEmailsAsync(CancellationToken ct)
        {
            var ids = await _db.Database.SqlQueryRaw<int>($@"
                SELECT e.""Id"" AS ""Value"" FROM mail_archiver.""ArchivedEmails"" e
                WHERE e.""HasAttachments"" AND e.""AttachmentTextAt"" IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM mail_archiver.""EmailAttachments"" a
                      LEFT JOIN mail_archiver.""AttachmentContents"" c ON c.""Id"" = a.""AttachmentContentId""
                      WHERE a.""ArchivedEmailId"" = e.""Id"" AND {EffectiveSha} IS NOT NULL
                        AND NOT EXISTS (SELECT 1 FROM mail_archiver.""AttachmentTexts"" t WHERE t.""Sha256"" = {EffectiveSha}))
                ORDER BY e.""Id""
                LIMIT {{0}}", Math.Max(1, _options.BatchSize) * 4).ToListAsync(ct);
            if (ids.Count == 0) return 0;

            // Locked emails are fine: the locked-email trigger ignores these two derived columns.
            await _db.Database.ExecuteSqlRawAsync($@"
                UPDATE mail_archiver.""ArchivedEmails"" e
                SET ""AttachmentText"" = NULLIF(sub.txt, ''), ""AttachmentTextAt"" = {{1}}
                FROM (
                    SELECT ids.id AS id,
                           left(COALESCE((
                               SELECT string_agg(t.""Text"", E'\n')
                               FROM (SELECT DISTINCT {EffectiveSha} AS sha
                                     FROM mail_archiver.""EmailAttachments"" a
                                     LEFT JOIN mail_archiver.""AttachmentContents"" c ON c.""Id"" = a.""AttachmentContentId""
                                     WHERE a.""ArchivedEmailId"" = ids.id) s
                               JOIN mail_archiver.""AttachmentTexts"" t ON t.""Sha256"" = s.sha
                               WHERE t.""Text"" <> ''), ''), {{2}}) AS txt
                    FROM unnest({{0}}::int[]) AS ids(id)
                ) sub
                WHERE e.""Id"" = sub.id", new object[] { ids.ToArray(), DateTime.UtcNow, MaxChars }, ct);
            return ids.Count;
        }

        public async Task<AttachmentTextStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var texts = await _db.AttachmentTexts.AsNoTracking()
                .GroupBy(t => new { t.Status, t.Method })
                .Select(g => new AttachmentTextCount(g.Key.Status, g.Key.Method, g.LongCount()))
                .ToListAsync(ct);

            var pendingAttachments = await _db.Database.SqlQueryRaw<long>($@"
                SELECT COUNT(DISTINCT x.sha) AS ""Value""
                FROM (SELECT {EffectiveSha} AS sha
                      FROM mail_archiver.""EmailAttachments"" a
                      LEFT JOIN mail_archiver.""AttachmentContents"" c ON c.""Id"" = a.""AttachmentContentId"") x
                WHERE x.sha IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM mail_archiver.""AttachmentTexts"" t WHERE t.""Sha256"" = x.sha)").SingleAsync(ct);

            var awaiting = await _db.Database.SqlQueryRaw<long>(@"
                SELECT COUNT(*) AS ""Value"" FROM mail_archiver.""ArchivedEmails""
                WHERE ""HasAttachments"" AND ""AttachmentTextAt"" IS NULL").SingleAsync(ct);

            return new AttachmentTextStatus(_options.Mode, texts, pendingAttachments, awaiting);
        }

        public async Task<int> RescanAsync(bool all, CancellationToken ct = default)
        {
            if (all)
            {
                var removed = await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM mail_archiver.""AttachmentTexts""", ct);
                await _db.Database.ExecuteSqlRawAsync(
                    @"UPDATE mail_archiver.""ArchivedEmails"" SET ""AttachmentText"" = NULL, ""AttachmentTextAt"" = NULL
                      WHERE ""AttachmentText"" IS NOT NULL OR ""AttachmentTextAt"" IS NOT NULL", ct);
                return removed;
            }

            // Failed results only: drop them and send the emails that contain them back to the queue.
            return await _db.Database.ExecuteSqlRawAsync($@"
                WITH del AS (DELETE FROM mail_archiver.""AttachmentTexts"" WHERE ""Status"" = 'Failed' RETURNING ""Sha256"")
                UPDATE mail_archiver.""ArchivedEmails"" e SET ""AttachmentTextAt"" = NULL
                WHERE e.""Id"" IN (
                    SELECT a.""ArchivedEmailId"" FROM mail_archiver.""EmailAttachments"" a
                    LEFT JOIN mail_archiver.""AttachmentContents"" c ON c.""Id"" = a.""AttachmentContentId""
                    WHERE {EffectiveSha} IN (SELECT ""Sha256"" FROM del))", ct);
        }
    }

    /// <summary>
    /// Runs <see cref="IAttachmentTextProcessor"/> in the background when Ocr:Mode is not off. cpu and gpu both
    /// read the text layer only for now: no OCR engine is installed yet (that is the next step).
    /// </summary>
    public sealed class AttachmentTextWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly AttachmentTextOptions _options;
        private readonly ILogger<AttachmentTextWorker> _logger;

        public AttachmentTextWorker(IServiceScopeFactory scopes, IOptions<AttachmentTextOptions> options, ILogger<AttachmentTextWorker> logger)
        {
            _scopes = scopes; _options = options.Value; _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.IsOff)
            {
                _logger.LogInformation("Attachment text is off (Ocr:Mode=off)");
                return;
            }
            if (!string.Equals(_options.Mode, "cpu", StringComparison.OrdinalIgnoreCase) && !string.Equals(_options.Mode, "gpu", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Ocr:Mode '{Mode}' is not valid (off, cpu or gpu); attachment text stays off", _options.Mode);
                return;
            }
            _logger.LogWarning("Ocr:Mode={Mode}: no OCR engine is installed yet, only the text layer of attachments is read (scans and photos are marked NeedsOcr)", _options.Mode);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, _options.StartupDelayMinutes)), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var work = 0;
                    try
                    {
                        using var scope = _scopes.CreateScope();
                        work = await scope.ServiceProvider.GetRequiredService<IAttachmentTextProcessor>().ProcessBatchAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _logger.LogError(ex, "Attachment text round failed"); }

                    if (work == 0) await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.IdleDelaySeconds)), stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }
}
