namespace MailArchiver.Models
{
    /// <summary>
    /// Fork option ("Storage:Mode"): <c>Standard</c> (default, upstream behaviour) or <c>Lean</c>.
    /// In lean mode a new email is archived from its original: the original is written (named by
    /// its SHA-256), read back and verified, and the database keeps only the search text, the list
    /// fields and, per attachment, its name, size, MIME part path and SHA-256. The HTML body, the
    /// untruncated body variants and the attachment bytes live only in the original and are read
    /// from it on demand. See doc/INTEGRITY-DESIGN.md. An email that fails any step of the check is
    /// stored in full, the upstream way, and logged: nothing is ever dropped without proof.
    /// </summary>
    public class StorageModeOptions
    {
        public const string Section = "Storage";
        public const string Standard = "Standard";
        public const string Lean = "Lean";

        public string Mode { get; set; } = Standard;

        public bool IsLean => string.Equals(Mode, Lean, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// mbox files hold re-serialized copies of the messages, not the bytes the server delivered, so
        /// they are not an audit source. In lean mode, or when RawMessages:Required is set, an mbox import
        /// is therefore refused unless this is true; the originals it stores are then labelled
        /// <c>serialized</c>, exactly as upstream labels them. Default false.
        /// </summary>
        public bool AllowSerializedImports { get; set; } = false;

        /// <summary>
        /// Review finding F-07: moving originals from the filesystem back into the database is refused while
        /// hash-named files are in use or lean mode is on (lean emails exist only as their original, and
        /// byte-identical originals share one file). Set this to true to run the migration anyway; shared files
        /// are then still never deleted while another row refers to them. Default false.
        /// </summary>
        public bool AllowHashNamedMigration { get; set; } = false;

        /// <summary>
        /// The message to show when an mbox import must not start, or null when it may. Used by the web
        /// form, the import job and the command-line import, so they all say the same thing.
        /// </summary>
        public string? MboxImportRefusal(RawMessageOptions raw)
        {
            if (AllowSerializedImports || !(IsLean || raw.Required)) return null;
            var why = IsLean ? "Storage:Mode=Lean" : "RawMessages:Required=true";
            return $"mbox import is refused because {why} is set: an mbox file holds re-serialized copies of the " +
                   "messages, not the original bytes, so it cannot serve as an audit source. Import the original " +
                   ".eml files instead, or set Storage:AllowSerializedImports=true to import mbox anyway " +
                   "(the stored originals are then labelled 'serialized').";
        }

        /// <summary>
        /// What lean mode needs from the original-message settings. Empty when the combination is
        /// valid (or the mode is not lean). The application refuses to start otherwise, because
        /// lean mode with a missing original would mean emails without their content.
        /// </summary>
        public IReadOnlyList<string> Validate(RawMessageOptions raw)
        {
            var problems = new List<string>();

            // A typo must not silently mean "standard": an unknown mode is refused outright.
            if (!IsLean && !string.Equals(Mode, Standard, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Storage:Mode must be '{Standard}' or '{Lean}', not '{Mode}'.");
            if (!IsLean) return problems;

            if (!raw.Enabled)
                problems.Add("Storage:Mode=Lean requires RawMessages:Enabled=true.");
            if (!raw.WritesToFileSystem)
                problems.Add("Storage:Mode=Lean requires RawMessages:Storage=filesystem.");
            if (!raw.FileSystem.HashNamed)
                problems.Add("Storage:Mode=Lean requires RawMessages:FileSystem:HashNamed=true.");
            if (raw.MaxMessageSizeMb != 0)
                problems.Add("Storage:Mode=Lean requires RawMessages:MaxMessageSizeMb=0 (no size limit), otherwise large emails would be archived without an original.");
            return problems;
        }
    }
}
