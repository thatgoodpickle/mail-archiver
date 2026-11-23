using MailKit;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// Audit finding 19: reading envelopes, sizes and Gmail labels without a body (the pre-check of every batch, the deletion-marker check and
    /// the daily label refresh) is traffic the provider counts. MailKit does not report the bytes of a FETCH, so they are ESTIMATED from what
    /// came back: the text of the envelope and labels plus a fixed overhead for the protocol framing (UID, size, flags, ids). The estimate
    /// is deliberately on the high side; it is only used to feed the daily bandwidth limit, never to decide whether a message is archived.
    /// </summary>
    public static class EnvelopeTraffic
    {
        /// <summary>Framing and fixed fields of one FETCH response line (UID, RFC822.SIZE, INTERNALDATE, X-GM-MSGID, X-GM-THRID, parentheses).</summary>
        public const int PerMessageOverhead = 200;

        public static long Estimate(IMessageSummary s)
        {
            long n = PerMessageOverhead;
            var e = s.Envelope;
            if (e != null)
            {
                n += (e.Subject?.Length ?? 0) + (e.MessageId?.Length ?? 0) + (e.InReplyTo?.Length ?? 0);
                n += AddressChars(e.From) + AddressChars(e.Sender) + AddressChars(e.ReplyTo) + AddressChars(e.To) + AddressChars(e.Cc) + AddressChars(e.Bcc);
            }
            if (s.GMailLabels != null)
                foreach (var l in s.GMailLabels) n += (l?.Length ?? 0) + 3;
            return n;
        }

        public static long Estimate(IEnumerable<IMessageSummary> summaries) => summaries.Sum(Estimate);

        private static int AddressChars(MimeKit.InternetAddressList? list) => list == null ? 0 : list.ToString().Length;
    }
}
