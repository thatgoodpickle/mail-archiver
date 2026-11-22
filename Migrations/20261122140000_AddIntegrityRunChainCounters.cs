using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Verify-all counters for the audit chain checks (review findings F-04/F-05): emails whose original hash
    /// differs from the chain, and emails with no chain entry. Purely additive.
    /// </summary>
    public partial class AddIntegrityRunChainCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""IntegrityRuns"" ADD COLUMN IF NOT EXISTS ""ChainMismatch"" integer NOT NULL DEFAULT 0;
                ALTER TABLE mail_archiver.""IntegrityRuns"" ADD COLUMN IF NOT EXISTS ""Unchained"" integer NOT NULL DEFAULT 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Counters of past runs are evidence; they are not dropped by a rollback.
        }
    }
}
