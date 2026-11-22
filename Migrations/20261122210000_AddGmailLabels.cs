using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Fork (step 5): Gmail labels, Gmail message id and thread id as metadata of an archived email, and when an account's labels were last
    /// refreshed. Additive. The locked-email trigger is extended so a locked email can still have its labels refreshed (they are metadata
    /// about the message, not its content); everything else stays protected.
    /// </summary>
    public partial class AddGmailLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""ArchivedEmails"" ADD COLUMN IF NOT EXISTS ""Labels"" text[] NULL;
                ALTER TABLE mail_archiver.""ArchivedEmails"" ADD COLUMN IF NOT EXISTS ""GmailMessageId"" text NULL;
                ALTER TABLE mail_archiver.""ArchivedEmails"" ADD COLUMN IF NOT EXISTS ""GmailThreadId"" text NULL;
                ALTER TABLE mail_archiver.""ArchivedEmails"" ADD COLUMN IF NOT EXISTS ""LabelsCapturedAt"" timestamp without time zone NULL;
                ALTER TABLE mail_archiver.""MailAccounts"" ADD COLUMN IF NOT EXISTS ""LabelsRefreshedAt"" timestamp without time zone NULL;
            ");
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_ArchivedEmails_Labels"" ON mail_archiver.""ArchivedEmails"" USING GIN (""Labels"");
                CREATE INDEX IF NOT EXISTS ""IX_ArchivedEmails_GmailMessageId"" ON mail_archiver.""ArchivedEmails"" (""MailAccountId"", ""GmailMessageId"") WHERE ""GmailMessageId"" IS NOT NULL;
            ");
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.prevent_locked_email_changes()
                RETURNS TRIGGER AS $$
                DECLARE
                    old_data jsonb;
                    new_data jsonb;
                BEGIN
                    IF OLD.""IsLocked"" = true THEN
                        old_data := to_jsonb(OLD) - 'IsLocked' - 'FolderName' - 'AttachmentText' - 'AttachmentTextAt' - 'Labels' - 'GmailMessageId' - 'GmailThreadId' - 'LabelsCapturedAt';
                        new_data := to_jsonb(NEW) - 'IsLocked' - 'FolderName' - 'AttachmentText' - 'AttachmentTextAt' - 'Labels' - 'GmailMessageId' - 'GmailThreadId' - 'LabelsCapturedAt';
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
                IS 'Blocks any UPDATE of a locked ArchivedEmails row except IsLocked, FolderName and the derived or metadata columns AttachmentText, AttachmentTextAt, Labels, GmailMessageId, GmailThreadId and LabelsCapturedAt.';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Left in place: the labels are part of what was captured.
        }
    }
}
