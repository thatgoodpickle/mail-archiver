using MailArchiver.Models;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// Maps an archived email id to the file it is stored in, and resolves that file against
    /// the configured base directory.
    /// <para>
    /// The path is derived from the id alone, never from the subject or any other message
    /// content: no escaping to worry about, no collisions, and the location can be recomputed
    /// for every request without consulting an index. The sharding keeps the number of entries
    /// per directory low on large archives (depth 2 gives 10,000 directories).
    /// </para>
    /// </summary>
    public class RawMessagePathResolver
    {
        private readonly string _basePath;
        private readonly int _shardDepth;

        public RawMessagePathResolver(RawMessageFileSystemOptions options, string contentRoot)
        {
            _basePath = Path.IsPathRooted(options.BasePath)
                ? options.BasePath
                : Path.Combine(contentRoot, options.BasePath);

            _shardDepth = Math.Clamp(options.ShardDepth, 0, 4);
        }

        /// <summary>The absolute base directory message files live under.</summary>
        public string BasePath => _basePath;

        /// <summary>
        /// The path of an email's file relative to <see cref="BasePath"/>, using forward slashes
        /// so the value is stable across platforms.
        /// </summary>
        public string GetRelativePath(int emailId)
        {
            var parts = new List<string>(_shardDepth + 1);
            var remaining = emailId;

            for (var level = 0; level < _shardDepth; level++)
            {
                parts.Add((remaining % 100).ToString("D2"));
                remaining /= 100;
            }

            parts.Add($"{emailId}.eml.gz");
            return string.Join('/', parts);
        }

        /// <summary>
        /// Path of a hash-named original: two directory levels from the first four hex digits,
        /// then <c>&lt;sha256&gt;.eml.gz</c>. Throws for anything that is not a 64-character
        /// lowercase hex SHA-256, so a hash can never be used to build a path outside the base.
        /// </summary>
        public string GetRelativePathForHash(string sha256)
        {
            if (sha256 is not { Length: 64 } || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new ArgumentException("Expected a lowercase hex SHA-256 (64 characters).", nameof(sha256));
            }

            return $"{sha256[..2]}/{sha256.Substring(2, 2)}/{sha256}.eml.gz";
        }

        /// <summary>
        /// Absolute path of an email's file. Throws when the resolved path would leave the base
        /// directory, so a corrupted <c>StoragePath</c> column cannot be used to read elsewhere.
        /// </summary>
        public string GetAbsolutePath(string relativePath)
        {
            var combined = Path.GetFullPath(Path.Combine(_basePath, relativePath));
            var root = Path.GetFullPath(_basePath);

            if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(combined, root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to access '{relativePath}' outside the raw message directory.");
            }

            return combined;
        }

        /// <summary>Absolute path of an email's file.</summary>
        public string GetAbsolutePath(int emailId) => GetAbsolutePath(GetRelativePath(emailId));
    }
}
