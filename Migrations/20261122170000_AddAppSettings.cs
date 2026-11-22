using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Admin-editable settings (Integrity &amp; Storage page). Purely additive; the table stays empty until an admin
    /// changes a setting, and an environment variable always wins over a stored value.
    /// </summary>
    public partial class AddAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS mail_archiver.""AppSettings"" (
                    ""Key"" text NOT NULL PRIMARY KEY,
                    ""Value"" text NOT NULL,
                    ""UpdatedAt"" timestamp without time zone NOT NULL,
                    ""UpdatedBy"" text NULL
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS mail_archiver.""AppSettings"";");
        }
    }
}
