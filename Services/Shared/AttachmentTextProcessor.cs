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
        long EmailsAwaitingIndex,
        string? OcrEngine = null,
        string? OcrUnavailable = null,
        IReadOnlyList<AttachmentTextFailure>? Failures = null);

    public sealed record AttachmentTextCount(string Status, string Method, long Count);

    /// <summary>Why attachments could not be read: the stored reason of the failed results, most common first (the page turns it into plain words).</summary>
    public sealed record AttachmentTextFailure(string Method, string Reason, long Count);

    public interface IAttachmentTextProcessor
    {
        /// <summary>One round: reads up to BatchSize attachments that have no text yet, then fills the search text of the emails that are complete. Returns the amount of work done (0 = idle).</summary>
        Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default);

        Task<AttachmentTextStatus> GetStatusAsync(CancellationToken cancellationToken = default);

        /// <summary>Forgets failed results (all = false) or every result (all = true) so they are read again. Returns the number of results removed.</summary>
        Task<int> RescanAsync(bool all, CancellationToken cancellationToken = default);

        /// <summary>Forgets the NeedsOcr results so the OCR engine reads them (after the engine was switched on). Returns the number removed.</summary>
        Task<int> RescanNeedsOcrAsync(CancellationToken cancellationToken = default);
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
        private readonly IOptionsMonitor<AttachmentTextOptions> _optionsMonitor;
        /// <summary>Read on every use, so a setting changed on the Integrity &amp; Storage page applies to the next attachment.</summary>
        private AttachmentTextOptions _options => _optionsMonitor.CurrentValue;
        private readonly ILogger<AttachmentTextProcessor> _logger;
        private readonly IOcrEngine? _ocr;

        public AttachmentTextProcessor(MailArchiverDbContext db, IStrippedContentHydrator hydrator,
            IOptionsMonitor<AttachmentTextOptions> options, ILogger<AttachmentTextProcessor> logger, IOcrEngine? ocr = null)
        {
            _db = db; _hydrator = hydrator; _optionsMonitor = options; _logger = logger; _ocr = ocr;
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
                var result = await Task.Run(() => AttachmentTextExtractor.Extract(bytes, att.FileName, att.ContentType, MaxChars, cts.Token), cts.Token);

                // No text layer: with the OCR engine on and its models present, read the picture(s).
                if (result.Status == AttachmentText.NeedsOcr && _options.UsesOcr && _ocr != null && _ocr.Unavailable == null)
                    result = await OcrAsync(bytes, att.FileName, att.ContentType, result, ct);
                return result;
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

        private AttachmentTextResult OcrResult(string status, string text, string? error = null)
            => new(status, text, AttachmentText.Ocr, _ocr!.Name, _ocr.Version, error);

        /// <summary>
        /// Reads a picture, or the pictures of a scanned PDF (at most Ocr:MaxPages pages), with the OCR engine. A picture with
        /// no recognisable text is Done with empty text (it was read, there was nothing), so it is not read again.
        /// A scan whose pages cannot be turned into pictures is Failed with the reason.
        /// </summary>
        private async Task<AttachmentTextResult> OcrAsync(byte[] bytes, string? fileName, string? contentType, AttachmentTextResult original, CancellationToken ct)
        {
            try
            {
                var source = AttachmentTextExtractor.OcrSourceOf(fileName, contentType);
                if (source == AttachmentTextExtractor.OcrSource.None) return original;

                var pieces = new List<(int Page, byte[] Bytes)>();
                var skippedPages = new List<int>();
                var pages = 1;
                if (source == AttachmentTextExtractor.OcrSource.Image)
                {
                    pieces.Add((1, bytes));
                }
                else
                {
                    using var pdfCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pdfCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
                    var extracted = await Task.Run(() => PdfPageImages.Extract(bytes, _options.MaxPages, pdfCts.Token, _options.MaxImagePixels), pdfCts.Token);
                    pages = extracted.Pages;
                    skippedPages.AddRange(extracted.Skipped);
                    pieces.AddRange(extracted.Images.Select(i => (i.Page, i.Bytes)));
                }

                var sb = new System.Text.StringBuilder();
                string? firstError = null;
                var readOne = false;
                foreach (var piece in pieces)
                {
                    ct.ThrowIfCancellationRequested();
                    using var imageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    imageCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSecondsPerImage)));
                    OcrImageResult r;
                    try { r = await _ocr!.RecognizeAsync(piece.Bytes, imageCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { r = new OcrImageResult(string.Empty, "ocr timeout"); }

                    if (!r.Ok) { firstError ??= r.Error; continue; }
                    readOne = true;
                    if (r.Text.Length > 0) sb.Append(r.Text).Append('\n');
                    if (sb.Length >= MaxChars) break;
                }

                var text = AttachmentTextExtractor.Clean(sb.ToString(), MaxChars);
                if (text.Length > 0)
                {
                    var note = skippedPages.Count > 0 ? $"{skippedPages.Count} page(s) could not be read as pictures" : firstError;
                    return OcrResult(AttachmentText.Done, text, note);
                }
                if (readOne && skippedPages.Count == 0 && firstError == null) return OcrResult(AttachmentText.Done, string.Empty);   // read, nothing there
                if (pieces.Count == 0 && skippedPages.Count == 0) return OcrResult(AttachmentText.Done, string.Empty);                 // PDF without pictures
                var reason = firstError ?? (skippedPages.Count > 0 ? "page pictures in an unsupported format" : "ocr found nothing readable");
                return OcrResult(AttachmentText.Failed, string.Empty, reason);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return OcrResult(AttachmentText.Failed, string.Empty, "ocr timeout"); }
            catch (Exception ex)
            {
                _logger.LogWarning("OCR of an attachment failed ({Error})", ex.GetType().Name);
                return OcrResult(AttachmentText.Failed, string.Empty, "ocr failed: " + ex.GetType().Name);
            }
        }

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
            // F-16: one bad row must not stop every later email. If the batch statement fails, each email is done on its
            // own, and an email that still fails is marked done WITHOUT text (logged) so the queue moves on.
            try
            {
                await UpdateAttachmentTextAsync(ids.ToArray(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Batch indexing of attachment text failed ({Error}); doing the emails one by one", ex.GetType().Name);
                foreach (var id in ids)
                {
                    try { await UpdateAttachmentTextAsync(new[] { id }, ct); }
                    catch (Exception one) when (one is not OperationCanceledException)
                    {
                        _logger.LogError("Attachment text of email {EmailId} could not be indexed ({Error}); it is marked done without text", id, one.GetType().Name);
                        await _db.Database.ExecuteSqlRawAsync(
                            @"UPDATE mail_archiver.""ArchivedEmails"" SET ""AttachmentTextAt"" = {0} WHERE ""Id"" = {1}",
                            new object[] { DateTime.UtcNow, id }, ct);
                    }
                }
            }
            return ids.Count;
        }

        private async Task UpdateAttachmentTextAsync(int[] ids, CancellationToken ct)
        {
            // The cap is also reduced by what the email's other searchable fields already use (full-text vectors
            // are limited to about 1 MB; a character can be up to 4 bytes), so one adversarial email cannot
            // push the whole row over the limit.
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
                               WHERE t.""Text"" <> ''), ''),
                                LEAST({{2}}, GREATEST(0, (900000 - octet_length(
                                    COALESCE(ee.""Subject"", '') || COALESCE(ee.""Body"", '') || COALESCE(ee.""From"", '') ||
                                    COALESCE(ee.""To"", '') || COALESCE(ee.""Cc"", '') || COALESCE(ee.""Bcc"", ''))) / 4))::int) AS txt
                    FROM unnest({{0}}::int[]) AS ids(id)
                    JOIN mail_archiver.""ArchivedEmails"" ee ON ee.""Id"" = ids.id
                ) sub
                WHERE e.""Id"" = sub.id", new object[] { ids, DateTime.UtcNow, MaxChars }, ct);
        }

        public async Task<AttachmentTextStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var texts = await _db.AttachmentTexts.AsNoTracking()
                .GroupBy(t => new { t.Status, t.Method })
                .Select(g => new AttachmentTextCount(g.Key.Status, g.Key.Method, g.LongCount()))
                .ToListAsync(ct);

            // Fork (page): the most common reasons for failed results, so the page can say why in plain words.
            var failures = await _db.AttachmentTexts.AsNoTracking()
                .Where(t => t.Status == AttachmentText.Failed)
                .GroupBy(t => new { t.Method, t.Error })
                .Select(g => new { g.Key.Method, g.Key.Error, Count = g.LongCount() })
                .OrderByDescending(x => x.Count).Take(5)
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

            return new AttachmentTextStatus(_options.Mode, texts, pendingAttachments, awaiting,
                _options.UsesOcr && _ocr != null ? _ocr.Version : null,
                _options.UsesOcr && _ocr != null ? _ocr.Unavailable : null,
                failures.Select(f => new AttachmentTextFailure(f.Method, f.Error ?? string.Empty, f.Count)).ToList());
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

            return await RescanStatusAsync("Failed", ct);
        }

        public Task<int> RescanNeedsOcrAsync(CancellationToken ct = default) => RescanStatusAsync("NeedsOcr", ct);

        // Drops the results with this status and sends the emails that contain them back to the queue.
        private async Task<int> RescanStatusAsync(string status, CancellationToken ct)
        {
            // F-25: the status is a parameter, not text in the SQL ({{0}} is the first parameter; EffectiveSha is a constant fragment).
            return await _db.Database.ExecuteSqlRawAsync($@"
                WITH del AS (DELETE FROM mail_archiver.""AttachmentTexts"" WHERE ""Status"" = {{0}} RETURNING ""Sha256"")
                UPDATE mail_archiver.""ArchivedEmails"" e SET ""AttachmentTextAt"" = NULL
                WHERE e.""Id"" IN (
                    SELECT a.""ArchivedEmailId"" FROM mail_archiver.""EmailAttachments"" a
                    LEFT JOIN mail_archiver.""AttachmentContents"" c ON c.""Id"" = a.""AttachmentContentId""
                    WHERE {EffectiveSha} IN (SELECT ""Sha256"" FROM del))", new object[] { status }, ct);
        }
    }

    /// <summary>
    /// Runs <see cref="IAttachmentTextProcessor"/> in the background when Ocr:Mode is not off. cpu and gpu both use the CPU
    /// OCR engine when its models are present; without them only the text layer is read and scans stay NeedsOcr.
    /// </summary>
    public sealed class AttachmentTextWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly IOptionsMonitor<AttachmentTextOptions> _monitor;
        private readonly ILogger<AttachmentTextWorker> _logger;
        private string? _lastState;
        private bool _firstRound = true;

        public AttachmentTextWorker(IServiceScopeFactory scopes, IOptionsMonitor<AttachmentTextOptions> options, ILogger<AttachmentTextWorker> logger)
        {
            _scopes = scopes; _monitor = options; _logger = logger;
        }

        /// <summary>How long to wait before looking at the mode again while the worker is off.</summary>
        internal static readonly TimeSpan OffRecheck = TimeSpan.FromSeconds(30);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, _monitor.CurrentValue.StartupDelayMinutes)), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var worked = await RoundAsync(stoppingToken);
                    if (!worked) await Task.Delay(CurrentIdle(), stoppingToken);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }

        private TimeSpan CurrentIdle()
        {
            var o = _monitor.CurrentValue;
            return o.IsOff ? OffRecheck : TimeSpan.FromSeconds(Math.Max(1, o.IdleDelaySeconds));
        }

        /// <summary>
        /// One look at the mode and, when the worker is on, one batch. The mode is read again every round (options monitor), so
        /// switching it on or off on the Integrity &amp; Storage page applies without a restart. Returns true when work was done.
        /// </summary>
        internal async Task<bool> RoundAsync(CancellationToken stoppingToken)
        {
            var o = _monitor.CurrentValue;
            if (o.IsOff)
            {
                if (_lastState != "off") _logger.LogInformation("Attachment text is off (Ocr:Mode=off)");
                _lastState = "off";
                return false;
            }
            if (!o.UsesOcr)
            {
                if (_lastState != "invalid:" + o.Mode) _logger.LogError("Ocr:Mode '{Mode}' is not valid (off, cpu or gpu); attachment text stays off", o.Mode);
                _lastState = "invalid:" + o.Mode;
                return false;
            }

            // Mode just became cpu/gpu (at start or later): say what is available and queue the earlier NeedsOcr items once.
            var state = "on:" + o.Mode.ToLowerInvariant();
            if (_lastState != state)
            {
                IOcrEngine? ocr;
                using (var engineScope = _scopes.CreateScope()) ocr = engineScope.ServiceProvider.GetService<IOcrEngine>();
                if (ocr != null && ocr.Unavailable == null)
                    _logger.LogInformation("Ocr:Mode={Mode}: OCR engine on ({Engine}); scans and photos are read, existing NeedsOcr items are queued again", o.Mode, ocr.Name);
                else
                    _logger.LogWarning("Ocr:Mode={Mode}: no OCR engine is available ({Reason}); only the text layer of attachments is read (scans and photos are marked NeedsOcr)",
                        o.Mode, ocr?.Unavailable ?? "not registered");
                _lastState = state;
                if (ocr != null && ocr.Unavailable == null)
                {
                    try
                    {
                        using var scope = _scopes.CreateScope();
                        var n = await scope.ServiceProvider.GetRequiredService<IAttachmentTextProcessor>().RescanNeedsOcrAsync(stoppingToken);
                        if (n > 0) _logger.LogInformation("Queued {Count} earlier NeedsOcr result(s) for OCR", n);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _logger.LogError(ex, "Could not queue NeedsOcr results for OCR"); }
                }
            }

            var work = 0;
            try
            {
                using var scope = _scopes.CreateScope();
                work = await scope.ServiceProvider.GetRequiredService<IAttachmentTextProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogError(ex, "Attachment text round failed"); }
            return work > 0;
        }
    }
}
