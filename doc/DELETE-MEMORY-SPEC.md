# Spec v2 APPROVED by the user 2026-11-23 (reviewed once by the advisor; option 1 + coverage check; all questions answered); NOTHING BUILT YET

Found on the clio test stack: delete an IMAP email in the archive, press Full resync, the email is archived again as a new id ("Found 14 messages", New: 2). A deletion only lasts until the next Full resync, and the production guide needs a Full resync after every Gmail cap raise.

Two features, one change (the markers make the coverage sum balance):
1. Deletion markers: a mail deleted on purpose is not archived again from the same account.
2. Coverage check (read-only): proves the archive accounts for everything the server lists.

The app stays strictly READ-ONLY towards Gmail. An opt-in label write ("tag deleted-from-archive mails in Gmail") was discussed and NOT chosen.

## Feature 1: deletion markers

### Written by the database, opt-in per path
`email.deleted` is already written by a trigger (`audit_chain_email_deleted`, BEFORE DELETE on ArchivedEmails, migration 20261122150000), so every path is in the chain. Markers use the same mechanism so no path can be forgotten, with one difference: **a marker is written only when the deleting code has set `mailarchiver.delete_reason` to a deliberate value** (`user`, `retention`) via `SET LOCAL` in the same transaction. Nothing sets that setting today (every chain entry says `unspecified`), so this is new. Default = NO marker. Reason: a forgotten path then fails visibly (mail returns), while a default-on marker would silently stop a mail from ever being archived.

Delete sites (all found with grep; each must be classified and tested):

| Site | What it is | Reason set | Marker |
|---|---|---|---|
| EmailsController.cs:3167 | single delete (UI) | user | yes |
| EmailsController.cs:3310 | bulk delete in controller | user | yes |
| EmailDeletionService.cs:318 | bulk delete job (UI, API, MCP, Tagged Marketing view) | user | yes |
| EmailCoreService.cs:2614 (DeleteOldLocalEmailsAsync) | local retention, deletes rows via ExecuteDeleteAsync | retention | yes |
| MailAccountDeletionService.cs:338, MailAccountsController.cs:1499 | account deletion / clearing | account | no (markers also go with the account: FK ON DELETE CASCADE) |
| EmailCoreService.cs:2215, MailImporter.cs:303, GraphMailArchiver.cs:156 | rollback of a just-inserted email | internal | NEVER |

To verify before building: every site above does write `email.deleted` through the trigger (retention uses ExecuteDeleteAsync, which still fires row triggers), and the reason value reaches the trigger (SET LOCAL needs the same connection/transaction; ExecuteDeleteAsync outside an explicit transaction needs one).

### Table `DeletedMessageMarkers`
AccountId (FK, cascade), Folder, MessageId, Size, Sha256, GmailMessageId, DeletedAt, ChainSeq (the email.deleted entry), Reason. No subject, sender or body. Unique (AccountId, MessageId, Size, Folder); indexes (AccountId, GmailMessageId) and (AccountId, Sha256). Created by a second trigger body in the same function (or a second trigger) so it is in the same transaction as the delete and the chain entry.

### How sync uses it (fail closed)
1. Marker check BEFORE download: Gmail by GmailMessageId; others by MessageId + Size (+ folder outside Gmail).
2. Marker check AFTER download by SHA-256 (covers a missing or changed Message-ID). This is the real backstop: it lives in its own code path OUTSIDE the existing pre-check's try/catch, which fails open ("downloaded as usual", ImapMailSyncService.cs ~847) and returns early when raw messages are not stored. A database error in the marker check must fail the message (counted as a sync problem), never import it.
3. Skipped messages are counted ("Skipped, deleted by you: N") and listed on the account page with **Allow again** per message and for all.
4. Same Message-ID but another size is NOT blocked (same documented limit as the existing pre-check).

### Allow again
Writes chain entry `marker.released` (who, when, marker). The mail comes back at the next FULL resync (quick sync only looks at mail after lastSync-12h, shown on clio: quick found 6, Full found 14), so the button offers to start one.

### Audit and backups
The marker row stores the chain seq of its `email.deleted` entry. The signed manifest counts markers, so a silent removal is visible. Markers are database rows, covered by the VM backup.

## Feature 2: coverage check (read-only)
Purpose: mail stays in Gmail for ever; the user needs proof the archive accounts for everything ("did I import all the mail?").

### Classification, not counting
One IMAP call on All Mail fetches only the message key of every message (no bodies, ReadOnly). **Each server message is assigned to exactly ONE bucket by a fixed order**, so a message can never be counted twice (for example archived and later labelled Marketing, since labels refresh daily):
1. archived (an email row with this key)
2. deleted by you (a marker)
3. quarantined
4. filtered out (member of the Gmail search-filter result, fetched as a set of ids, not a count)
5. not yet seen (everything else)

The page shows `server total = 1 + 2 + 3 + 4 + 5` plus an integrity line that the buckets add up. "Not yet seen" must be 0 after a clean sync; otherwise it lists those messages with a Gmail search link each. Reverse lines: `archived, no longer in Gmail: N` (information, the archive is now the only copy) and `archived twice (same X-GM-MSGID): N` (should be 0; catches a sync bug the sum would absorb).

### Provider-neutral key (so clio can test it)
Dovecot has no X-GM-EXT-1 (no X-GM-MSGID, no X-GM-RAW), so the Gmail sum could otherwise only run on the real mailbox. The classification code takes a message key from a provider adapter: Gmail = X-GM-MSGID; plain IMAP = Message-ID + size (folder + UIDVALIDITY + UID as a tiebreak). Clio runs the same classification code on Dovecot; unit tests feed it fake Gmail summaries.

### Quarantine key
SyncProblemMessage is keyed by Uid (ImapMailSyncService.cs ~1052), no Gmail id. Add X-GM-MSGID (and the folder's UIDVALIDITY) to the problem row when it is first recorded, so the bucket can match it.

### Limits (write in the docs)
Only the synced folders (default Gmail All Mail; Spam and Trash are outside the sum). Proves the archive holds what Gmail's IMAP served, not that Google's copy was byte-identical. Mail that arrived after the last sync shows as "not yet seen". Deleting the account and re-adding it (for example to fix credentials) forgets every marker; document it (or key markers by mailbox address, to decide). Deletions made before the feature exists have no marker (the chain keeps only the hash, not the account); production has none yet, the test stack can be wiped. Graph and importers are not covered by the sum or the markers.

## Tests
Unit: bucket order (every key lands in exactly one bucket; archived+later-labelled-Marketing is only "archived"), key adapters, marker matching rules, fail-closed on a simulated database error, reason handling per delete site. Integration on clio (Dovecot): delete IMAP mail -> Full resync -> New: 0 and listed as skipped; Allow again -> Full resync -> returns; rollback path creates no marker; account delete removes markers; coverage sum balances with deleted, quarantined and not-yet-seen cases. Pilot on real Gmail: compare the sum with Google's own count, check sub-label and Marketing set membership, label refresh at ~15k.

## Order of work
spec approved -> build in the fork with tests -> narrow Opus review of the sync path including this change (8474feb..741adab plus new commits) -> clio test of the new image -> production stack and pilot.

## Decisions (user, 2026-11-23)
1. Bucket order accepted: archived, deleted by you, quarantined, filtered out, not yet seen.
2. Allow again starts a Full resync for that account.
3. Option A: deleting an account is a full reset; its markers go with it (document it). Markers are NOT keyed by mailbox address.
Earlier: markers per account (option 1), read-only coverage check, retention deletions also create markers, skipped list with Allow again on the account page, no write to Gmail.
