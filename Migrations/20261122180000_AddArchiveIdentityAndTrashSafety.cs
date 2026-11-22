using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Second review N-01 (fork): the Trash becomes a garbage collector that needs proof. Adds the archive identity
    /// (one random id, also kept as a marker file in the originals folder), an index so "is there a delete request for
    /// this original hash?" is a lookup, and two columns on the Trash runs (files without a delete request, and why a run
    /// was refused). Purely additive.
    /// </summary>
    public partial class AddArchiveIdentityAndTrashSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS mail_archiver.""ArchiveIdentity"" (
                    ""Id"" integer NOT NULL PRIMARY KEY,
                    ""ArchiveId"" text NOT NULL,
                    ""CreatedAt"" timestamp without time zone NOT NULL
                );
            ");
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_AuditChain_Sha256"" ON mail_archiver.""AuditChain"" (""Sha256"") WHERE ""Sha256"" IS NOT NULL;
            ");
            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""OriginalCleanupRuns"" ADD COLUMN IF NOT EXISTS ""Unclaimed"" integer NOT NULL DEFAULT 0;
                ALTER TABLE mail_archiver.""OriginalCleanupRuns"" ADD COLUMN IF NOT EXISTS ""Alarm"" text NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Left in place on purpose: the identity must not be lost by a rollback, and the extra columns and index are harmless.
        }
    }
}
