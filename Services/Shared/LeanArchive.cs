using MailArchiver.Models;
using MailArchiver.Services.Monitoring;
using MailArchiver.Utilities;
using Microsoft.Extensions.Logging;
using MimeKit;
using System.Text;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// The list fields of an email as derived from its original (what ArchiveEmailAsync stores for IMAP
    /// mail and what IntegrityService expects to find). One definition for the writer and the checker.
    /// </summary>
    public sealed record LeanIndexFieldValues(string Subject, string From, string To, string Cc, string Bcc);

    public static class LeanIndexFields
    {
        public static LeanIndexFieldValues Derive(MimeMessage message)
        {
            var subject = MailContentHelper.TruncateFieldForTsvector(
                MailContentHelper.CleanText(message.Subject ?? "(No Subject)"), 50_000);
            var fromAddress = message.From?.FirstOrDefault() as MailboxAddress;
            var from = MailContentHelper.TruncateFieldForTsvector(
                MailContentHelper.CleanText(fromAddress?.Address ?? string.Empty), 10_000);

            static string Join(InternetAddressList? list) => MailContentHelper.TruncateFieldForTsvector(
                MailContentHelper.CleanText(string.Join(", ",
                    list?.Select(m => m as MailboxAddress).Where(m => m != null).Select(m => m!.Address) ?? Enumerable.Empty<string>())),
                50_000);

            return new LeanIndexFieldValues(subject, from, Join(message.To), Join(message.Cc), Join(message.Bcc));
        }
    }

    /// <summary>What to do with one incoming email in lean mode: a verified plan, or a reason to refuse it.</summary>
    /// <param name="OriginalOnlyRow">
    /// Set when the email cannot be stored lean (it carries synthesized content that is not a part of the
    /// original) but the original WAS written and verified: the email is stored in full and this row is
    /// inserted together with it, so the original exists before the email does (review finding F-02).
    /// </param>
    public sealed record LeanArchiveDecision(LeanIngestPlan? Plan, string? RefusalReason, RawMessage? OriginalOnlyRow = null);

    /// <summary>
    /// The lean-storage decision and the lean row shape, shared by every archive path (IMAP sync,
    /// EML/mbox import, Graph) so they cannot drift apart. The IMAP path uses it unchanged; the
    /// import and Graph paths additionally pass the email to re-derive its index fields from the
    /// verified original, because their parsed copy does not come from the original's own parse
    /// (an import may have been header-recovered, a Graph copy comes from the Graph JSON), and
    /// IntegrityService compares the database copy with exactly that parse.
    /// </summary>
    public static class LeanArchive
    {
        /// <summary>
        /// Writes and verifies the original when lean mode is on, then applies RawMessages:Required.
        /// <paramref name="synthesizedContent"/> is true when the parsed copy carries content that is not
        /// part of the original (a synthesized calendar attachment or body): such an email is stored in
        /// full. <paramref name="rederiveInto"/> is the email whose index fields are re-derived from the
        /// verified original (null for IMAP, whose fields already come from it); if that is not
        /// possible the email is not stored lean.
        /// </summary>
        public static async Task<LeanArchiveDecision> DecideAsync(
            StorageModeOptions storageMode,
            ILeanIngest? ingest,
            RawMessageOptions raw,
            RawMessageCapture? capture,
            IReadOnlyList<EmailAttachment> attachments,
            bool synthesizedContent,
            ArchivedEmail? rederiveInto,
            ILogger logger,
            string? subject)
        {
            LeanIngestPlan? plan = null;
            RawMessage? originalOnly = null;
            if (storageMode.IsLean && ingest != null && capture != null)
            {
                if (synthesizedContent)
                {
                    // Stored in full, but the original is still written and verified FIRST (no attachments
                    // to find again), so RawMessages:Required is satisfied and no ghost row can appear.
                    logger.LogWarning("Lean ingest skipped for '{Subject}': it carries a synthesized calendar attachment; stored in full with its verified original", subject);
                    originalOnly = (await ingest.TryPrepareAsync(capture, Array.Empty<byte[]>()))?.Row;
                }
                else
                {
                    plan = await ingest.TryPrepareAsync(capture, attachments.Select(a => a.Content).ToList());
                    // F-08: the index fields always come from the verified original's own parse (the parsed copy
                    // may have been built from a header-recovered or API-sourced message). If that parse is not
                    // possible the email is not stored lean.
                    if (plan != null && rederiveInto != null && !TryRederiveIndexFields(rederiveInto, capture, logger, subject))
                        plan = null;
                }

                // Lean could not be completed (an attachment not found again, the original not parseable) but the
                // original itself may still be written and verified: then the email is stored in full with it, and
                // RawMessages:Required (refuse only when NO verified original exists) is satisfied.
                if (plan == null && originalOnly == null)
                    originalOnly = (await ingest.TryPrepareAsync(capture, Array.Empty<byte[]>()))?.Row;
            }

            // RawMessages:Required: no verified original, no archive entry. Lean ingest that could not
            // verify (no plan) counts as "no original"; the full-storage fallback would only keep the
            // parsed copy.
            // Rule (F-02): refuse only when NO verified original exists.
            var refusal = RawMessageRequirement.RefusalReason(raw, capture);
            // F-09: with Required, a re-serialized "original" (mbox import, IMAP recovery fetch) is not the bytes the
            // server delivered. It is kept and labelled (source "serialized", shown by the integrity check), but a
            // Required archive refuses it unless serialized imports were allowed on purpose.
            if (refusal == null && raw.Required && !storageMode.AllowSerializedImports
                && string.Equals(capture?.Source, RawMessageSource.Serialized, StringComparison.Ordinal))
                refusal = MetricsReasons.Serialized;
            if (refusal == null && raw.Required && storageMode.IsLean && plan == null && originalOnly == null)
                refusal = MetricsReasons.Failed;

            return new LeanArchiveDecision(plan, refusal, plan == null ? originalOnly : null);
        }

        /// <summary>
        /// Turns the email into its lean form: no HTML body, no untruncated variants, no attachment
        /// bytes, each attachment reduced to its place and hash in the original, and the verified
        /// original's row attached (inserted together with the email).
        /// </summary>
        public static void Apply(ArchivedEmail email, IReadOnlyList<EmailAttachment> source, LeanIngestPlan plan)
        {
            var leanAttachments = new List<EmailAttachment>(source.Count);
            for (var i = 0; i < source.Count; i++)
            {
                var s = source[i];
                // No Content set: the dedup interceptor skips attachments without bytes.
                leanAttachments.Add(new EmailAttachment
                {
                    FileName = s.FileName,
                    ContentType = s.ContentType,
                    ContentId = s.ContentId,
                    Size = s.Size,
                    PartPath = plan.Attachments[i].PartPath,
                    Sha256 = plan.Attachments[i].Sha256
                });
            }

            email.Attachments = leanAttachments;
            email.HtmlBody = string.Empty;
            email.BodyUntruncatedText = null;
            email.BodyUntruncatedHtml = null;
            email.OriginalBodyText = null;
            email.OriginalBodyHtml = null;
            email.ContentStripped = true;
            email.RawMessage = plan.Row; // inserted together with the email
        }

        /// <summary>
        /// Re-derives subject, sender, recipients and (for bodies the integrity check compares) the text
        /// body from the original's own parse, exactly as IntegrityService expects them.
        /// </summary>
        private static bool TryRederiveIndexFields(ArchivedEmail email, RawMessageCapture capture, ILogger logger, string? subject)
        {
            try
            {
                using var stream = new MemoryStream(capture.Bytes, writable: false);
                var message = MimeMessage.Load(ParserOptions.Default, stream);

                var f = LeanIndexFields.Derive(message);
                email.Subject = f.Subject;
                email.From = f.From;
                email.To = f.To;
                email.Cc = f.Cc;
                email.Bcc = f.Bcc;

                var (text, _) = LeanBodyExtractor.Extract(message);
                if (!string.IsNullOrEmpty(text) && Encoding.UTF8.GetByteCount(text) < IntegrityService.MaxComparedBodyBytes)
                    email.Body = MailContentHelper.SanitizeLongTokens(text) ?? string.Empty;

                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lean ingest not possible for '{Subject}': the original could not be parsed again; stored in full", subject);
                return false;
            }
        }
    }
}
