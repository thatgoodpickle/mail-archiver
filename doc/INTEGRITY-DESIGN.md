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
- `ArchivedEmail.ContentHash` and `HashCreatedAt` are mapped in the model and DbContext
  but never populated or read.
- `ArchivedEmail.IsLocked` is NOT dead (corrected 2026-11-21): the DB trigger
  `mail_archiver.prevent_locked_email_changes` (migration 20251105131500) rejects any
  change to a locked row except `IsLocked` and `FolderName`, and `DeletionPolicyApplicationService`
  sets `IsLocked` on every row at startup from the deletion policy (`DeletionAllowed`;
  default column value set in Program.cs). Consequences: (a) it is a built-in write
  protection we can reuse for audit; (b) any job that rewrites or clears the parsed copy of
  an email (the lean-mode conversion) is blocked by the trigger on locked rows, so the
  conversion must be designed around it (unlock, change, relock inside one transaction, or
  a dedicated, logged conversion path), and must never leave rows unlocked afterwards;
  (c) the locked state and the unlock path in MailAccountsController must be reviewed
  before any lean-mode code is written.
- `AccessLog` + `AccessLogService`; read-only REST API and MCP server.

## Stage 0 findings (read from the code, 2026-11-21; nothing was run)

1. **Search is computed from the stored body.** `EmailFilterSql.cs` builds
   `to_tsvector('simple', Subject || Body || From || To || Cc || Bcc)` at query time, backed by
   an expression GIN index (migration 20250821132800), and phrase searches also use
   `POSITION(...)` on `Subject`/`Body`/`From`. It uses plain-text `Body`, not `HtmlBody`.
   Consequence: lean mode cannot simply drop `Body`; the index would index nothing. Lean mode
   needs its own search source (a column or small table filled at ingest from the parsed
   original, with the GIN index on it) and the query code switched to it. Dropping
   `HtmlBody` and attachment bytes is the cheap part.
2. **`IsLocked` is weaker than it looks.** The trigger `prevent_locked_email_changes` only
   rejects changes to `Subject`, `Body`, `From`, `To` and `ContentHash` on a locked row (not
   `HtmlBody`, `Cc`, `Bcc`, dates, folder or attachments; the earlier "any change except
   IsLocked/FolderName" wording was wrong), and a second trigger blocks deleting locked rows.
   But account deletion (`MailAccountsController`, ~line 1422) first sets `IsLocked=false`
   for every email of the account, then deletes them, and the deletion policy can change the
   flag at startup. So it is a guard against accidental edits, not an audit control. It does
   not cover `RawMessages` at all. Lean mode must not rely on it; audit protection comes from
   read-only originals, hashes, the verify job and the hash-chained log. Lean conversion of
   locked rows needs `Body` handled around the trigger.
3. **Auditor recipe (code level):** the filesystem store writes a plain
   `GZipStream` stream (`RawMessageCodec.Compress`, written with `File.WriteAllBytesAsync`),
   and `Sha256` is the SHA-256 of the uncompressed bytes (`ComputeSha256`). So
   `gunzip -c file | sha256sum` should equal the stored value. Still to confirm on a real
   stored file once a test stack runs.
4. **Interface actions that change or remove mail:** `Delete`, `DeleteSelected`,
   `DeleteByFilter` (plus the account-deletion path) in `EmailsController` /
   `MailAccountsController`, and the restore flows (which append to a mailbox). No
   content-edit action was found. The REST API has no write/delete endpoints. Lean mode must
   gate or disable deletion for the audit setup (`ENABLE_DELETION`-style switch plus the
   deletion policy), and restore must keep refusing on a hash mismatch.

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
  Search behaviour (corrected 2026-11-21): upstream uses `to_tsvector('simple', ...)` /
  `to_tsquery('simple', ...)` (EmailFilterSql.cs): whole words and prefixes, no stemming, not
  substring matching. The earlier claim that it finds partial words was wrong. The `simple`
  configuration avoids language-specific stemming (names, Vietnamese, Korean). Open
  question before coding: whether upstream stores the vector or builds it from `Body` at
  query time; if the latter, lean mode needs a stored vector column (tsvector is capped at
  about 1 MB per value, so very long bodies must be truncated for indexing only).
- **What the screen shows:** the email body, headers and attachments always come from the
  original (unpacked and read on demand; HTML sanitised as upstream does, so what is shown is
  the original rendered, never altered; the raw `.eml` is always downloadable). The database
  feeds only search and the list rows (sender, subject, date, size, folder, preview). List
  rows are a convenience, not evidence: a database edit could change a list value without
  touching the original, so verify-all rebuilds every index card from its original and flags
  any difference. Rule: the original is the evidence; the database is a rebuildable lookup
  layer. Backups matter most for the originals, since the database can be rebuilt from them
  but they cannot be rebuilt from the database.
- **Showing an email / downloading an attachment:** decompress the one original in memory
  and cut the part out; small cache of recently opened items, no permanent second copy.
  Lists and search never open files.
- **Integrity consequence:** with no parsed copy left, the parsed-vs-original check (2b)
  only needs to compare the rebuilt index with the originals during verify-all.
- **De-dup:** message level only (Message-ID, per account, as upstream; never across
  accounts). The stored original is never edited: a duplicate arrival is simply not stored.
  Rule: keep the first original and write every skipped duplicate to a log (Message-ID,
  SHA-256 of the skipped copy, time seen), because two copies with one Message-ID can differ
  in headers. Option to keep both copies if a stricter reading is wanted.
  There is no attachment table in lean mode, so no attachment de-dup; de-duplicating inside
  stored originals would alter the evidence and is out unless the skeleton layer is chosen.
- **Read-only access:** the app only reads originals (no edit/rename/replace action, files
  written once; optional `chattr +i`). Attachment view/download streams the bytes cut from
  the original, unmodified, after comparing the attachment hash with the recorded one.
  In-app viewer shows only safe types (PDF, images, plain text) sandboxed; everything else
  is download-only. Reuses upstream AttachmentPreview/File() code with a new byte source.
  To check at build time: the interface has no email edit/delete actions beyond this rule.
- **Attachment text / OCR (later, optional; decided 2026-11-21):**
  - Separate asynchronous worker; never blocks archiving; text is derived, rebuildable and
    only ever indexed (never fed to an agent that can act: scanned content is untrusted).
  - Queue keyed by attachment SHA-256, so an attachment seen in many emails is read once
    (processing is shared even though the files are not de-duplicated). Resumable.
  - Step 1, always: read the text layer directly (PDF/Word/Excel) with no OCR. Step 2, only
    for pages with no usable text (scans, photos): OCR.
  - Setting `Ocr:Mode = off | cpu | gpu`. GPU-vendor agnostic (decided 2026-11-21): the code
    talks only to an execution-provider setting `Ocr:Provider = auto | cpu | cuda | rocm |
    openvino | ...`, never to one vendor. One classic engine for all devices: PaddleOCR
    models through ONNX Runtime, which has CPU, NVIDIA CUDA, AMD ROCm and Intel OpenVINO
    providers (availability per platform to be verified at build time). The default image is
    the CPU build; GPU builds are separate image tags per vendor (e.g. `-cuda`, `-rocm`,
    `-openvino`), each needing that vendor's driver/runtime on the host. If the chosen
    provider is not available the worker logs it and falls back to cpu. Vendor-neutral escape
    hatch: an optional OCR HTTP address (any machine/GPU/server that returns text), which is
    also where a future LLM engine would plug in. Tesseract is CPU-only (no real GPU mode), so
    it is only a fallback/baseline. Bundled in the app image, no external service; run as a
    second container of the same image, no network, originals mounted read-only, time/memory
    limits.
  - Hardware today (example only, not a requirement): a T1000 8 GB (NVIDIA) that could be
    passed through to the clio VM (IOMMU/VFIO on Hades, driver in the VM, backup/restore
    test of the VM with passthrough). Not needed for cpu mode.
  - Record engine + version + time per extracted text so it can be re-run with a better
    engine and the text replaced.
  - Open points: confirm a maintained .NET path for PaddleOCR-ONNX (else a small helper
    program in the image); measure first how many attachments have no text layer (attachment
    type/size counts, no mail reading); compare engines on ~20 of the user's own scans.
  - Privacy note: OCR of IDs/passports puts that text in the search index in the clear.
  - FUTURE (not part of the first build, deliberately left out): optional vision-LLM engine
    as an HTTP address (Ollama or any OpenAI-style server) with a vision model, used as a
    second pass for pages where classic OCR gave poor results. Needs: shared page-to-image
    step, address/model/timeout/concurrency settings, image resizing, output sanity checks
    (runaway/empty/commentary), a "poor result" rule, and sandboxing. Expected slower per page
    than classic OCR on the T1000 but better on tables/handwriting/skewed photos; can invent
    text, so only ever an index aid. Decide after comparing with classic OCR on real scans.
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
3. `ContentHash`/`HashCreatedAt`: left alone. `IsLocked` is in use upstream (trigger plus
   deletion policy), see "What already exists upstream"; do not touch it without the review in (c).
5. Sync order: user decided 2026-11-21 NOT to start syncs on the existing app first; lean mode
   is built before any Gmail sync. Originals cannot be back-filled, so no mail is synced until
   then.
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
