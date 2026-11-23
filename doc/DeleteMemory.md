# Delete memory and the coverage check

This fork remembers what you deleted on purpose, so a later sync does not archive it again, and it can prove that the archive
accounts for everything the mail server lists. Design and decisions: `DELETE-MEMORY-SPEC.md`.

## Deletion markers

* Deleting an email in the archive (one, many, by filter, API or MCP) or removing it by the local retention policy leaves a
  **marker**: the account, folder, Message-ID, size, the SHA-256 of the stored original, Gmail's X-GM-MSGID and the number of the
  audit entry of the deletion. **No subject, sender or text** is kept.
* The marker is written **by the database** in the same transaction as the delete and its audit entry (`email.deleted`, whose payload
  carries `markerId`). A deletion that is rolled back leaves no marker.
* Only deliberate deletions leave markers (reason `user` or `retention`). Deleting an account or clearing it, and the internal
  rollback of a message that could not be stored, leave none.
* The sync skips a message that matches a marker. Before the download it matches by Gmail id (Gmail) or by Message-ID, size and
  folder (other servers); after the download it matches the SHA-256 of the original. The check **fails closed**: if it cannot be made
  (database error, the server did not describe the message), the message is not imported and counts as a sync problem.
* Skipped messages are counted as "Skipped, deleted by you" in the sync result and listed on the account page.
* A message with the same Message-ID but another size is **not** blocked (the same documented limit as the sync pre-check).
  Without a stored original there is no hash, so only the key check applies.

### Allow again

On the account page each skipped message has **Allow again**, and there is **Allow all again and start a Full resync**. Each release
writes a `marker.released` entry to the audit chain (who, when, which marker) before the marker is removed. The mail comes back at the
next **Full** resync: a quick sync only looks at mail after the last sync time. Only administrators can release markers.

### Deleting an account is a full reset

The markers belong to the account and are removed with it (foreign key, ON DELETE CASCADE). If you delete an account and add it
again (for example to fix credentials), everything you had deleted earlier will be archived again. Deletions made before this feature
existed have no marker.

Graph accounts and the importers are not covered by markers or by the coverage check.
