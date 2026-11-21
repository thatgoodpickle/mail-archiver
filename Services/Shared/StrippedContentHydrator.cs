using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// A lean-stored email (<see cref="ArchivedEmail.ContentStripped"/>) keeps its content only in
    /// the original, and the original is missing from the store, so nothing can vouch for what the
    /// email contained. Deliberately an exception, never an empty page or a hollow rebuild.
    /// </summary>
    public class LeanOriginalMissingException : Exception
    {
        public LeanOriginalMissingException(int emailId)
            : base($"Email {emailId} is stored lean (its content lives only in the original), but no original is stored for it.")
        {
            EmailId = emailId;
        }

        public int EmailId { get; }
    }

    /// <summary>
    /// An attachment cut out of the stored original does not match the hash recorded when the
    /// email was archived, or its part is no longer where it was.
    /// </summary>
    public class LeanAttachmentIntegrityException : Exception
    {
        public LeanAttachmentIntegrityException(int emailId, int attachmentId, string detail)
            : base($"Attachment {attachmentId} of email {emailId} cannot be read from the stored original: {detail}")
        {
            EmailId = emailId;
            AttachmentId = attachmentId;
        }

        public int EmailId { get; }
        public int AttachmentId { get; }
    }

    /// <summary>
    /// Fills in the content of lean-stored emails from their original, so that every reader
    /// (pages, exports, restores, API) can keep working with an ordinary full-looking email.
    /// <para>
    /// The hydrated entity is detached from the database context and flagged
    /// <c>ContentStripped = false</c> in memory only: a later SaveChanges on the same context can
    /// never write the filled-in bodies or attachment bytes back to the database.
    /// ArchivedEmailMimeBuilder refuses an entity that is still flagged as stripped, so a reader
    /// that forgot to hydrate fails loudly instead of producing a hollow message.
    /// </para>
    /// </summary>
    public interface IStrippedContentHydrator
    {
        /// <summary>
        /// No-op for emails that are not lean-stored. Otherwise detaches the email and its
        /// attachments, loads the original (throws <see cref="LeanOriginalMissingException"/> when
        /// it is gone) and restores the HTML body, the full text/HTML bodies and the bytes of
        /// every attachment accepted by <paramref name="attachmentFilter"/> (all when null).
        /// </summary>
        Task HydrateAsync(ArchivedEmail email, Func<EmailAttachment, bool>? attachmentFilter = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// For a single attachment of a lean-stored email: cuts the bytes out of the original and
        /// verifies them against the recorded SHA-256. No-op when the email is not lean-stored.
        /// </summary>
        Task HydrateAttachmentAsync(EmailAttachment attachment, CancellationToken cancellationToken = default);
    }

    public sealed class StrippedContentHydrator : IStrippedContentHydrator
    {
        private readonly MailArchiverDbContext _context;
        private readonly IOriginalMessageReader _reader;

        public StrippedContentHydrator(MailArchiverDbContext context, IOriginalMessageReader reader)
        {
            _context = context;
            _reader = reader;
        }

        public async Task HydrateAsync(ArchivedEmail email, Func<EmailAttachment, bool>? attachmentFilter = null,
            CancellationToken cancellationToken = default)
        {
            if (!email.ContentStripped) return;

            // Detach first: from here on the entity is a private in-memory copy.
            var attachments = email.Attachments.ToList();
            _context.Entry(email).State = EntityState.Detached;
            foreach (var a in attachments) _context.Entry(a).State = EntityState.Detached;

            var original = await _reader.ReadAsync(email.Id) ?? throw new LeanOriginalMissingException(email.Id);
            var message = original.Message;

            var (text, html) = LeanBodyExtractor.Extract(message);
            email.HtmlBody = html ?? string.Empty;
            email.BodyUntruncatedHtml = null;
            email.BodyUntruncatedText = null;
            email.OriginalBodyHtml = string.IsNullOrEmpty(html) ? null : Encoding.UTF8.GetBytes(html);
            email.OriginalBodyText = string.IsNullOrEmpty(text) ? null : Encoding.UTF8.GetBytes(text);

            foreach (var attachment in attachments)
            {
                if (attachmentFilter != null && !attachmentFilter(attachment)) continue;
                await FillAttachmentAsync(email.Id, attachment, message);
            }

            // Only now does the email look complete to the readers; the flag is never saved.
            email.ContentStripped = false;
        }

        public async Task HydrateAttachmentAsync(EmailAttachment attachment, CancellationToken cancellationToken = default)
        {
            var stripped = attachment.ArchivedEmail?.ContentStripped
                ?? await _context.ArchivedEmails.AsNoTracking()
                    .Where(e => e.Id == attachment.ArchivedEmailId)
                    .Select(e => e.ContentStripped)
                    .FirstOrDefaultAsync(cancellationToken);
            if (!stripped) return;

            _context.Entry(attachment).State = EntityState.Detached;

            var original = await _reader.ReadAsync(attachment.ArchivedEmailId)
                ?? throw new LeanOriginalMissingException(attachment.ArchivedEmailId);
            await FillAttachmentAsync(attachment.ArchivedEmailId, attachment, original.Message);
        }

        private static async Task FillAttachmentAsync(int emailId, EmailAttachment attachment, MimeMessage message)
        {
            if (string.IsNullOrEmpty(attachment.PartPath) || string.IsNullOrEmpty(attachment.Sha256))
            {
                throw new LeanAttachmentIntegrityException(emailId, attachment.Id, "no part path or hash was recorded");
            }

            var content = await OriginalMessageReader.ReadAttachmentAsync(message, attachment.PartPath);
            if (content == null)
            {
                throw new LeanAttachmentIntegrityException(emailId, attachment.Id,
                    $"part '{attachment.PartPath}' does not exist in the original");
            }

            if (!string.Equals(content.Sha256, attachment.Sha256, StringComparison.Ordinal))
            {
                throw new LeanAttachmentIntegrityException(emailId, attachment.Id,
                    $"hash mismatch (recorded {attachment.Sha256}, original has {content.Sha256})");
            }

            attachment.Content = content.Bytes;
        }
    }

    /// <summary>
    /// The text and HTML bodies of a message as the archive stores them, shared by ingest and by
    /// the lean hydrator so both interpret a message the same way.
    /// </summary>
    public static class LeanBodyExtractor
    {
        /// <summary>
        /// Text body (or, when there is none, the HTML turned into text as upstream does) and the
        /// HTML body, both cleaned of characters PostgreSQL text cannot hold. Null when absent.
        /// </summary>
        public static (string? Text, string? Html) Extract(MimeMessage message)
        {
            string? text = null;
            string? html = null;

            if (!string.IsNullOrEmpty(message.HtmlBody))
            {
                html = MailContentHelper.CleanText(message.HtmlBody);
            }

            if (!string.IsNullOrEmpty(message.TextBody))
            {
                text = MailContentHelper.CleanText(message.TextBody);
            }
            else if (!string.IsNullOrEmpty(message.HtmlBody))
            {
                text = MailContentHelper.CleanText(message.HtmlBody);
            }

            return (text, html);
        }
    }
}
