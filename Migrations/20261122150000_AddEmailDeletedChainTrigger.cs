using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Review finding F-05: every removal of an email is recorded in the audit chain. A BEFORE DELETE trigger on
    /// ArchivedEmails appends an 'email.deleted' entry (email id, SHA-256 of its stored original, account, who
    /// the database session was, why if the session said so) to AuditChain IN THE SAME TRANSACTION as the delete,
    /// under the same advisory lock the application's chain writer uses, with the same hash formula, so it covers
    /// every deletion path (UI, API, jobs, retention, account deletion, raw SQL) and an email cannot be removed
    /// without its chain entry. If the delete is rolled back (for example by the locked-email trigger) so is the
    /// entry. Purely additive.
    /// </summary>
    public partial class AddEmailDeletedChainTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.audit_chain_email_deleted()
                RETURNS trigger AS $fn$
                DECLARE
                    last_seq bigint;
                    last_hash text;
                    new_seq bigint;
                    ts timestamp;
                    sha text;
                    kind text := 'email.deleted';
                    payload text;
                    entry_hash text;
                BEGIN
                    IF to_regclass('mail_archiver.""AuditChain""') IS NULL THEN
                        RETURN OLD;
                    END IF;

                    -- Same key as AuditChain.AppendAsync: one linear chain across the application and this trigger.
                    PERFORM pg_advisory_xact_lock(7283640101);

                    SELECT ""Seq"", ""EntryHash"" INTO last_seq, last_hash
                    FROM mail_archiver.""AuditChain"" ORDER BY ""Seq"" DESC LIMIT 1;

                    new_seq := COALESCE(last_seq, 0) + 1;
                    last_hash := COALESCE(last_hash, repeat('0', 64));
                    ts := (clock_timestamp() AT TIME ZONE 'UTC');

                    SELECT r.""Sha256"" INTO sha FROM mail_archiver.""RawMessages"" r WHERE r.""ArchivedEmailId"" = OLD.""Id"";

                    payload := jsonb_build_object(
                        'accountId', OLD.""MailAccountId"",
                        'lean', OLD.""ContentStripped"",
                        'dbUser', current_user::text,
                        'application', current_setting('application_name', true),
                        'reason', COALESCE(NULLIF(current_setting('mailarchiver.delete_reason', true), ''), 'unspecified'),
                        'txid', txid_current()
                    )::text;

                    -- The same text the application hashes: Seq|Timestamp(us)|Kind|EmailId|Sha256|PrevHash|Payload
                    entry_hash := encode(sha256(convert_to(
                        new_seq::text || '|' || to_char(ts, 'YYYY-MM-DD""T""HH24:MI:SS.US') || '|' || kind || '|' ||
                        OLD.""Id""::text || '|' || COALESCE(sha, '') || '|' || last_hash || '|' || payload, 'UTF8')), 'hex');

                    INSERT INTO mail_archiver.""AuditChain"" (""Seq"", ""Timestamp"", ""Kind"", ""EmailId"", ""Sha256"", ""Payload"", ""PrevHash"", ""EntryHash"")
                    VALUES (new_seq, ts, kind, OLD.""Id"", sha, payload, last_hash, entry_hash);

                    RETURN OLD;
                END;
                $fn$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS audit_chain_email_deleted ON mail_archiver.""ArchivedEmails"";
                CREATE TRIGGER audit_chain_email_deleted
                    BEFORE DELETE ON mail_archiver.""ArchivedEmails""
                    FOR EACH ROW EXECUTE FUNCTION mail_archiver.audit_chain_email_deleted();
            ");

            migrationBuilder.Sql(@"
                COMMENT ON FUNCTION mail_archiver.audit_chain_email_deleted() IS
                    'Appends an email.deleted entry to the hash chain in the same transaction as the delete.';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS audit_chain_email_deleted ON mail_archiver.""ArchivedEmails"";");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS mail_archiver.audit_chain_email_deleted();");
        }
    }
}
