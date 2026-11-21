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
   Missing (file/row gone), NotStored (no original), Reconstructed, Inconsistent (see 2b).
2b. **Parsed-vs-original consistency check.** The UI, search, REST API and MCP serve the
   *parsed* copy (bodies, headers, attachment list), but the SHA-256 covers only the
   original. Without this check, someone editing `Body`/`HtmlBody` in Postgres would
   still see "Valid". The verify service therefore re-parses the stored original with
   MimeKit and compares it with the stored fields (subject, from/to/cc/bcc, dates,
   text and HTML bodies, attachment names/sizes) and hashes each attachment MIME part
   against the linked `AttachmentContent.Hash`. Any difference => **Inconsistent**.
   This is what makes the parsed copy provably derived from the original.
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

## Phase 3: single-copy ("lean") storage (DECIDED 2026-11-21, reverses the earlier drop)

Reason: the user does not want to store the email twice (original plus parsed copy in
Postgres). Behind a flag (`Storage:Mode=Lean`) so upstream merges stay possible.

- **Original:** one gzipped file per email on the filesystem (ext4, bind mount), SHA-256 of
  the UNCOMPRESSED bytes in the database. It is the only place bodies and attachment bytes
  are stored. Verify recipe for an auditor, no app needed: `gunzip -c <file> | sha256sum`.
  Plain `.eml` export for any message or date range.
- **Database = index cards only:** message id, folder, dates, addresses, subject, size,
  file path, hash, a short preview, and per attachment: name, size, hash (no bytes).
  Search = Postgres full-text word index built from subject, addresses and body text; no
  stored body text. The index is derived and can be rebuilt from the originals.
- **Showing an email / downloading an attachment:** decompress the one original in memory
  and cut the part out; small cache of recently opened items, no permanent second copy.
  Lists and search never open files.
- **Integrity consequence:** with no parsed copy left, the parsed-vs-original check (2b)
  only needs to compare the rebuilt index with the originals during verify-all.
- **De-dup:** message level (Message-ID, per account, as upstream). Attachment de-dup is
  given up in this stage: the same attachment in N emails is stored N times.
- **Attachment text / OCR (later, optional):** a separate asynchronous job extracts text
  (PDF/Word first, OCR after) and adds it to the search index keyed by attachment hash.
  Never blocks archiving; the text is derived and rebuildable. Intended OCR engine: a GPU
  service (PaddleOCR/docTR) using the T1000 8 GB passed through to the clio VM; Tesseract
  (Tika) has no GPU mode. Needs IOMMU/VFIO on Hades, driver + container toolkit in the VM,
  a backup/restore test of the VM with passthrough.
- **Later option: skeleton layer** to regain attachment de-dup: store the original with
  attachment bodies replaced by hash references into a content-addressed store; reassemble
  byte-exact; verify the reassembled SHA-256 at save time and fall back to whole storage on
  mismatch. Risks: imperfect reassembly of odd MIME, shared file damage hits many emails,
  deletion bugs, backup coupling (skeletons and attachment store together), harder audit
  story. Only worth it if measured attachment duplication is large.
- **Measure before deciding more:** after the first sync compare `du` of the originals with
  the Postgres size (index cost); thao import in Open Archiver is a comparable data point.

## Conventions

- New code in new files; one migration per schema change, named
  `YYYYMMDDHHMMSS_<Feature>.cs`, raw SQL in `DO $$` blocks, schema-qualified names (see
  `AGENTS.md`).
- Tests in `tests/MailArchiver.Tests` (xUnit). Build and test inside the .NET SDK
  container; nothing is installed on the host.
- Keep `main` equal to upstream; all work on `integrity`; rebase onto upstream regularly.
  Generic parts (integrity panel, verify job) are candidates for upstream pull requests.

## Decisions (2026-11-21)

1. Single-copy lean storage: YES (see Phase 3); attachment de-dup deferred (skeleton layer is a later option); OCR later on GPU.
2. Signed manifest: stored on the same drive as the archive. Known limit: no off-VM trust
   boundary (mitigations: PBS snapshots keep older manifests; copy the chain-head hash
   elsewhere now and then). Signing key location still open.
3. `ContentHash`/`HashCreatedAt`/`IsLocked`: left alone.
4. Source of mail: Hestia deleted received mail after forwarding, so Gmail All Mail over
   IMAP is the only source. The audit claim must therefore read "byte-exact as held by
   Gmail at capture time", not "as originally delivered" (it includes the forwarding
   headers added by Hestia and Google).

## Operational rules for the syncs

- `RawMessages:Enabled=true` BEFORE the first sync (it does not backfill), storage
  `filesystem` on a bind-mounted path.
- Upstream has no "Required" switch, so after each sync check without reading content:
  emails with no RawMessages row, and RawMessages counts grouped by Source (expect
  `imap`; recovery fetches may show `serialized`).
