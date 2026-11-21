using MailArchiver.Models;
using MailArchiver.Services.Monitoring;
using Microsoft.Extensions.Logging;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// RawMessages:Required (fork): decides, before an email is saved, whether it may be archived
    /// without a stored original. Pure decision plus the shared log/metric line, so every archive
    /// path (IMAP sync, Graph, EML/mbox import) refuses in the same way.
    /// </summary>
    public static class RawMessageRequirement
    {
        /// <summary>
        /// The reason (a <see cref="MetricsReasons"/> value) the email must be refused, or null when
        /// it may be archived. Always null unless <see cref="RawMessageOptions.Required"/> is set.
        /// <paramref name="captureExpected"/> is false for sources that never deliver an original by
        /// configuration (Graph with IncludeGraph=false); Required still refuses those, because the
        /// switch means no email without an original.
        /// </summary>
        public static string? RefusalReason(RawMessageOptions options, RawMessageCapture? capture)
        {
            if (!options.Required) return null;
            if (capture == null || capture.Bytes.Length == 0) return MetricsReasons.NoCapture;
            if (!RawMessageCodec.ShouldStore(capture, options)) return MetricsReasons.Size;
            return null;
        }

        /// <summary>Logs the refusal at error level and counts it. Does not throw.</summary>
        public static void LogRefused(ILogger logger, string? subject, string reason, IMetricsRecorder? metrics)
        {
            metrics?.RawMessageNotStored(reason);
            logger.LogError(
                "Email refused: RawMessages:Required is set and no verified original could be kept ({Reason}). " +
                "Not archived: '{Subject}'. It is tried again on the next sync.",
                reason, subject);
        }

        /// <summary>Startup problems with the setting; empty when valid.</summary>
        public static IReadOnlyList<string> Validate(RawMessageOptions options)
        {
            var problems = new List<string>();
            if (options.Required && !options.Enabled)
                problems.Add("RawMessages:Required=true requires RawMessages:Enabled=true.");
            return problems;
        }
    }
}
