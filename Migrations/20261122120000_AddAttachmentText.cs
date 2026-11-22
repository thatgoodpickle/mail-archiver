using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Attachment text for search (fork). Adds the AttachmentTexts table, two derived columns on
    /// ArchivedEmails, and keeps two existing protections working:
    ///  - the locked-email trigger compares the whole row, so it now ignores the two derived columns
    ///    (otherwise the worker could not fill them on a locked email; the email's own content is unchanged);
    ///  - the full-text GIN index is rebuilt on the longer search expression (EmailFilterSql), because an
    ///    index is only used when the query repeats its expression exactly.
    /// </summary>
    public partial class AddAttachmentText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS mail_archiver.""AttachmentTexts"" (
                    ""Sha256"" text NOT NULL PRIMARY KEY,
                    ""Text"" text NOT NULL,
                    ""Status"" text NOT NULL,
                    ""Method"" text NOT NULL,
                    ""Engine"" text NOT NULL,
                    ""EngineVersion"" text NOT NULL,
                    ""ExtractedAt"" timestamp without time zone NOT NULL,
                    ""Error"" text NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_AttachmentTexts_Status"" ON mail_archiver.""AttachmentTexts"" (""Status"");
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""ArchivedEmails"" ADD COLUMN IF NOT EXISTS ""AttachmentText"" text NULL;
                ALTER TABLE mail_archiver.""ArchivedEmails"" ADD COLUMN IF NOT EXISTS ""AttachmentTextAt"" timestamp without time zone NULL;
            ");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.prevent_locked_email_changes()
                RETURNS TRIGGER AS $$
                DECLARE
                    old_data jsonb;
                    new_data jsonb;
                BEGIN
                    IF OLD.""IsLocked"" = true THEN
                        old_data := to_jsonb(OLD) - 'IsLocked' - 'FolderName' - 'AttachmentText' - 'AttachmentTextAt';
                        new_data := to_jsonb(NEW) - 'IsLocked' - 'FolderName' - 'AttachmentText' - 'AttachmentTextAt';
                        IF old_data IS DISTINCT FROM new_data THEN
                            RAISE EXCEPTION 'Email is locked and cannot be modified (compliance requirement)';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql(@"
                COMMENT ON FUNCTION mail_archiver.prevent_locked_email_changes()
                IS 'Blocks any UPDATE of a locked ArchivedEmails row except IsLocked, FolderName and the derived search columns AttachmentText and AttachmentTextAt.';
            ");

            // Same expression as EmailFilterSql (index and query must match to be used), plus the attachment text.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""idx_archivedemails_fulltext_search_v2""
                ON mail_archiver.""ArchivedEmails""
                USING GIN (to_tsvector('simple',
                    COALESCE(""Subject"", '') || ' ' ||
                    COALESCE(""Body"", '') || ' ' ||
                    COALESCE(""From"", '') || ' ' ||
                    COALESCE(""To"", '') || ' ' ||
                    COALESCE(""Cc"", '') || ' ' ||
                    COALESCE(""Bcc"", '') || ' ' ||
                    COALESCE(""AttachmentText"", '')));
                DROP INDEX IF EXISTS mail_archiver.""idx_archivedemails_fulltext_search"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""idx_archivedemails_fulltext_search""
                ON mail_archiver.""ArchivedEmails""
                USING GIN (to_tsvector('simple',
                    COALESCE(""Subject"", '') || ' ' ||
                    COALESCE(""Body"", '') || ' ' ||
                    COALESCE(""From"", '') || ' ' ||
                    COALESCE(""To"", '') || ' ' ||
                    COALESCE(""Cc"", '') || ' ' ||
                    COALESCE(""Bcc"", '')));
                DROP INDEX IF EXISTS mail_archiver.""idx_archivedemails_fulltext_search_v2"";
            ");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.prevent_locked_email_changes()
                RETURNS TRIGGER AS $$
                DECLARE
                    old_data jsonb;
                    new_data jsonb;
                BEGIN
                    IF OLD.""IsLocked"" = true THEN
                        old_data := to_jsonb(OLD) - 'IsLocked' - 'FolderName';
                        new_data := to_jsonb(NEW) - 'IsLocked' - 'FolderName';
                        IF old_data IS DISTINCT FROM new_data THEN
                            RAISE EXCEPTION 'Email is locked and cannot be modified (compliance requirement)';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""ArchivedEmails"" DROP COLUMN IF EXISTS ""AttachmentTextAt"";
                ALTER TABLE mail_archiver.""ArchivedEmails"" DROP COLUMN IF EXISTS ""AttachmentText"";
                DROP TABLE IF EXISTS mail_archiver.""AttachmentTexts"";
            ");
        }
    }
}
