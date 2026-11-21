using MimeKit;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// An archived email's original, parsed for display. The bytes behind it were verified
    /// against the recorded SHA-256 by the store before parsing.
    /// </summary>
    public sealed record OriginalMessage(MimeMessage Message, string Sha256, string Source);

    /// <summary>Decoded bytes of one attachment part and their SHA-256.</summary>
    public sealed record OriginalAttachmentContent(byte[] Bytes, string Sha256);

    /// <summary>
    /// Reads the stored original of an email and parses it, so that lean storage can serve
    /// bodies and attachments from the original instead of a second copy in the database.
    /// Additive: nothing calls this until a reader is switched over.
    /// </summary>
    public interface IOriginalMessageReader
    {
        /// <summary>
        /// Loads and parses the original of the email. Returns null when no original is stored.
        /// Throws <see cref="RawMessageIntegrityException"/> when the stored bytes no longer
        /// match the recorded checksum (the store enforces that for the filesystem backend).
        /// </summary>
        Task<OriginalMessage?> ReadAsync(int emailId);
    }

    public sealed class OriginalMessageReader : IOriginalMessageReader
    {
        private readonly IRawMessageStore _store;

        public OriginalMessageReader(IRawMessageStore store)
        {
            _store = store;
        }

        public async Task<OriginalMessage?> ReadAsync(int emailId)
        {
            var content = await _store.ReadAsync(emailId);
            if (content == null) return null;

            using var stream = new MemoryStream(content.Bytes, writable: false);
            var message = await MimeMessage.LoadAsync(ParserOptions.Default, stream);
            return new OriginalMessage(message, content.Sha256, content.Source);
        }

        /// <summary>
        /// Decodes the attachment at <paramref name="partPath"/> (see <see cref="MimePartLocator"/>)
        /// and hashes the decoded bytes. Returns null when the path does not lead to a part with content.
        /// </summary>
        public static async Task<OriginalAttachmentContent?> ReadAttachmentAsync(MimeMessage message, string partPath)
        {
            if (MimePartLocator.Resolve(message, partPath) is not MimePart part || part.Content == null)
            {
                return null;
            }

            using var ms = new MemoryStream();
            await part.Content.DecodeToAsync(ms);
            var bytes = ms.ToArray();
            return new OriginalAttachmentContent(bytes, RawMessageCodec.ComputeSha256(bytes));
        }
    }
}
