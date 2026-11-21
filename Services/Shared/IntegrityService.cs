using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailArchiver.Services.Shared
{
    /// <summary>Outcome of verifying one archived email against its stored original.</summary>
    public enum IntegrityStatus
    {
        /// <summary>The original matches its recorded SHA-256 and everything derived from it agrees.</summary>
        Valid,
        /// <summary>The stored original no longer matches the SHA-256 recorded when it was archived.</summary>
        Invalid,
        /// <summary>A record of the original exists but its file or row content is gone.</summary>
        Missing,
        /// <summary>No original was stored for this email (archived without RawMessages), so nothing can be proven.</summary>
        NotStored,
        /// <summary>The original is intact, but the database copy derived from it disagrees with it.</summary>
        Inconsistent
    }

    public sealed record IntegrityReport(
        int EmailId,
        IntegrityStatus Status,
        string? Sha256,
        string? Source,
        string? StorageType,
        bool Lean,
        IReadOnlyList<string> Findings);

    public interface IIntegrityService
    {
        Task<IntegrityReport> VerifyAsync(int emailId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Proves an archived email against its stored original: re-hashes the original (the store
    /// enforces the recorded SHA-256), then re-derives what the database holds from it (subject,
    /// addresses, text body, attachments) and flags any difference as <see cref="IntegrityStatus.Inconsistent"/>.
    /// The derived fields are interpreted with the same helpers ingest uses. Read-only.
    /// </summary>
    public sealed class IntegrityService : IIntegrityService
    {
        // Bodies longer than this are not compared: ingest truncates them, so equality would be unreliable.
        private const int MaxComparedBodyBytes = 100_000;

        private readonly MailArchiverDbContext _context;
        private readonly IRawMessageStore _store;

        public IntegrityService(MailArchiverDbContext context, IRawMessageStore store)
        {
            _context = context;
            _store = store;
        }

        public async Task<IntegrityReport> VerifyAsync(int emailId, CancellationToken cancellationToken = default)
        {
            var email = await _context.ArchivedEmails.AsNoTracking()
                .Where(e => e.Id == emailId)
                .Select(e => new
                {
                    e.Id, e.Subject, e.Body, e.From, e.To, e.Cc, e.Bcc, e.ContentStripped
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (email == null)
                throw new InvalidOperationException($"Email {emailId} does not exist.");

            var row = await _context.RawMessages.AsNoTracking()
                .Where(r => r.ArchivedEmailId == emailId)
                .Select(r => new { r.Sha256, r.Source, r.StorageType })
                .FirstOrDefaultAsync(cancellationToken);

            if (row == null)
            {
                return new IntegrityReport(emailId, IntegrityStatus.NotStored, null, null, null, email.ContentStripped,
                    new[] { "No original was stored for this email, so its content cannot be proven." });
            }

            RawMessageContent? original;
            try
            {
                original = await _store.ReadAsync(emailId);
            }
            catch (RawMessageIntegrityException ex)
            {
                return new IntegrityReport(emailId, IntegrityStatus.Invalid, row.Sha256, row.Source, row.StorageType,
                    email.ContentStripped, new[] { ex.Message });
            }

            if (original == null)
            {
                return new IntegrityReport(emailId, IntegrityStatus.Missing, row.Sha256, row.Source, row.StorageType,
                    email.ContentStripped, new[] { "The original is recorded but its stored content could not be found." });
            }

            // The Postgres backend does not re-check the hash on read; do it here so Valid always means proven.
            var actual = RawMessageCodec.ComputeSha256(original.Bytes);
            if (!string.Equals(actual, row.Sha256, StringComparison.Ordinal))
            {
                return new IntegrityReport(emailId, IntegrityStatus.Invalid, row.Sha256, row.Source, row.StorageType,
                    email.ContentStripped,
                    new[] { $"Checksum mismatch: recorded {row.Sha256}, stored original has {actual}." });
            }

            var findings = new List<string>();
            MimeMessage message;
            try
            {
                using var stream = new MemoryStream(original.Bytes, writable: false);
                message = await MimeMessage.LoadAsync(ParserOptions.Default, stream, cancellationToken);
            }
            catch (Exception ex)
            {
                return new IntegrityReport(emailId, IntegrityStatus.Inconsistent, row.Sha256, row.Source, row.StorageType,
                    email.ContentStripped, new[] { $"The stored original cannot be parsed: {ex.Message}" });
            }

            CompareFields(email.Subject, email.From, email.To, email.Cc, email.Bcc, email.Body, message, findings);
            await CompareAttachmentsAsync(emailId, email.ContentStripped, message, findings, cancellationToken);

            var status = findings.Count == 0 ? IntegrityStatus.Valid : IntegrityStatus.Inconsistent;
            return new IntegrityReport(emailId, status, row.Sha256, row.Source, row.StorageType, email.ContentStripped, findings);
        }

        private static void CompareFields(string subject, string from, string to, string cc, string bcc, string body,
            MimeMessage message, List<string> findings)
        {
            // Same derivation as EmailCoreService.ArchiveEmailAsync.
            var expectedSubject = MailContentHelper.TruncateFieldForTsvector(
                MailContentHelper.CleanText(message.Subject ?? "(No Subject)"), 50_000);
            var fromAddress = message.From?.FirstOrDefault() as MailboxAddress;
            var expectedFrom = MailContentHelper.TruncateFieldForTsvector(
                MailContentHelper.CleanText(fromAddress?.Address ?? string.Empty), 10_000);

            static string Join(InternetAddressList? list) => MailContentHelper.TruncateFieldForTsvector(
                MailContentHelper.CleanText(string.Join(", ",
                    list?.Select(m => m as MailboxAddress).Where(m => m != null).Select(m => m!.Address) ?? Enumerable.Empty<string>())),
                50_000);

            if (!string.Equals(subject, expectedSubject, StringComparison.Ordinal)) findings.Add("Subject differs from the original.");
            if (!string.Equals(from, expectedFrom, StringComparison.Ordinal)) findings.Add("Sender differs from the original.");
            if (!string.Equals(to, Join(message.To), StringComparison.Ordinal)) findings.Add("Recipients (To) differ from the original.");
            if (!string.Equals(cc, Join(message.Cc), StringComparison.Ordinal)) findings.Add("Recipients (Cc) differ from the original.");
            if (!string.Equals(bcc, Join(message.Bcc), StringComparison.Ordinal)) findings.Add("Recipients (Bcc) differ from the original.");

            var (text, _) = LeanBodyExtractor.Extract(message);
            if (!string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text) < MaxComparedBodyBytes)
            {
                var expectedBody = MailContentHelper.SanitizeLongTokens(text) ?? string.Empty;
                if (!string.Equals(body ?? string.Empty, expectedBody, StringComparison.Ordinal))
                    findings.Add("The stored text body differs from the original.");
            }
        }

        private async Task CompareAttachmentsAsync(int emailId, bool lean, MimeMessage message, List<string> findings,
            CancellationToken cancellationToken)
        {
            var attachments = await _context.EmailAttachments.AsNoTracking()
                .Where(a => a.ArchivedEmailId == emailId)
                .Select(a => new
                {
                    a.Id, a.FileName, a.ContentType, a.PartPath, a.Sha256,
                    StoredHash = a.AttachmentContent != null ? a.AttachmentContent.Hash : null
                })
                .ToListAsync(cancellationToken);

            if (attachments.Count == 0) return;

            if (lean)
            {
                foreach (var a in attachments)
                {
                    if (string.IsNullOrEmpty(a.PartPath) || string.IsNullOrEmpty(a.Sha256))
                    {
                        findings.Add($"Attachment '{a.FileName}' has no recorded location or hash.");
                        continue;
                    }

                    var content = await OriginalMessageReader.ReadAttachmentAsync(message, a.PartPath);
                    if (content == null)
                        findings.Add($"Attachment '{a.FileName}' is not at its recorded place in the original.");
                    else if (!string.Equals(content.Sha256, a.Sha256, StringComparison.OrdinalIgnoreCase))
                        findings.Add($"Attachment '{a.FileName}' does not match its recorded hash.");
                }

                return;
            }

            // Full storage: every stored attachment must exist, byte for byte, as a part of the original.
            var partHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, entity) in MimePartLocator.Enumerate(message))
            {
                if (entity is not MimePart part || part.Content == null) continue;
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, cancellationToken);
                partHashes.Add(RawMessageCodec.ComputeSha256(ms.ToArray()));
            }

            foreach (var a in attachments)
            {
                if (string.IsNullOrEmpty(a.StoredHash)) continue; // legacy inline bytes: not compared here

                // A calendar attachment can be synthesized at archive time and is not a part of the original.
                if ((a.ContentType ?? string.Empty).StartsWith("text/calendar", StringComparison.OrdinalIgnoreCase)) continue;

                if (!partHashes.Contains(a.StoredHash))
                    findings.Add($"Attachment '{a.FileName}' is not found in the original.");
            }
        }
    }
}
