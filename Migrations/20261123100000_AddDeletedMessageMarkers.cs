using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Delete memory (doc/DELETE-MEMORY-SPEC.md): a table of what was deleted ON PURPOSE (no subject, sender or body), and the
    /// email.deleted trigger extended so the DATABASE writes the marker in the same transaction as the delete and its chain entry.
    /// A marker is written only when the deleting code set mailarchiver.delete_reason to 'user' or 'retention' (SET LOCAL); the default
    /// and the reasons 'account' and 'internal' write none, so a forgotten delete path makes mail come back visibly instead of
    /// silently never returning. The marker keeps the chain sequence of its email.deleted entry, and the entry's payload gets a
    /// markerId. The hash formula is unchanged (the application hashes the stored payload text), and entries without a marker keep
    /// exactly the old payload.
    /// </summary>
    public partial class AddDeletedMessageMarkers : Migration
    {
        private const string FunctionSql = @"
                CREATE OR REPLACE FUNCTION mail_archiver.audit_chain_email_deleted()
                RETURNS trigger AS $fn$
                DECLARE
                    last_seq bigint;
                    last_hash text;
                    new_seq bigint;
                    ts timestamp;
                    sha text;
                    msize bigint;
                    kind text := 'email.deleted';
                    reason text;
                    marker_id integer;
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

                    SELECT r.""Sha256"", r.""Size"" INTO sha, msize FROM mail_archiver.""RawMessages"" r WHERE r.""ArchivedEmailId"" = OLD.""Id"";

                    reason := COALESCE(NULLIF(current_setting('mailarchiver.delete_reason', true), ''), 'unspecified');

                    -- Delete memory: only a deliberate deletion leaves a marker. Same transaction, so a rolled-back delete leaves none.
                    IF reason IN ('user', 'retention') THEN
                        INSERT INTO mail_archiver.""DeletedMessageMarkers""
                            (""AccountId"", ""Folder"", ""MessageId"", ""Size"", ""Sha256"", ""GmailMessageId"", ""DeletedAt"", ""ChainSeq"", ""Reason"")
                        VALUES (OLD.""MailAccountId"", COALESCE(OLD.""FolderName"", ''), OLD.""MessageId"", COALESCE(msize, 0), sha, OLD.""GmailMessageId"", ts, new_seq, reason)
                        ON CONFLICT (""AccountId"", ""MessageId"", ""Size"", ""Folder"") DO UPDATE SET
                            ""Sha256"" = EXCLUDED.""Sha256"", ""GmailMessageId"" = EXCLUDED.""GmailMessageId"", ""DeletedAt"" = EXCLUDED.""DeletedAt"",
                            ""ChainSeq"" = EXCLUDED.""ChainSeq"", ""Reason"" = EXCLUDED.""Reason""
                        RETURNING ""Id"" INTO marker_id;
                    END IF;

                    payload := (jsonb_build_object(
                        'accountId', OLD.""MailAccountId"",
                        'lean', OLD.""ContentStripped"",
                        'dbUser', current_user::text,
                        'application', current_setting('application_name', true),
                        'reason', reason,
                        'txid', txid_current()
                    ) || CASE WHEN marker_id IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('markerId', marker_id) END)::text;

                    -- The same text the application hashes: Seq|Timestamp(us)|Kind|EmailId|Sha256|PrevHash|Payload
                    entry_hash := encode(sha256(convert_to(
                        new_seq::text || '|' || to_char(ts, 'YYYY-MM-DD""T""HH24:MI:SS.US') || '|' || kind || '|' ||
                        OLD.""Id""::text || '|' || COALESCE(sha, '') || '|' || last_hash || '|' || payload, 'UTF8')), 'hex');

                    INSERT INTO mail_archiver.""AuditChain"" (""Seq"", ""Timestamp"", ""Kind"", ""EmailId"", ""Sha256"", ""Payload"", ""PrevHash"", ""EntryHash"")
                    VALUES (new_seq, ts, kind, OLD.""Id"", sha, payload, last_hash, entry_hash);

                    RETURN OLD;
                END;
                $fn$ LANGUAGE plpgsql;
            ";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS mail_archiver.""DeletedMessageMarkers"" (
                    ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    ""AccountId"" integer NOT NULL REFERENCES mail_archiver.""MailAccounts"" (""Id"") ON DELETE CASCADE,
                    ""Folder"" text NOT NULL,
                    ""MessageId"" text NOT NULL,
                    ""Size"" bigint NOT NULL,
                    ""Sha256"" text NULL,
                    ""GmailMessageId"" text NULL,
                    ""DeletedAt"" timestamp without time zone NOT NULL,
                    ""ChainSeq"" bigint NOT NULL,
                    ""Reason"" text NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DeletedMessageMarkers_AccountId_MessageId_Size_Folder""
                    ON mail_archiver.""DeletedMessageMarkers"" (""AccountId"", ""MessageId"", ""Size"", ""Folder"");
                CREATE INDEX IF NOT EXISTS ""IX_DeletedMessageMarkers_AccountId_GmailMessageId""
                    ON mail_archiver.""DeletedMessageMarkers"" (""AccountId"", ""GmailMessageId"");
                CREATE INDEX IF NOT EXISTS ""IX_DeletedMessageMarkers_AccountId_Sha256""
                    ON mail_archiver.""DeletedMessageMarkers"" (""AccountId"", ""Sha256"");
                COMMENT ON TABLE mail_archiver.""DeletedMessageMarkers"" IS
                    'Identifiers (no subject, sender or body) of emails deleted on purpose; written by the email.deleted trigger; a sync does not archive them again until released.';
            ");

            migrationBuilder.Sql(FunctionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The trigger goes back to the version without markers; the table (evidence of what was deleted on purpose) is left in place.
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
                    entry_hash := encode(sha256(convert_to(
                        new_seq::text || '|' || to_char(ts, 'YYYY-MM-DD""T""HH24:MI:SS.US') || '|' || kind || '|' ||
                        OLD.""Id""::text || '|' || COALESCE(sha, '') || '|' || last_hash || '|' || payload, 'UTF8')), 'hex');
                    INSERT INTO mail_archiver.""AuditChain"" (""Seq"", ""Timestamp"", ""Kind"", ""EmailId"", ""Sha256"", ""Payload"", ""PrevHash"", ""EntryHash"")
                    VALUES (new_seq, ts, kind, OLD.""Id"", sha, payload, last_hash, entry_hash);
                    RETURN OLD;
                END;
                $fn$ LANGUAGE plpgsql;
            ");
        }
    }
}
