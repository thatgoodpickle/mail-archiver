# Backup and verification runbook (fork)

Goal: be able to show, at any time, that the archive is intact and that a backup can really restore it.
Threat model: mistakes, bugs, disk damage, bad restores. (Someone with root on everything can change
everything together; this runbook does not claim to stop that.)

## What must be in the backup

| Part | Where (lean test stack paths) | Why |
|---|---|---|
| Originals | `originals/` (`RawMessages:FileSystem:BasePath`) | The only full copy of each email in lean mode. Losing it loses HTML bodies and attachments. |
| Database | `pgdata/` or a `pg_dump` | Index data, attachment references, audit chain, integrity runs. |
| Manifests and signing key | `manifests/` (`Integrity:ManifestPath`) | Dated statements of what existed. Keep each `public-key.pem` with the manifests it signed. |
| Compose file and `.env` | stack folder | Needed to bring it back up. |

Take the backup consistently: a whole-VM snapshot (PBS) of the archive VM, with the application stopped, or a
database dump together with the originals folder. If the database and the originals folder are backed up at
different moments, the database can hold a row whose original file is not in the other copy (verify-all
reports it as Missing). A single whole-VM snapshot avoids this.

## Schedule these in the app

- `Integrity__VerifyAllIntervalHours=168` (weekly): re-hashes every original and re-derives the database copy.
- `Integrity__ManifestIntervalHours=24` (daily): signed list of every original plus the audit chain head.

## Periodic restore test (monthly is a good start)

1. Restore the latest snapshot to a temporary VM or stack. Do not point it at the live mailboxes: disable the
   accounts or use no network, so it cannot sync.
2. Start it and, as an admin with an API key (read it from a file, never on a command line):
   `GET /api/v1/integrity/chain/verify` should return `"ok": true`, then `POST /api/v1/integrity/runs` and read
   `GET /api/v1/integrity/runs/{id}` until it is Completed. Expect 0 invalid, 0 missing, 0 inconsistent.
3. Compare the restored copy with the live archive (next section).
4. Throw the temporary VM away.

## Compare a backup with the live archive

Copy the `Manifests` folder of the backup and of the live archive to one machine, then:

```
tools/compare-manifests.py <backup-manifests-dir> <live-manifests-dir>
```

It checks both signatures and entry lists, then prints:

- `MISSING`: an email in the backup is not in the live archive. Legitimate if it was deleted on purpose: look the
  id up in the access log (type Deletion). Otherwise it is data loss.
- `CHANGED`: the same email id now has a different original hash. This should never happen.
- `NEW`: emails archived since (normal).

Exit code 0 means nothing missing or changed. A signature or entry-list problem is printed as `PROBLEM` and
also gives exit code 1.

## When something is wrong

- `Invalid` / `Missing` in a verify-all run: restore that original from the backup (the file is named by its
  SHA-256, so the right file is the one whose hash matches the database row), then run verify again.
- Chain does not verify: the report names the first broken entry. Restore the database from a backup that
  verifies; do not repair entries by hand.
- Do not edit the audit chain or the manifests by hand; the checks exist to notice exactly that.
