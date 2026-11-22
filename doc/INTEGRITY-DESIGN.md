# Integrity fork: design (draft for approval)

Branch `integrity` of the fork of s1t5/mail-archiver (GPL-3.0). Goal: an archive whose
originals are kept byte-exact, can be restored to a mailbox, and whose integrity can be
proven at any time. Mbox import is out of scope (its copy is re-serialized, see
`RawMessageSource.Serialized`) and is refused in lean mode and under `RawMessages:Required` (see
"Lean storage for imports and Graph"); capture comes from IMAP, Graph and EML files.

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
2. **`IsLocked` is weaker than it looks.** (Corrected 2026-11-22: the current upstream trigger function,
   migration 20260710120000, compares the WHOLE row except `IsLocked` and `FolderName` and, in this fork,
   the derived `AttachmentText`/`AttachmentTextAt` columns; the five-field list that stood here was stale.)
   A second trigger blocks deleting locked rows.
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

### Finding while building lean reads (2026-11-21, from CI tests)

Attachment bytes are the output of MimeKit's `DecodeToAsync`, the same call upstream's ingest
uses. Observed in a CI test: a quoted-printable part whose content contains CRLF decodes with
LF line endings; base64 parts decode byte-exact. (A first guess that this was a `text/*`
effect was wrong: it happens for a binary content type too, so it comes from decoding the
quoted-printable body.) Consequence for the audit claim: an attachment is "the decoded
content of that MIME part of the stored original" and, for quoted-printable parts with line
breaks, line endings can differ from the sender's local file; the MIME part itself is always
available byte-exact in the original `.eml`. Lean must hash and serve attachments with the
identical decode call at ingest and at read time (pinned by a test). Parts are located by tree
path (`MimePartLocator`: `b/c1/m/c0`), not by file name, because names can be missing,
duplicated or generated at archive time.

### Lean ingest builds the record from the original (decided 2026-11-21, user's suggestion)

Upstream (`EmailCoreService.ArchiveEmailAsync` ~2045) saves the full parsed row first and writes
the original afterwards (the file is named by the email id). Because no mail is synced before
lean mode exists, there are no existing rows to convert, so lean mode does NOT use a separate
strip step. For each new email, inside one database transaction:
1. Parse the downloaded bytes once; build a LEAN row: addresses, subject, dates, plain-text
   `Body`, `RawHeaders`, and per attachment name/size/content type/content id, decoded
   SHA-256 and MIME part path. No `HtmlBody`, no `BodyUntruncated*`/`OriginalBody*`, no
   attachment bytes; `ContentStripped = true`.
2. Insert the row (the id now exists), write the original, read it back (hash checked).
3. Re-parse the file just written and confirm every attachment resolves by part path to
   content with the recorded SHA-256.
4. Any failure (write, hash, a part that does not resolve, the synthesized .ics attachment, an
   original over the size limit): fill the FULL data (we still hold it in memory) into the same
   row, `ContentStripped = false`, log it, commit. Nothing is lost and bulky data is only
   persisted when needed.
Needs a migration (`EmailAttachments.PartPath`, `.Sha256`; `ArchivedEmails.ContentStripped`).
Readers check `ContentStripped` and read from the original; a stripped email whose original is
missing is an error, never an empty page. Locked rows are unaffected (the trigger watches
`Subject/Body/From/To/ContentHash`). A conversion tool for already-archived rows is only built
if the sync-order decision is ever reversed. Startup checks in Lean: `RawMessages:Enabled`,
filesystem storage and `MaxMessageSizeMb = 0` are required, otherwise the app refuses to start.

### Hash as identifier, audit reference in three places, threat model (decided 2026-11-21)

- **Content-addressed originals:** each original is stored under a path derived from its
  SHA-256 (e.g. `ab/cd/<sha256>.eml.gz`). The hash is the stable external identifier of an
  email (file store, database, audit log, what an auditor is shown). The database id stays as
  the row key, because two emails can share a Message-ID with different bytes (different
  hashes; both recorded in the duplicate log). Hash-named files can be written and verified
  BEFORE the row exists, which removes the id-first ordering problem of upstream's id-named
  files and simplifies lean ingest (write file, verify, insert row). Deletion rule: byte-
  identical originals (e.g. in two accounts) share one file; a file is removed only when no
  row refers to it (the orphan collector already works this way). This replaces
  `RawMessagePathResolver` naming for lean mode; upstream-named rows stay readable.
- **Audit reference kept in three independent places:** (1) the database row; (2) the
  hash-chained audit log and the signed manifest next to the files; (3) a copy outside the
  archive machine (cheap options: a daily chain-head hash appended to a private repo, or sent
  to a mailbox the user controls; exact target not decided).
- **Threat model, stated plainly:** the design protects against mistakes, software bugs, a
  changed file, a changed database row and non-root tampering. Anyone with root on the archive
  machine can change files, database, log and manifest together; against that, only the copy
  held somewhere root cannot reach (3) helps. The audit claim must not say more than that.

### Build status (2026-11-21, CI green, 1327 tests)

Built and tested on GitHub Actions: MimePartLocator + OriginalMessageReader; migration
`AddLeanStorageColumns`; `RawMessages:FileSystem:HashNamed`; `StrippedContentHydrator` wired into
every reader (pages, attachment view/download, exports, M365 restore, API) with the builder refusing
a stripped email; lean ingest (`Storage:Mode=Lean`, default off: verified hash-named original first,
lean row, every attachment re-found by hash, otherwise stored in full) with startup validation.
Run lean with: `Storage__Mode=Lean`, `RawMessages__Enabled=true`, `RawMessages__Storage=filesystem`,
`RawMessages__FileSystem__BasePath=<mounted dir>`, `RawMessages__FileSystem__HashNamed=true`,
`RawMessages__MaxMessageSizeMb=0` (the app refuses to start with any of these wrong). Not yet built:
verify-all job and integrity panel (Phase 1 items 2-5), hash-chained audit trail (Phase 2), MCP check,
`RawMessages:Required` (refuse instead of fall back to full storage), OCR.

### Build status update (2026-11-22, CI green, 1357+ tests)

Done since the status above: integrity panel on the detail page (`GET /Emails/Integrity/{id}`); MCP
`get_email_integrity` and lean-aware `get_email`/`get_attachment`/`get_email_eml`; `RawMessages:Required`
(IMAP, Graph and import paths refuse an email whose original cannot be kept; lean ingest that fails to
verify also refuses; startup check that Required needs Enabled); Phase 2:

- **Audit chain** (`AuditChain` table, migration `20261122100000_AddAuditChain`): `EntryHash` = SHA-256 of
  `Seq|Timestamp(µs)|Kind|EmailId|Sha256|PrevHash|Payload`, `Seq` assigned by the writer under a
  PostgreSQL advisory lock. A trigger refuses UPDATE/DELETE/TRUNCATE (stops mistakes and non-owner
  changes; the table owner can drop it, which the head copy below covers). Chained events: every
  access-log entry (free text as one hash), every original stored (`original.stored`, via an EF
  interceptor on RawMessages inserts, so all import paths are covered), every verify-all run, every
  manifest. `GET /api/v1/integrity/chain/head` and `/chain/verify` (first break, hash changed since
  archiving, emails with no `original.stored` entry, e.g. stored before the chain existed).
- **Signed manifest** (`POST /api/v1/integrity/manifests`, list, `GET .../{name}/verify`): JSONL of every
  stored original (id, SHA-256, size, source, storage) plus a header with its SHA-256, count and chain
  head, signed ECDSA P-256 (DER). Outsiders verify with
  `openssl dgst -sha256 -verify public-key.pem -signature manifest-X.sig manifest-X.json`. Key and
  manifests live in `Integrity:ManifestPath` on the same drive (user decision); the key location is
  configurable (`Integrity:ManifestKeyPath`). Schedule: `Integrity:ManifestIntervalHours` (0 = off).
- **Known limits, stated plainly:** root on the archive machine can re-sign and rewrite the chain
  together; only a copy of the chain head (seq + hash) and the public key held elsewhere defeats that.
  A failed chain append is logged but does not block the action; the verifier reports unchained emails.
  (Corrected 2026-11-22: a failed original write after the row was saved used to leave the email without its
  original; under Required the row is now removed again and the email counts as failed, and the half-written
  original row is dropped. Lean, EML and Graph lean paths write and verify the original BEFORE the row.)

### Lean storage for imports and Graph (2026-11-22, CI green, 1428 tests)

Until now only the IMAP sync archived lean; EML import and Graph stored the email in full. Now all three
share one code path (`LeanArchive`: the lean/refuse decision and the lean row shape; `EmailCoreService`
uses it unchanged):

- **EML import** (`MailImporter`): under `Storage:Mode=Lean` the file bytes (`RawMessageSource.EmlFile`) are
  written hash-named and verified first, every attachment is found again in the written file by hash, and
  the row is stored lean; otherwise it is stored in full, or refused under `RawMessages:Required`.
- **Graph sync** (`GraphMailArchiver`): same, with the MIME message Graph returns as the original
  (`RawMessageSource.Graph`; needs `RawMessages__IncludeGraph=true`, the default, otherwise there is no original
  and the email is stored in full or refused under Required). A body synthesized from an `.ics` attachment is
  not part of the original, so such an email is stored in full, as for IMAP. Duplicate checks are unchanged.
- **Index fields come from the original.** The import's parsed copy may have been header-recovered or cleaned,
  and the Graph copy comes from the Graph JSON (its text body is the 255-character preview) and the Graph
  attachment API. IntegrityService compares the database copy with the parse of the stored original, so for
  these two paths the subject, sender, recipients and text body of a lean row are re-derived from the verified
  original (`LeanIndexFields`, shared with IntegrityService). If the original cannot be parsed again the
  email is stored in full. Consequence worth knowing: for an import with several `From` addresses the lean row
  keeps the first, like IMAP does. Graph's display names are kept from the Graph JSON.
- **mbox is refused** when `Storage:Mode=Lean` or `RawMessages:Required=true`, unless
  `Storage:AllowSerializedImports=true`. The check runs when the web form is opened and submitted (the page
  shows the reason), at the start of every import job (so the queue and the command-line import end as a
  failed job with the reason) and nothing is read from the file. With the setting on, mbox imports work as
  upstream: the original is the re-serialized message, labelled `serialized`, and in lean mode the row is lean
  from that copy. `serialized` originals prove only that the archive holds what was imported, not what was delivered.
- **Chain:** the audit-chain interceptor sits on the RawMessages insert, so EML and Graph lean rows get their
  `original.stored` entry like IMAP rows (tested).
- **Not covered:** the Graph archiver needs a live Graph client, so its end-to-end path is not unit tested;
  the lean step it calls is tested with a Graph-shaped email. Existing emails are not converted.
- **Risk under Required:** a Graph attachment that cannot be found again in the MIME original (an item
  attachment, for example) means "no verified original", so such a message is refused until stored in full is
  allowed (turn `Required` off) or the attachment handling is extended.

### OCR step A: attachment text for search (2026-11-22, CI green, 1388 tests)

Built: the worker that reads the TEXT LAYER of attachments so a word that exists only inside an attachment
is found by search. No OCR engine yet (step B).

- **Settings** (`Ocr` section, env `Ocr__Mode` etc.): `Mode` off (default) | cpu | gpu. cpu and gpu both read the
  text layer only for now and log that no OCR engine is installed; off does nothing at all. Also `BatchSize`,
  `IdleDelaySeconds`, `StartupDelayMinutes`, `MaxAttachmentMb` (50), `MaxTextKb` (128), `TimeoutSeconds` (60).
- **Queue:** keyed by the attachment SHA-256 (lean rows record it; full-storage rows use `AttachmentContents.Hash`),
  so an attachment in many emails is read once. Resumable by construction: "attachments without a result" is the
  queue, nothing is kept in memory. Lean rows are read through the hydrator (bytes cut from the original and
  checked against the recorded hash). Results live in `AttachmentTexts` (status Done | NeedsOcr | Unsupported |
  Failed, method textlayer|ocr, engine, version, time, error). Failed results are retried only after a rescan.
- **Extractors:** PDF (PdfPig 0.1.11, Apache-2.0), Word/Excel/PowerPoint and OpenDocument (zip + XML, no macros,
  DTD off, per-entry decompression cap, entry count cap), text/CSV/JSON/XML/ICS, HTML (tags, scripts, styles
  dropped), attached messages (subject + text body). A PDF with under 20 characters of text and every image are
  NeedsOcr (waiting for step B). Text is cleaned (no NUL/control characters, whitespace collapsed) and capped.
- **Search:** `ArchivedEmails.AttachmentText` (+ `AttachmentTextAt`, set when all of an email's attachments are done,
  also when they held no text) is added to both `to_tsvector` expressions and the phrase checks in
  `EmailFilterSql` and to the EF fallbacks in `EmailCoreService.SearchEmailsEFAsync`. Field searches
  (`subject:`, `body:`, `from:`, `to:`) are unchanged. The column is derived and rebuildable; nothing reads it
  except search (not the integrity check, API DTOs, exports, the eml builder or restore).
- **Two existing protections had to change** (migration `20261122120000_AddAttachmentText`): (1) the locked-email
  trigger compares the whole row, so it now also ignores `AttachmentText` and `AttachmentTextAt` (otherwise a locked
  email could never get its search text; its own content is still fully protected); (2) the full-text GIN index only
  helps when the query repeats its expression exactly, so `idx_archivedemails_fulltext_search` is replaced by
  `..._v2` on the longer expression (the build time grows with the archive; do it at a quiet moment on a large one).
  A small partial index `IX_ArchivedEmails_AttachmentTextPending` keeps the queue lookup cheap.
- **PostgreSQL's 1 MB vector limit:** the other fields are already capped upstream; the attachment text adds at most
  `MaxTextKb` (128 KB), per attachment and per email.
- **API (admin):** `GET /api/v1/attachment-text/status` (counts by status and method, attachments waiting, emails
  awaiting their search text), `POST /api/v1/attachment-text/rescan?scope=failed|all`.
- **Open:** the pending-attachment query anti-joins all attachments each round (fine for step A, an index on
  `EmailAttachments.Sha256` would help on millions of rows); legacy inline attachments without a hash are skipped;
  step B (engine, sample scans, CPU/GPU choice) is not started.

### OCR step B: the CPU OCR engine (2026-11-22, CI green, 1412 tests)

**Decision (by measurement, not assumption).** PP-OCRv5 *mobile* detector + the PP-OCRv5 *Latin* recogniser, run
through ONNX Runtime on the CPU, with our own C# pre/post-processing (DB box extraction, CTC decode). Official models,
Apache-2.0, pinned by Hugging Face commit and SHA-256 (`tools/fetch-ocr-models.sh`): det 4.8 MB
(`PaddlePaddle/PP-OCRv5_mobile_det_onnx`), rec 8.0 MB (`PaddlePaddle/latin_PP-OCRv5_mobile_rec_onnx`), plus the 836-character
dictionary taken from the recogniser's own `inference.yml` (`OcrData/`). Pipeline settings come from the models'
`inference.yml` and were confirmed against RapidOCR's defaults and a set of variants (larger long side, lower box threshold,
inverting dark images: none helped, so there is no pre-processing): detector long side capped at 960 px (never enlarged),
sides rounded to 32, BGR, ImageNet mean/std, threshold 0.3, box threshold 0.6, unclip 1.5; recogniser height 48,
(x/255-0.5)/0.5, greedy CTC, 838 classes (blank, 836 characters, space).

| Spike candidate (reference pipeline) | synthetic: CER / WER / numbers / words | real screenshots: words |
|---|---|---|
| Tesseract 5.5 (eng) | 7.3% / 12.0% / 56 of 59 / 95.8% | 92.5% |
| RapidOCR defaults, v5 det + latin rec | n/a | 87.4% |
| v3 English / v3 Latin recognisers | 19.1-19.4% CER on all 12 cases | 96.5% / 97.0% |
| v5 *server* det (88 MB) + latin rec | 59 of 59 / 86.7% on all 12 | 95.5%, 6.2 s per image, 2.1 GB |
| **v5 mobile det + latin rec (chosen)** | **4.0% / 5.1% / 59 of 59 / 99.7%** | **99.5%**, 0.17-0.44 s per image |

(Image-only runs; the scan PDF case is excluded because image-only engines cannot read a PDF. An earlier table counted its 23
numbers as misses for every engine, which is why "numbers" looked low everywhere: 56 of 82 for Tesseract was really 56 of 59.)
Real screenshots, per image (word recall / character error, hand-written truth for four): capabilities 100% / 7.5%,
Proxmox 100% / 4.2%, dark integrations page 94.7% / 4.8% (Tesseract: 42.1% / 55.2%), BookStack permissions 100% / 37.9% (the
character error is column order; the words are all there). The C# port reads all six committed synthetic test images with
100% word recall in CI (0.3-1.4 s each on a shared 2-core runner). Parity on the user's real screenshots was measured with
the Python reference (`tools/ocr-reference.py`, same settings): they cannot go to CI because the fork is public.

**Integration route.** `Microsoft.ML.OnnxRuntime` 1.32.0 (MIT) + `SkiaSharp` 4.156.2 (MIT, decode only; the resize, crop and
rotation are our own code so they are testable). No wrapper NuGet was needed: the post-processing is about 150 lines.
ImageSharp was not used (split licence). Everything behind `IOcrEngine`; `OnnxOcrEngine` loads nothing until the first image,
so the application starts and `Ocr__Mode=off` works with the models absent (tested).

**Behaviour.** Items that step A marks NeedsOcr (images; PDFs with under 20 characters of text layer) are read when
`Ocr__Mode` is `cpu` (or `gpu`, which logs that no GPU image variant exists and uses the CPU) and the models are present.
Scanned PDFs: the embedded page pictures are extracted with PdfPig (JPEG as is, others through PdfPig's PNG conversion;
pictures under 100 px skipped, 4 per page, `Ocr__MaxPages` pages); a page whose picture cannot be extracted (fax/JBIG2/JPEG 2000
formats) is reported and, if nothing at all was read, the attachment is Failed with that reason. A picture with no
recognisable text is Done with empty text (read, nothing there). Results are stored with method `ocr`, engine
`paddleocr-ppocrv5-onnx/1 det:<hash8> rec-latin:<hash8>` so a better engine can replace them. At worker start, existing NeedsOcr
results are queued again automatically when the engine is available; `POST /api/v1/attachment-text/rescan?scope=needsocr`
does the same on demand. `GET .../status` shows `ocrEngine` and `ocrUnavailable`.

**Limits and untrusted input.** Pixel count is read from the image header and checked BEFORE decoding (`Ocr__MaxImagePixels`,
default 25 million); one image at a time; per-image time limit (`Ocr__TimeoutSecondsPerImage`, default 30; the ONNX run is
terminated on cancellation); at most 400 lines per image; `Ocr__Threads` default 2; ORT memory arena and pattern cache off so
memory is returned after each image. Corrupt, oversized or unsupported images (for example TIFF, which SkiaSharp cannot decode)
become Failed with a short reason, never a crash. The OCR text is only ever indexed.
Settings: `Ocr__Mode`, `Ocr__Provider` (auto|cpu; cuda/rocm/openvino log that they are not built yet), `Ocr__Threads`,
`Ocr__ModelDirectory` (default `<app>/ocr-models`), `Ocr__MaxImagePixels`, `Ocr__TimeoutSecondsPerImage`, `Ocr__MaxPages`.

**Image.** The Dockerfile downloads the models with the pinned script (build arg `OCR_MODELS=skip` builds without them) and keeps
only the target platform's native libraries. Compressed image 135.0 MB before, 165.4 MB after (+30.4 MB).

**Risks / open points.** TIFF and bilevel-fax PDF scans are not read yet (Failed with a reason); handwriting and very poor
photos are untested on real material; the GPU variants (CUDA, ROCm, OpenVINO) are not built; vertical text is not handled
beyond the 90-degree turn of tall crops; the C# engine itself has not been run on the user's real screenshots (parity argument
above); search remains whole-word/prefix, so OCR misreads of a word make it unfindable.

### Review fixes (2026-11-22): the audit claim, exactly, and what changed

An independent review (REVIEW-opus-2026-11-22.md, 27 findings) found that the checks said more than they
proved. These are the corrected statements; REVIEW-fixes-status.md lists every finding and its state.

**The audit claim, in exactly this wording.** For every email that has a stored original, the archive can
show that (1) the original's bytes match the SHA-256 recorded when it was stored (re-hashed on every check);
(2) that hash is the one the append-only audit chain recorded at that time (emails stored before the chain was
switched on are reported as *Unchained*, which is informational: the chain adds nothing for them);
(3) the fields the application shows and serves from the database agree with the original: subject, sender,
recipients, display names, sent date (within 2 seconds), the text body (up to 100 KB), the HTML body and
original-body copies, and for every attachment its bytes (hashed again), name, type and size, plus that every
real attachment part of the original has a database counterpart; a lean email must carry no HTML body, no body
copy and no attachment bytes. Fields that cannot be compared exactly are listed as *not compared* (Graph rows
take display names, dates, HTML and the attachment list from the Graph API; very large bodies are stored
truncated). Originals are byte-exact as held by the mail server at capture time, except those labelled source
`serialized` (mbox import, IMAP recovery fetch), which are intact but re-written by the parser. Email
deletions and every access-log row are recorded in the chain, and a manifest lists every original.

**What this does and does not protect against.** It protects against mistakes, software faults, and changes by
anyone who does NOT hold the application's database credentials, host file access or code execution inside the
application. Someone who does can rewrite the originals, the database, the chain and the manifests together
(the chain has no secret and the app holds the signing key). Only a copy of the chain head and of the manifest
public key kept off this host (`tools/pull-anchor.sh`, `tools/compare-manifests.py --pubkey-sha256`) can reveal
that, and only for changes made after the last collection. The append-only trigger stops mistakes and non-owner
changes; the table owner (the shipped compose file uses the Postgres superuser) can switch it off.

**Verdicts.** Valid / Invalid (bytes or encoding damaged, including truncated, non-gzip and oversized files) /
Missing (the original or its record is gone, including when the chain says one was stored) / NotStored /
Inconsistent (database copy differs) / ChainMismatch (the row's hash is not the one the chain recorded). The
verify-all run counts ChainMismatch and Unchained.

**Behaviour changes.** A refused or failed email is a failed email: the sync does not move past it. Required
refuses only when no verified original exists (a calendar invite or an attachment that cannot be found again is
stored in full WITH its verified original); re-serialized originals are refused under Required unless
`Storage:AllowSerializedImports` is set. Every deletion path is recorded by a database trigger in the same
transaction (`email.deleted`: id, hash of the original, account, database user, reason if the session set
`mailarchiver.delete_reason`). The storage migration filesystem to database is refused for hash-named or lean
archives unless `Storage:AllowHashNamedMigration`, and never deletes a shared file. The manifest signing key
defaults to the DataProtection key directory (not beside the manifests), is created 0600, and is never
re-created while manifests exist unless `Integrity:AllowNewKey`.

**Known limits and manual steps.**
- *Deployment (F-06).* The shipped compose file runs the app as the container's default user with the Postgres
  superuser. To reduce the exposure: run the app container with `user: "<uid>:<gid>"` that owns the mounted
  folders; connect the application as a role that is not the owner of `AuditChain` (create it with
  `CREATE ROLE app LOGIN PASSWORD ...; GRANT USAGE ON SCHEMA mail_archiver TO app; GRANT SELECT, INSERT ON mail_archiver."AuditChain" TO app;`
  and the usual table rights on the rest, and run migrations with the owner role). Not done by default because it
  needs testing on the real stack and a migration role.
- *Index build (F-17).* The migration `AddAttachmentText` replaces the full-text index with a plain
  `CREATE INDEX`, which blocks writes on `ArchivedEmails` while it builds. On a large archive run, BEFORE
  upgrading, `CREATE INDEX CONCURRENTLY idx_archivedemails_fulltext_search_v2 ON mail_archiver."ArchivedEmails" USING gin (...)`
  with the expression from that migration; the migration then finds it present. (A shipped migration is not edited.)
- *One instance (F-13).* verify-all and the background workers assume one application instance.
- *Rollbacks (F-26).* Down methods of the shipped migrations drop audit and lean data; rolling back past them is
  not supported. Restore from a backup instead (doc/BackupVerification.md).
- *Untrusted attachments (F-14).* Text and picture extraction run inside the application process with time and
  size limits; a hostile file can still exhaust it. Running the worker in its own container is the remedy and is
  not built yet.
- *Decisions waiting for the user:* F-11 (what deleting an email does to its hash-named original) and F-21 (a
  versioned chain hash input). See REVIEW-fixes-status.md.

### The original bin: deleting an email really removes its original, after 30 days (review F-11, 2026-11-22, CI green, 1500 tests)

**It is ON by default.** Switch it off with `RawMessages__OriginalCleanup__Enabled=false`. It only ever acts on
hash-named originals on the filesystem (lean mode uses them), so on an archive without `HashNamed` it does nothing and
says so in the log and in `GET /api/v1/integrity/original-cleanup/status` (`applicable: false`).

Why it exists: hash-named originals are shared by byte-identical emails, so they were never deleted with an email (the
review found that deletion and retention did not remove the original). Now, like a recycle bin:

1. **Bin.** A hash-named file that NO `RawMessages` row refers to is MOVED (atomic rename, then verified by size and
   by hashing the decompressed content) to `.bin/` under the originals folder, same layout. The reference check is
   repeated right before the move and right after it (a row that appeared meanwhile undoes the move). The first run
   after an upgrade moves files that are already unreferenced into the bin; it deletes nothing outright.
2. **Hold.** The file stays in the bin for `BinRetentionDays` (default 30, minimum 1), counted from the day it was
   binned (`OriginalBinEntries.BinnedAt`). Nothing is purged earlier, however old the file was.
3. **Restore.** If a row refers to a binned file again (the same bytes were archived again, or the email came back),
   the next run moves it back (or drops the bin copy when a live copy was written again). An admin can also move one
   back by SHA-256: `POST /api/v1/integrity/original-cleanup/restore` with `{"sha256": "..."}` (never a path). A restored
   original stays live for at least a day so it can be re-imported; if nothing refers to it after that, it returns to
   the bin with a fresh retention period.
4. **Purge.** After the retention period the bin copy is deleted permanently, after re-checking that no row refers to it
   (if one does, it is restored instead).

Safety rails: files written or reused within the last 24 hours are left alone (ingest touches a reused file, so an import
that is about to commit its row is never raced); at most `MaxFilesPerRun` (default 1000) are moved and, separately, purged
per run; only paths of the form `ab/cd/<64 hex>.eml.gz` whose directory names match the hash are touched (id-named
files, anything malformed, symlinks, and anything resolving outside the originals folder are refused); one run at a time
in a process; every action is written to the audit chain BEFORE it happens (`original.binned`, `original.restored`,
`original.purged`, `original.purge-failed`; an action whose record cannot be written is not done), plus an access-log row
of type Integrity per run and per manual restore; it never runs while the storage settings do not describe hash-named
filesystem originals. Ownership: the older orphan collector owns id-named files and ignores hash-named files and the
bin; this collector owns hash-named files and the bin and ignores id-named files.

What verification does with it: a binned or purged original is not a finding for a deleted email (the email's own
`email.deleted` entry explains it, and manifest verify is unaffected). It IS a finding when a row still refers to an
original whose last bin event is `binned` or `purged` (no later restore or new store of the same bytes): chain verify
reports it and sets `ok` to false, and a per-email check says the original is in the bin and how to restore it. A file
that is missing with no bin entry and no chain record stays a plain `Missing`.

Settings: `RawMessages__OriginalCleanup__Enabled` (true), `__BinRetentionDays` (30), `__IntervalHours` (1, so a deleted
email's original reaches the bin soon), `__MaxFilesPerRun` (1000), `__StartupDelayMinutes` (15). Admin API (all under
`/api/v1/integrity/original-cleanup/`): `GET status` (on/applicable, counts in the bin and bytes, earliest purge date,
totals, last run), `POST run` (one pass now, takes no path), `GET bin` (sha256, binned at, purge date, size; no paths),
`POST restore`, `POST erasure-status/{emailId}` (read-only: for one already deleted email, whether its original is still
shared by other emails, in the bin until when, or gone).

**The honest limit:** while an original is in the bin it can be restored. After the retention period it is gone for good,
except from a backup. Leaving the bin on (the default) therefore means that deleted emails are really erased after 30
days, and from then on the archive no longer proves what those emails contained. The audit chain keeps only their hashes.
The grace period is counted from when the bin moved the file, not from when the email was deleted (a deletion is
noticed within `IntervalHours`).

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
   MimeKit and compares it with the stored fields. (Corrected 2026-11-22: the first build did NOT
   compare dates, HTML bodies, display names or attachment names/sizes; it does now, see
   "Review fixes" below for exactly what is compared and what is listed as not compared.)
   Any difference => **Inconsistent**.
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
- **Database = index cards plus the plain-text body as search source (decided 2026-11-21):**
  message id, folder, dates, addresses, subject, size, file path, hash, a short preview, the
  plain-text `Body` (already capped at 500 KB by upstream; upstream's search index is built
  from it, so it stays), and per attachment: name, size, hash, MIME part path (no bytes).
  Dropped in lean mode: `HtmlBody`, `BodyUntruncatedText/Html`, `OriginalBodyText/Html`,
  attachment bytes (`AttachmentContents`, legacy `Content`). `RawHeaders` stays for now (small,
  used by date resolution). The `Body` text is derived and rebuildable from the original; the
  earlier "no stored body text" and "word list only" wording is superseded.
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

### Storage comparison on the thao import (measured 2026-11-22)

Data set: thao@deptco mbox, 15,647 messages (one of 15,648 missing, not chased), Takeout mbox 5.3 GB.

**Open Archiver (measured on clio after the ingest finished, all 15,647 indexed):**

| Part | Size |
|---|---|
| Originals: one `.eml` per email, attachments inside the file, no separate attachment store (15,647 files, about 345 KB each) | 5.4 GB |
| Search index (Meilisearch volume; probably includes Tika text incl. OCR) | 2.2 GB |
| Postgres database (`pg_database_size`) | 34 MB |
| **Total** | **about 7.6 GB (1.45x the mbox)** |

The staged mbox copy (`storage/temp/thao.mbox`, 5.3 GB) is extra and can be deleted. Open Archiver
stores one original; its overhead is the search index, not a second body copy.

**gzip gain (measured):** 200 random stored files, 55.0 MB raw -> 32.6 MB gzipped (default level),
about 41% saved (about 59% of original size). Two files with spaces in the name failed on both
sides of the test (same omission, small bias). Clears the 20% revisit threshold, so gzip stays.

**Fork, lean mode (ESTIMATE, never ingested real mail):** gzipped originals about 3.2 GB
(5.4 GB x 0.59) plus the DB. The DB is NOT near zero: lean mode keeps the plain-text `Body`
(search source, see above) plus index cards. Body text size is unmeasured, so the total is
about 3.5 to 4.5 GB, roughly half of Open Archiver, as long as the tsvector is built on the fly
(open question above; a stored vector or GIN index would add to it).

**Mail-Archiver upstream, original plus parsed copy (weak ESTIMATE):** about 2x the originals,
10 GB or more; share of attachment bytes in the parsed copy not measured.

**Not decided by storage alone:** the saving is about 3-4 GB per 15k messages, small next to the
audit goals. Open Archiver's free edition lacks the audit log and legal hold.

**Open checks:**
- Search speed: Postgres `to_tsvector('simple')` on the fly (fork) vs Meilisearch (Open Archiver),
  measure with several mailboxes loaded before committing.
- OCR later needs its own text store, adding to the lean figure.
- Disk: clio has 24 GB free of 64 GB; all three mailboxes in Open Archiver about 23 GB, lean
  about 10 to 13 GB. Grow the disk before the Gmail IMAP syncs.

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
