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
