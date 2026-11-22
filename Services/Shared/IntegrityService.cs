using System.IO.Compression;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailArchiver.Services.Shared
{
    /// <summary>Outcome of verifying one archived email against its stored original.</summary>
    public enum IntegrityStatus
    {
        /// <summary>
        /// The original matches its recorded SHA-256, the audit chain agrees with that hash (or the email was
        /// stored before the chain existed), and everything the application shows or serves from the database
        /// agrees with the original (see <see cref="IntegrityService"/> for exactly what is compared).
        /// </summary>
        Valid,
        /// <summary>The stored original no longer matches the SHA-256 recorded when it was archived, or it cannot be decoded.</summary>
        Invalid,
        /// <summary>A record of the original exists (a row or an audit chain entry) but its file or content is gone.</summary>
        Missing,
        /// <summary>No original was stored for this email (archived without RawMessages), so nothing can be proven.</summary>
        NotStored,
        /// <summary>The original is intact, but the database copy derived from it disagrees with it.</summary>
        Inconsistent,
        /// <summary>The hash of the original in the database differs from the hash the audit chain recorded when it was stored.</summary>
        ChainMismatch
    }

    public sealed record IntegrityReport(
        int EmailId,
        IntegrityStatus Status,
        string? Sha256,
        string? Source,
        string? StorageType,
        bool Lean,
        IReadOnlyList<string> Findings,
        string ChainState = IntegrityChainState.Unknown,
        IReadOnlyList<string>? NotComparedList = null)
    {
        /// <summary>Things that could not be compared exactly (large or reduced fields, API-sourced fields); informational.</summary>
        public IReadOnlyList<string> NotCompared => NotComparedList ?? Array.Empty<string>();
    }

    /// <summary>Whether the audit chain holds a record of the original for an email.</summary>
    public static class IntegrityChainState
    {
        /// <summary>The chain has an entry for this original and its hash agrees with the database.</summary>
        public const string Chained = "Chained";
        /// <summary>No chain entry exists (stored before the chain was switched on): informational, not a defect.</summary>
        public const string Unchained = "Unchained";
        /// <summary>A chain entry exists and its hash disagrees with the database, or the entry itself is corrupt.</summary>
        public const string Mismatch = "Mismatch";
        public const string Unknown = "Unknown";
    }

    public interface IIntegrityService
    {
        Task<IntegrityReport> VerifyAsync(int emailId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Proves an archived email against its stored original. It
    /// <list type="number">
    /// <item>reads the original and checks its SHA-256 against the row (an undecodable, truncated or oversized
    /// file is Invalid, never an error);</item>
    /// <item>checks that the hash in the row is the one the audit chain recorded when the original was stored
    /// (<see cref="IntegrityStatus.ChainMismatch"/>; emails stored before the chain existed are reported as
    /// <see cref="IntegrityChainState.Unchained"/>);</item>
    /// <item>re-derives what the application shows and serves from the database and compares it with the original:
    /// subject, sender, recipients, display names, sent date (within 2 seconds), the text body (up to 100 KB), the
    /// HTML body and the original-body copies, and for every attachment its bytes (hashed again), name, type and
    /// size, plus that every real attachment part of the original has a database counterpart. A lean email must
    /// carry no HTML body, no original-body copy and no attachment bytes.</item>
    /// </list>
    /// What is not compared is listed in <see cref="IntegrityReport.NotCompared"/>: fields of Graph rows that come
    /// from the Graph API instead of the message, and fields stored in a reduced form because they were very large.
    /// Read-only.
    /// </summary>
    public sealed class IntegrityService : IIntegrityService
    {
        // Bodies longer than this are not compared: ingest truncates them, so equality would be unreliable.
        public const int MaxComparedBodyBytes = 100_000;
        private const int MaxComparedHtmlBytes = 900_000;
        private const double DateToleranceSeconds = 2;

        private readonly MailArchiverDbContext _context;
        private readonly IRawMessageStore _store;
        private readonly DateTimeHelper? _dateTimeHelper;

        public IntegrityService(MailArchiverDbContext context, IRawMessageStore store, DateTimeHelper? dateTimeHelper = null)
        {
            _context = context;
            _store = store;
            _dateTimeHelper = dateTimeHelper;
        }

        private sealed record RawRow(string Sha256, string Source, string StorageType);

        private sealed record EmailRow(
            string Subject, string Body, string HtmlBody, string From, string To, string Cc, string Bcc,
            string? FromDisplayName, string? ToDisplayNames, string? CcDisplayNames, string? BccDisplayNames,
            DateTime SentDate, byte[]? OriginalBodyText, byte[]? OriginalBodyHtml,
            string? BodyUntruncatedText, string? BodyUntruncatedHtml, bool ContentStripped);

        public async Task<IntegrityReport> VerifyAsync(int emailId, CancellationToken cancellationToken = default)
        {
            var e = await _context.ArchivedEmails.AsNoTracking()
                .Where(x => x.Id == emailId)
                .Select(x => new EmailRow(
                    x.Subject, x.Body, x.HtmlBody, x.From, x.To, x.Cc, x.Bcc,
                    x.FromDisplayName, x.ToDisplayNames, x.CcDisplayNames, x.BccDisplayNames,
                    x.SentDate, x.OriginalBodyText, x.OriginalBodyHtml,
                    x.BodyUntruncatedText, x.BodyUntruncatedHtml, x.ContentStripped))
                .FirstOrDefaultAsync(cancellationToken);

            if (e == null)
                throw new InvalidOperationException($"Email {emailId} does not exist.");

            var row = await _context.RawMessages.AsNoTracking()
                .Where(r => r.ArchivedEmailId == emailId)
                .Select(r => new RawRow(r.Sha256, r.Source, r.StorageType))
                .FirstOrDefaultAsync(cancellationToken);

            // What the audit chain recorded when an original was stored for this email (F-04).
            var chainEntries = await _context.AuditChain.AsNoTracking()
                .Where(c => c.Kind == "original.stored" && c.EmailId == emailId)
                .OrderBy(c => c.Seq)
                .ToListAsync(cancellationToken);

            if (row == null)
            {
                if (chainEntries.Count > 0)
                {
                    // F-05: the chain proves an original was stored, so a missing row is not "archived before originals existed".
                    return new IntegrityReport(emailId, IntegrityStatus.Missing, chainEntries[^1].Sha256, null, null, e.ContentStripped,
                        new[] { $"The audit chain records an original stored for this email (SHA-256 {chainEntries[^1].Sha256}), but its record in the database is gone." },
                        IntegrityChainState.Chained);
                }

                return new IntegrityReport(emailId, IntegrityStatus.NotStored, null, null, null, e.ContentStripped,
                    new[] { "No original was stored for this email, so its content cannot be proven." }, IntegrityChainState.Unchained);
            }

            // Chain check on the recorded hash (cheap; done before the file is read so a mismatch is never hidden).
            var chainState = IntegrityChainState.Unchained;
            var chainFinding = (string?)null;
            if (chainEntries.Count > 0)
            {
                chainState = IntegrityChainState.Chained;
                var corrupt = chainEntries.FirstOrDefault(c => !string.Equals(c.EntryHash, AuditChainHash.Compute(c), StringComparison.Ordinal));
                if (corrupt != null)
                {
                    chainState = IntegrityChainState.Mismatch;
                    chainFinding = $"Audit chain entry {corrupt.Seq} for this email does not match its own hash.";
                }
                else if (!chainEntries.Any(c => string.Equals(c.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    chainState = IntegrityChainState.Mismatch;
                    chainFinding = $"The original's hash in the database ({row.Sha256}) is not the hash the audit chain recorded when it was stored ({chainEntries[^1].Sha256}).";
                }
            }

            RawMessageContent? original;
            try
            {
                original = await _store.ReadAsync(emailId);
            }
            catch (RawMessageIntegrityException ex)
            {
                return Report(emailId, IntegrityStatus.Invalid, row, e, chainState, ex.Message, chainFinding);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is InvalidOperationException)
            {
                // F-10: a truncated, non-gzip or oversized (decompression-bomb) file is damage, reported as such.
                return Report(emailId, IntegrityStatus.Invalid, row, e, chainState,
                    "The stored original cannot be decoded (corrupt, truncated or larger than its recorded size).", chainFinding);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // F-27: no server paths in what users see.
                return Report(emailId, IntegrityStatus.Missing, row, e, chainState,
                    "The stored original could not be read.", chainFinding);
            }

            if (original == null)
            {
                return Report(emailId, IntegrityStatus.Missing, row, e, chainState,
                    "The original is recorded but its stored content could not be found.", chainFinding);
            }

            // The Postgres backend does not re-check the hash on read; do it here so Valid always means proven.
            var actual = RawMessageCodec.ComputeSha256(original.Bytes);
            if (!string.Equals(actual, row.Sha256, StringComparison.Ordinal))
            {
                return Report(emailId, IntegrityStatus.Invalid, row, e, chainState,
                    $"Checksum mismatch: recorded {row.Sha256}, stored original has {actual}.", chainFinding);
            }

            var findings = new List<string>();
            if (chainFinding != null) findings.Add(chainFinding);

            MimeMessage message;
            try
            {
                using var stream = new MemoryStream(original.Bytes, writable: false);
                message = await MimeMessage.LoadAsync(ParserOptions.Default, stream, cancellationToken);
            }
            catch (Exception ex)
            {
                findings.Add($"The stored original cannot be parsed: {ex.Message}");
                return Finish(emailId, row.Sha256, row.Source, row.StorageType, e.ContentStripped, findings, chainState, new List<string>());
            }

            var notCompared = new List<string>();
            // F-09: a re-serialized original (mbox import, IMAP recovery fetch) is intact but is not the bytes the server delivered.
            if (string.Equals(row.Source, RawMessageSource.Serialized, StringComparison.Ordinal))
                notCompared.Add("The stored original is a re-serialized copy (source: serialized), not the bytes the server delivered.");
            var fromMessage = !string.Equals(row.Source, RawMessageSource.Graph, StringComparison.Ordinal);
            CompareFields(e, message, fromMessage, findings, notCompared);
            await CompareAttachmentsAsync(emailId, message, row.Source, fromMessage, findings, notCompared, cancellationToken);

            return Finish(emailId, row.Sha256, row.Source, row.StorageType, e.ContentStripped, findings, chainState, notCompared);
        }

        private static IntegrityReport Report(int emailId, IntegrityStatus status, RawRow row, EmailRow e, string chainState,
            string finding, string? chainFinding)
        {
            var findings = new List<string> { finding };
            if (chainFinding != null) findings.Add(chainFinding);
            return new IntegrityReport(emailId, status, row.Sha256, row.Source, row.StorageType,
                e.ContentStripped, findings, chainState);
        }

        private static IntegrityReport Finish(int emailId, string sha, string? source, string? storage, bool lean,
            List<string> findings, string chainState, List<string> notCompared)
        {
            IntegrityStatus status;
            if (chainState == IntegrityChainState.Mismatch) status = IntegrityStatus.ChainMismatch;
            else status = findings.Count == 0 ? IntegrityStatus.Valid : IntegrityStatus.Inconsistent;
            return new IntegrityReport(emailId, status, sha, source, storage, lean, findings, chainState, notCompared);
        }

        private void CompareFields(EmailRow e, MimeMessage message, bool fromMessage, List<string> findings, List<string> notCompared)
        {
            // Same derivation as EmailCoreService.ArchiveEmailAsync (shared in LeanIndexFields, which the
            // import and Graph lean paths also use to fill the index fields from the original).
            var expected = LeanIndexFields.Derive(message);

            if (!string.Equals(e.Subject, expected.Subject, StringComparison.Ordinal)) findings.Add("Subject differs from the original.");
            if (!string.Equals(e.From, expected.From, StringComparison.Ordinal)) findings.Add("Sender differs from the original.");
            if (!string.Equals(e.To, expected.To, StringComparison.Ordinal)) findings.Add("Recipients (To) differ from the original.");
            if (!string.Equals(e.Cc, expected.Cc, StringComparison.Ordinal)) findings.Add("Recipients (Cc) differ from the original.");
            if (!string.Equals(e.Bcc, expected.Bcc, StringComparison.Ordinal)) findings.Add("Recipients (Bcc) differ from the original.");

            var (text, _) = LeanBodyExtractor.Extract(message);
            if (!string.IsNullOrEmpty(text))
            {
                if (Encoding.UTF8.GetByteCount(text) < MaxComparedBodyBytes)
                {
                    var expectedBody = MailContentHelper.SanitizeLongTokens(text) ?? string.Empty;
                    if (!string.Equals(e.Body ?? string.Empty, expectedBody, StringComparison.Ordinal))
                        findings.Add("The stored text body differs from the original.");
                }
                else notCompared.Add("Text body (over 100 KB, stored truncated)");
            }

            // Lean rows keep no HTML body and no copy of the bodies: they are rebuilt from the original on every read.
            if (e.ContentStripped)
            {
                if (!string.IsNullOrEmpty(e.HtmlBody)) findings.Add("A lean email carries an HTML body in the database; lean emails must read it from the original.");
                if (e.OriginalBodyText != null || e.OriginalBodyHtml != null || e.BodyUntruncatedText != null || e.BodyUntruncatedHtml != null)
                    findings.Add("A lean email carries a copy of its body in the database; lean emails must read it from the original.");
            }

            if (!fromMessage)
            {
                // Graph rows take these from the Graph API, not from the message.
                notCompared.Add("HTML body, original-body copies, display names and sent date (Graph rows take them from the Graph API)");
                return;
            }

            // Sent date, within a small tolerance (the display-timezone conversion is shared with ingest).
            if (_dateTimeHelper != null)
            {
                var expectedDate = _dateTimeHelper.ConvertToDisplayTimeZone(MailContentHelper.ExtractEmailDate(message.Date, message.Headers));
                if (Math.Abs((e.SentDate - expectedDate).TotalSeconds) > DateToleranceSeconds)
                    findings.Add("The sent date differs from the original.");
            }
            else notCompared.Add("Sent date (no time-zone helper available)");

            // Display names, derived exactly as ingest does.
            var fromAddress = message.From?.FirstOrDefault() as MailboxAddress;
            string Names(InternetAddressList? list) => MailContentHelper.TruncateFieldForTsvector(MailContentHelper.CleanText(string.Join(", ",
                list?.Select(m => (m as MailboxAddress)?.Name).Where(n => !string.IsNullOrEmpty(n)) ?? Enumerable.Empty<string>())), 50_000);
            var expectedFromName = MailContentHelper.TruncateFieldForTsvector(MailContentHelper.CleanText(fromAddress?.Name), 50_000);
            if (!string.Equals(e.FromDisplayName ?? string.Empty, expectedFromName ?? string.Empty, StringComparison.Ordinal)) findings.Add("The sender's display name differs from the original.");
            if (!string.Equals(e.ToDisplayNames ?? string.Empty, Names(message.To), StringComparison.Ordinal)) findings.Add("The recipients' (To) display names differ from the original.");
            if (!string.Equals(e.CcDisplayNames ?? string.Empty, Names(message.Cc), StringComparison.Ordinal)) findings.Add("The recipients' (Cc) display names differ from the original.");
            if (!string.Equals(e.BccDisplayNames ?? string.Empty, Names(message.Bcc), StringComparison.Ordinal)) findings.Add("The recipients' (Bcc) display names differ from the original.");

            // HTML body and the original-body copies of a full email.
            if (!e.ContentStripped)
            {
                var rawHtml = message.HtmlBody;
                var expectedHtml = string.IsNullOrEmpty(rawHtml) ? string.Empty : MailContentHelper.CleanText(rawHtml);
                if (Encoding.UTF8.GetByteCount(expectedHtml) > MaxComparedHtmlBytes)
                    notCompared.Add("HTML body (large, stored in a reduced form)");
                else if (!string.Equals(e.HtmlBody ?? string.Empty, expectedHtml, StringComparison.Ordinal))
                    findings.Add("The stored HTML body differs from the original.");

                var rawText = message.TextBody;
                if (e.OriginalBodyHtml != null && !MatchesAny(e.OriginalBodyHtml, rawHtml))
                    findings.Add("The stored original-HTML copy differs from the original.");
                if (e.OriginalBodyText != null && !MatchesAny(e.OriginalBodyText, rawText, rawHtml))
                    findings.Add("The stored original-text copy differs from the original.");
            }
        }

        // The original-body copies hold the body as parsed (possibly with null bytes) or cleaned.
        private static bool MatchesAny(byte[] stored, params string?[] sources)
        {
            foreach (var s in sources)
            {
                if (string.IsNullOrEmpty(s)) continue;
                if (stored.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(s))) return true;
                if (stored.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(MailContentHelper.CleanText(s)))) return true;
            }
            return false;
        }

        private sealed record AttachmentRow(int Id, string FileName, string ContentType, long Size, string? PartPath, string? Sha256, string? StoredHash, byte[]? Bytes);

        private sealed record OriginalPart(string Path, string Sha256, long Length, string ContentType, string? FileName, bool IsAttachment);

        private async Task CompareAttachmentsAsync(int emailId, MimeMessage message, string? source, bool fromMessage,
            List<string> findings, List<string> notCompared, CancellationToken cancellationToken)
        {
            var rows = await _context.EmailAttachments.AsNoTracking()
                .Where(a => a.ArchivedEmailId == emailId)
                .Select(a => new AttachmentRow(
                    a.Id, a.FileName, a.ContentType, a.Size, a.PartPath, a.Sha256,
                    a.AttachmentContent != null ? a.AttachmentContent.Hash : null,
                    a.AttachmentContent != null ? a.AttachmentContent.Content : a.LegacyContent))
                .ToListAsync(cancellationToken);

            // Every part of the original with content, decoded and hashed once.
            var parts = new List<OriginalPart>();
            foreach (var (path, entity) in MimePartLocator.Enumerate(message))
            {
                if (entity is not MimePart part || part.Content == null) continue;
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, cancellationToken);
                var bytes = ms.ToArray();
                parts.Add(new OriginalPart(path, RawMessageCodec.ComputeSha256(bytes), bytes.LongLength,
                    MailContentHelper.CleanText(part.ContentType?.MimeType ?? "application/octet-stream"),
                    string.IsNullOrEmpty(part.FileName) ? null : MailContentHelper.CleanText(part.FileName),
                    part.IsAttachment));
            }

            var coveredHashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Cover(string sha) => coveredHashes[sha] = coveredHashes.GetValueOrDefault(sha) + 1;

            foreach (var a in rows)
            {
                var isCalendar = (a.ContentType ?? string.Empty).StartsWith("text/calendar", StringComparison.OrdinalIgnoreCase);
                var hasBytes = a.Bytes != null && a.Bytes.Length > 0;

                // Independent of the ContentStripped flag: an attachment with a recorded place is checked at that place,
                // one with bytes is checked by hashing them, and one with neither is a defect.
                if (!string.IsNullOrEmpty(a.PartPath) && !string.IsNullOrEmpty(a.Sha256))
                {
                    var content = await OriginalMessageReader.ReadAttachmentAsync(message, a.PartPath);
                    if (content == null)
                    {
                        findings.Add($"Attachment '{a.FileName}' is not at its recorded place in the original.");
                        continue;
                    }
                    if (!string.Equals(content.Sha256, a.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add($"Attachment '{a.FileName}' does not match its recorded hash.");
                        continue;
                    }
                    Cover(content.Sha256);
                    if (a.Size != content.Bytes.LongLength) findings.Add($"Attachment '{a.FileName}' has a different size than in the original.");
                    if (hasBytes && !string.Equals(RawMessageCodec.ComputeSha256(a.Bytes!), content.Sha256, StringComparison.OrdinalIgnoreCase))
                        findings.Add($"Attachment '{a.FileName}' carries bytes in the database that differ from the original.");
                    CompareNameAndType(a, parts.FirstOrDefault(p => p.Path == a.PartPath), findings);
                    continue;
                }

                if (hasBytes)
                {
                    var actual = RawMessageCodec.ComputeSha256(a.Bytes!);
                    if (!string.IsNullOrEmpty(a.StoredHash) && !string.Equals(a.StoredHash, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add($"Attachment '{a.FileName}': the stored bytes do not match their recorded hash.");
                        continue;
                    }

                    // A calendar attachment can be synthesized at archive time and is not a part of the original.
                    if (isCalendar) continue;

                    var match = parts.Where(p => string.Equals(p.Sha256, actual, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (match.Count == 0)
                    {
                        findings.Add($"Attachment '{a.FileName}' is not found in the original.");
                        continue;
                    }
                    Cover(actual);
                    if (a.Size != a.Bytes!.LongLength) findings.Add($"Attachment '{a.FileName}' has a different size than its stored bytes.");
                    CompareNameAndType(a, match.FirstOrDefault(p => p.ContentType == a.ContentType) ?? match[0], findings);
                    continue;
                }

                if (isCalendar) continue;
                findings.Add($"Attachment '{a.FileName}' has neither stored bytes nor a place in the original.");
            }

            // Completeness: every real attachment part of the original must have a counterpart (F-03: a deleted row is not Valid).
            // Only for sources whose attachment list comes from the message itself.
            if (fromMessage)
            {
                var needed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in parts.Where(p => p.IsAttachment))
                    needed[p.Sha256] = needed.GetValueOrDefault(p.Sha256) + 1;
                foreach (var (sha, count) in needed)
                {
                    if (coveredHashes.GetValueOrDefault(sha) < count)
                    {
                        var name = parts.First(p => p.IsAttachment && string.Equals(p.Sha256, sha, StringComparison.OrdinalIgnoreCase)).FileName ?? "(unnamed)";
                        findings.Add($"An attachment of the original ('{name}') is missing from the database copy.");
                    }
                }
            }
            else notCompared.Add("Attachment list completeness (Graph rows take attachments from the Graph API)");
        }

        // The file name is only compared when the part names itself (ingest invents names for unnamed parts).
        private static void CompareNameAndType(AttachmentRow a, OriginalPart? part, List<string> findings)
        {
            if (part == null) return;
            if (!string.Equals(a.ContentType, part.ContentType, StringComparison.OrdinalIgnoreCase))
                findings.Add($"Attachment '{a.FileName}' has a different content type than in the original.");
            if (part.FileName != null && !string.Equals(a.FileName, part.FileName, StringComparison.Ordinal))
                findings.Add($"Attachment '{a.FileName}' has a different file name than in the original.");
        }
    }
}
