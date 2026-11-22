namespace MailArchiver.Models
{
    /// <summary>
    /// One verify-all pass over the archive (see IntegrityRunner). Counters are updated after each
    /// batch, so a running pass can be watched; <see cref="Status"/> is Running, Completed, Failed
    /// (an unexpected error or an interruption such as a restart) or Cancelled (application stopping).
    /// </summary>
    public class IntegrityRun
    {
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";

        public int Id { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public string Status { get; set; } = Running;

        /// <summary>Emails in the archive when the run started.</summary>
        public int Total { get; set; }
        public int Checked { get; set; }
        public int Valid { get; set; }
        public int Invalid { get; set; }
        public int Missing { get; set; }
        public int NotStored { get; set; }
        public int Inconsistent { get; set; }

        /// <summary>Emails whose original hash differs from the one the audit chain recorded when it was stored.</summary>
        public int ChainMismatch { get; set; }

        /// <summary>Emails with an original but no audit chain entry (stored before the chain existed): informational.</summary>
        public int Unchained { get; set; }

        /// <summary>Emails whose verification itself failed unexpectedly.</summary>
        public int Errors { get; set; }

        /// <summary>Who started the run: a user name, "schedule", or null.</summary>
        public string? StartedBy { get; set; }
        public string? Error { get; set; }

        public virtual ICollection<IntegrityFinding> Findings { get; set; } = new List<IntegrityFinding>();
    }

    /// <summary>
    /// A result of a run other than Valid. The email id is deliberately not a foreign key: a finding
    /// is evidence and must outlive the email it is about.
    /// </summary>
    public class IntegrityFinding
    {
        public int Id { get; set; }
        public int RunId { get; set; }
        public int EmailId { get; set; }

        /// <summary>Invalid, Missing, NotStored, Inconsistent or Error.</summary>
        public string Status { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;

        public virtual IntegrityRun Run { get; set; } = null!;
    }

    /// <summary>Settings of the verify-all job ("Integrity" section).</summary>
    public class IntegrityOptions
    {
        public const string Section = "Integrity";

        /// <summary>Start a verify-all run every this many hours; 0 (default) disables the schedule.</summary>
        public int VerifyAllIntervalHours { get; set; } = 0;

        /// <summary>Delay before the first scheduled run, in minutes, so startup settles first.</summary>
        public int ScheduleStartupDelayMinutes { get; set; } = 10;

        /// <summary>
        /// Keep the hash-chained audit trail (AuditChain table): every access-log entry, every
        /// original stored and every verify-all run is appended with the hash of the previous entry.
        /// On by default; a failed append is logged at error level but never blocks the action.
        /// </summary>
        public bool AuditChainEnabled { get; set; } = true;

        /// <summary>Directory the signed manifests and the signing key are written to. Relative paths resolve against the content root.</summary>
        public string ManifestPath { get; set; } = "Manifests";

        /// <summary>Write a signed manifest every this many hours; 0 (default) disables the schedule (the API still works).</summary>
        public int ManifestIntervalHours { get; set; } = 0;

        /// <summary>
        /// The signing key (PEM, ECDSA P-256). Empty = <c>manifest-signing-key.pem</c> in the DataProtection key
        /// directory (<c>DataProtection:KeyPath</c>, a different volume than the manifests in the shipped compose
        /// file), created on first use with mode 0600. A key on the same host as the archive protects against
        /// mistakes and against anyone without file access or the app's credentials; copy the public key and the
        /// chain head somewhere this host cannot reach if more is needed.
        /// </summary>
        public string ManifestKeyPath { get; set; } = string.Empty;

        /// <summary>
        /// Allow a NEW signing key to be created although signed manifests already exist. Off by default: a silently
        /// regenerated key would make a rewritten manifest set verify. Set it once, deliberately, after losing the key.
        /// </summary>
        public bool AllowNewKey { get; set; } = false;
    }

    /// <summary>
    /// One link of the tamper-evident audit trail. <see cref="EntryHash"/> is the SHA-256 of the
    /// entry's fields and <see cref="PrevHash"/>, so changing or removing any earlier entry breaks
    /// every later hash. <see cref="Seq"/> counts from 1 without gaps. A database trigger refuses
    /// UPDATE, DELETE and TRUNCATE (protection against mistakes and non-owner tampering; the table
    /// owner can still drop the trigger, which is why the chain head is also kept elsewhere).
    /// </summary>
    public class AuditChainEntry
    {
        public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

        public long Seq { get; set; }
        public DateTime Timestamp { get; set; }
        public string Kind { get; set; } = string.Empty;
        public int? EmailId { get; set; }
        public string? Sha256 { get; set; }
        public string Payload { get; set; } = "{}";
        public string PrevHash { get; set; } = GenesisHash;
        public string EntryHash { get; set; } = string.Empty;
    }
}
