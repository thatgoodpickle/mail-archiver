using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>Fork: the per-account Gmail search filter (for example -label:Marketing). Purely additive; null means the default.</summary>
    public partial class AddGmailSearchFilter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE mail_archiver.""MailAccounts"" ADD COLUMN IF NOT EXISTS ""GmailSearchFilter"" text NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Left in place: the filter is part of what the archive claims to contain.
        }
    }
}
