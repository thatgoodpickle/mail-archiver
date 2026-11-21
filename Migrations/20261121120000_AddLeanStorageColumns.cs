using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Columns for lean storage (see doc/INTEGRITY-DESIGN.md): a flag on the email saying that
    /// its HTML body, untruncated body variants and attachment bytes live only in the stored
    /// original, and per attachment the MIME part path and the SHA-256 of the decoded bytes.
    /// Purely additive; existing rows keep ContentStripped = false and behave as before.
    /// </summary>
    public partial class AddLeanStorageColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                          AND table_name = 'ArchivedEmails'
                          AND column_name = 'ContentStripped'
                    ) THEN
                        ALTER TABLE mail_archiver.""ArchivedEmails""
                            ADD COLUMN ""ContentStripped"" boolean NOT NULL DEFAULT false;
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                          AND table_name = 'EmailAttachments'
                          AND column_name = 'PartPath'
                    ) THEN
                        ALTER TABLE mail_archiver.""EmailAttachments"" ADD COLUMN ""PartPath"" text NULL;
                    END IF;
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                          AND table_name = 'EmailAttachments'
                          AND column_name = 'Sha256'
                    ) THEN
                        ALTER TABLE mail_archiver.""EmailAttachments"" ADD COLUMN ""Sha256"" text NULL;
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                COMMENT ON COLUMN mail_archiver.""ArchivedEmails"".""ContentStripped"" IS
                    'Lean storage: true when the HTML body, untruncated body variants and attachment bytes are not kept in the database and are read from the stored original.';
                COMMENT ON COLUMN mail_archiver.""EmailAttachments"".""PartPath"" IS
                    'MIME part path of the attachment inside the stored original (e.g. b/c1/m/c0). Set for lean-stored emails.';
                COMMENT ON COLUMN mail_archiver.""EmailAttachments"".""Sha256"" IS
                    'Lowercase hex SHA-256 of the decoded attachment bytes, recorded at archive time and checked when the part is read from the original.';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""EmailAttachments"" DROP COLUMN IF EXISTS ""Sha256"";
                ALTER TABLE mail_archiver.""EmailAttachments"" DROP COLUMN IF EXISTS ""PartPath"";
                ALTER TABLE mail_archiver.""ArchivedEmails"" DROP COLUMN IF EXISTS ""ContentStripped"";
            ");
        }
    }
}
