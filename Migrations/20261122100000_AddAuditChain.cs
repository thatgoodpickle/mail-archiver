using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <summary>
    /// Hash-chained audit trail (see doc/INTEGRITY-DESIGN.md, Phase 2). Purely additive. A trigger
    /// refuses UPDATE, DELETE and TRUNCATE on the table so entries can only be appended.
    /// </summary>
    public partial class AddAuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS mail_archiver.""AuditChain"" (
                    ""Seq"" bigint NOT NULL PRIMARY KEY,
                    ""Timestamp"" timestamp without time zone NOT NULL,
                    ""Kind"" text NOT NULL,
                    ""EmailId"" integer NULL,
                    ""Sha256"" text NULL,
                    ""Payload"" text NOT NULL,
                    ""PrevHash"" text NOT NULL,
                    ""EntryHash"" text NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_AuditChain_EmailId"" ON mail_archiver.""AuditChain"" (""EmailId"");
            ");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION mail_archiver.audit_chain_append_only()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'AuditChain is append-only (% refused)', TG_OP;
                END;
                $$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS audit_chain_no_change ON mail_archiver.""AuditChain"";
                CREATE TRIGGER audit_chain_no_change
                    BEFORE UPDATE OR DELETE ON mail_archiver.""AuditChain""
                    FOR EACH ROW EXECUTE FUNCTION mail_archiver.audit_chain_append_only();
                DROP TRIGGER IF EXISTS audit_chain_no_truncate ON mail_archiver.""AuditChain"";
                CREATE TRIGGER audit_chain_no_truncate
                    BEFORE TRUNCATE ON mail_archiver.""AuditChain""
                    FOR EACH STATEMENT EXECUTE FUNCTION mail_archiver.audit_chain_append_only();
            ");

            migrationBuilder.Sql(@"
                COMMENT ON TABLE mail_archiver.""AuditChain"" IS
                    'Append-only hash chain: EntryHash = SHA-256 over the entry fields and PrevHash.';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS mail_archiver.""AuditChain"";");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS mail_archiver.audit_chain_append_only();");
        }
    }
}
