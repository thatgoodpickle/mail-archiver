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
    }
}
