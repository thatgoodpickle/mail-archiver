using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>Delete memory / coverage check: a problem (quarantine) row records the message's Gmail id so the coverage check can match it. Purely additive.</summary>
    public partial class AddSyncProblemGmailId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE mail_archiver.""SyncProblemMessages"" ADD COLUMN IF NOT EXISTS ""GmailMessageId"" text NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE mail_archiver.""SyncProblemMessages"" DROP COLUMN IF EXISTS ""GmailMessageId"";");
        }
    }
}
