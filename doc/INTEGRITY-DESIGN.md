# Integrity fork: design (draft for approval)

Branch `integrity` of the fork of s1t5/mail-archiver (GPL-3.0). Goal: an archive whose
originals are kept byte-exact, can be restored to a mailbox, and whose integrity can be
proven at any time. Mbox import is out of scope (its copy is re-serialized, see
`RawMessageSource.Serialized`); capture comes from IMAP, Graph and EML files.

## What already exists upstream (verified in the code)

- `RawMessage` row per email: gzip bytes (Postgres or file), `Sha256`, `Size`, `Source`.
  Written when `RawMessages:Enabled` is set; does not backfill.
- Capture sites: `ImapMailSyncService` (2), `GraphMailArchiver`, `EmlImportService`,
  `MBoxImportService` (serialized). All flow into `EmailCoreService` (~line 1667).
- Checksum is verified on read for the filesystem backend (`VerifyChecksumOnRead`), on
  storage migration, and before export/restore (refused on mismatch).
- Restore already appends the stored original through the provider client.
- `ArchivedEmail.ContentHash`, `HashCreatedAt`, `IsLocked` are mapped in the model and
  DbContext but never populated or read (dead "compliance fields").
- `AccessLog` + `AccessLogService`; read-only REST API and MCP server.

## Phase 1: make the original mandatory, prove it

1. `RawMessages:Required` (default false, so upstream behaviour is unchanged): when true,
   an email without a captured original is not archived (logged and counted), instead of
   silently stored without one.
2. `IIntegrityService.VerifyAsync(emailId)`: loads the original through the existing
   store router, re-hashes, compares with `RawMessage.Sha256`. Result: Valid, Invalid,
   Missing (file/row gone), NotStored (no original), Reconstructed.
3. Email detail page: "Integrity report" panel with that result (same idea as Open
   Archiver's badge), plus source (imap/graph/eml-file) and hash.
4. API + MCP: `GET /api/v1/emails/{id}/integrity` and an `get_email_integrity` tool.
5. Verify-all background job: walks all RawMessage rows in batches, records a run
   (started, finished, counts, list of failures) in a new table `IntegrityRuns` /
   `IntegrityFindings`; on demand from the admin UI and on a configurable schedule.

## Phase 2: tamper-evident audit trail

- Extend `AccessLog` (or a new `AuditChain` table) with `PrevHash` and `EntryHash`
  (SHA-256 over the previous hash plus the canonical entry). Written under a lock so the
  chain is linear. `Verify` walks the chain and reports the first break.
- Export a signed manifest (hash of every original + chain head) to a path outside the
  database disk (NAS), so a database edit cannot also rewrite the manifest.

## Phase 3 (optional): lean storage

- A setting that stops storing a second copy of bodies/attachments: the database keeps
  metadata, search text and attachment names/sizes/hashes; attachment downloads are cut
  from the original on demand. Costs attachment de-duplication; brings storage to ~1x.
  Needs a backfill for older rows. Behind a flag so upstream merges stay possible.

## Conventions

- New code in new files; one migration per schema change, named
  `YYYYMMDDHHMMSS_<Feature>.cs`, raw SQL in `DO $$` blocks, schema-qualified names (see
  `AGENTS.md`).
- Tests in `tests/MailArchiver.Tests` (xUnit). Build and test inside the .NET SDK
  container; nothing is installed on the host.
- Keep `main` equal to upstream; all work on `integrity`; rebase onto upstream regularly.
  Generic parts (integrity panel, verify job) are candidates for upstream pull requests.

## Open decisions

1. Phase 3 (lean mode): wanted or not?
2. Where the signed manifest goes (NAS path) and how it is signed (key kept off the VM).
3. Whether `ContentHash` (currently unused) should become a hash of the parsed content,
   or be removed in favour of `RawMessage.Sha256`.
