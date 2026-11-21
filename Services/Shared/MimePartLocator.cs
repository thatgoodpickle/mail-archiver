using MimeKit;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// Addresses a part of a MIME message by its position in the entity tree, independent of
    /// file names (which can be missing, duplicated or generated at archive time) and of which
    /// collector found the part. Used by lean storage to cut an attachment back out of the
    /// stored original.
    /// <para>
    /// Path grammar: <c>b</c> is the message body; <c>/c{n}</c> descends into child n of a
    /// multipart; <c>/m</c> descends into the body of an embedded message/rfc822 part.
    /// Example: <c>b/c1/m/c0</c>.
    /// </para>
    /// </summary>
    public static class MimePartLocator
    {
        public const string Root = "b";

        /// <summary>Returns the path of <paramref name="target"/> in the message, or null if it is not part of it.</summary>
        public static string? GetPath(MimeMessage message, MimeEntity target)
        {
            if (message.Body == null) return null;
            return Find(message.Body, target, Root);
        }

        private static string? Find(MimeEntity current, MimeEntity target, string path)
        {
            if (ReferenceEquals(current, target)) return path;

            if (current is Multipart multipart)
            {
                for (var i = 0; i < multipart.Count; i++)
                {
                    var found = Find(multipart[i], target, $"{path}/c{i}");
                    if (found != null) return found;
                }
            }
            else if (current is MessagePart messagePart && messagePart.Message?.Body != null)
            {
                return Find(messagePart.Message.Body, target, $"{path}/m");
            }

            return null;
        }

        /// <summary>Every entity of the message with its path, depth first (the order used when locating parts).</summary>
        public static IEnumerable<(string Path, MimeEntity Entity)> Enumerate(MimeMessage message)
        {
            if (message.Body == null) yield break;
            foreach (var item in EnumerateFrom(message.Body, Root)) yield return item;
        }

        private static IEnumerable<(string Path, MimeEntity Entity)> EnumerateFrom(MimeEntity entity, string path)
        {
            yield return (path, entity);

            if (entity is Multipart multipart)
            {
                for (var i = 0; i < multipart.Count; i++)
                    foreach (var item in EnumerateFrom(multipart[i], $"{path}/c{i}")) yield return item;
            }
            else if (entity is MessagePart messagePart && messagePart.Message?.Body != null)
            {
                foreach (var item in EnumerateFrom(messagePart.Message.Body, $"{path}/m")) yield return item;
            }
        }

        /// <summary>Returns the entity at <paramref name="path"/>, or null if the path does not exist in this message.</summary>
        public static MimeEntity? Resolve(MimeMessage message, string path)
        {
            if (message.Body == null || string.IsNullOrEmpty(path)) return null;

            var segments = path.Split('/');
            if (segments[0] != Root) return null;

            MimeEntity current = message.Body;
            for (var i = 1; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment == "m" && current is MessagePart messagePart && messagePart.Message?.Body != null)
                {
                    current = messagePart.Message.Body;
                }
                else if (segment.Length > 1 && segment[0] == 'c'
                         && int.TryParse(segment.AsSpan(1), out var index)
                         && current is Multipart multipart
                         && index >= 0 && index < multipart.Count)
                {
                    current = multipart[index];
                }
                else
                {
                    return null;
                }
            }

            return current;
        }
    }
}
