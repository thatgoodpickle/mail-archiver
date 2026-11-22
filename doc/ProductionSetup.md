# Production setup and first real sync (fork)

For: the archive running on one VM (here: `clio`), backed up as a whole VM by Proxmox Backup Server (PBS), reachable on the
internal network only, syncing Gmail accounts over IMAP. Written for the first real mailbox, with no test Gmail account:
the first run is therefore treated as a **rehearsal that can be wiped and repeated**, because the app only READS from Gmail
and Gmail keeps every message.

## 1. Before you start

- [ ] Free disk on the VM: `df -h /opt/stacks`. Rule of thumb: at least **3x the mailbox size** free (lean storage is expected to need
      roughly 0.7 to 0.9x of the mailbox, but that estimate is unproven; attachments are stored whole inside each original).
- [ ] The VM is in the PBS backup job (whole VM, daily). Enable the QEMU guest agent on the VM so a backup can freeze the file systems.
- [ ] The test stack is removed (`/opt/stacks/mail-archive-test`), or at least stopped, so nothing confuses the two.
- [ ] A password manager for: the admin password you will choose, the Gmail app password, and the database password.

## 2. Create the stack

```
sudo mkdir -p /opt/stacks/mail-archive && sudo chown "$USER": /opt/stacks/mail-archive
cd /opt/stacks/mail-archive
mkdir -p originals pgdata logs keys manifests
curl -fsSL https://raw.githubusercontent.com/thatgoodpickle/mail-archiver/integrity/deploy/compose.production.yml -o compose.yml

umask 077
printf 'DB_PASSWORD=%s\nADMIN_PASSWORD=%s\nBIND_ADDR=%s\nAPP_PORT=5555\n' \
  "$(openssl rand -hex 24)" "$(openssl rand -hex 24)" "<the VM's LAN IP>" > .env
umask 022

docker compose up -d
docker compose logs --tail 30 mailarchiver
```
Use the VM's own LAN address for `BIND_ADDR` (for example `10.0.0.5`), so the app is not published on every interface. If the
firewall (ufw) is on, allow only your LAN to that port. Check the port is free (`ss -ltn | grep 5555`) and change `APP_PORT` if not.

Log in at `http://<BIND_ADDR>:5555` as `admin` with the `ADMIN_PASSWORD` from `.env` (`grep ADMIN_PASSWORD .env`; never paste it
anywhere). The app forces you to choose a new password at the first login: choose a long one and store it in the password manager.
The `.env` value stops working after that, which is expected.

## 3. Turn the safeguards on (Integrity & Storage page, admin menu)

1. **Trash:** on by default. After an upgrade of an EXISTING archive it waits for one click, "This is the right archive". On a
   new stack it identifies itself at first start.
2. **Schedules:** "check all emails" weekly (168 hours) and "signed records" daily (24 hours). Both are editable on the page
   (they are not set in the compose file on purpose).
3. **Attachment text / OCR:** choose On (processor) if you want scans and photos searchable. It is a background job that does
   not block anything.
4. Press "Check all emails" and "Check the history log" once on the empty archive: both should be clean.

## 4. Prepare Gmail (one account at a time)

1. **IMAP on:** Gmail, Settings, Forwarding and POP/IMAP, "Enable IMAP".
2. **App password:** Google Account, Security, 2-Step Verification must be on, then App passwords: create one for "mail archive".
   Store it in the password manager. Server `imap.gmail.com`, port 993, SSL on, username is the full address.
3. **All Mail must be visible over IMAP:** Gmail, Settings, Labels, "All Mail": Show in IMAP = ON. (Upstream's guide says to hide it;
   this fork syncs All Mail on purpose, because each message is then stored exactly once. The sync fails with a clear message if it is hidden.)
4. **The Marketing label:** create a label `Marketing`. Add filters that apply it (building blocks: `unsubscribe`, `"manage subscriptions"`,
   `list:*`). Protect mail you must keep, for example:
   `(unsubscribe OR list:*) -subject:(booking OR confirmation OR itinerary OR invoice OR receipt OR ticket)`.
   Tick "Also apply filter to matching conversations" to tag existing mail. Open the label and READ what it caught: look for booking
   confirmations and invoices that were caught wrongly, and fix the filter. Nothing is deleted or moved.
5. **Cap the first sync:** Settings, Forwarding and POP/IMAP, "Limit IMAP folders to contain no more than this many messages": **1,000**.
   The app then sees only the 1,000 most recent messages. You raise this later.
6. Leave Gmail's auto-expunge as it is: this archive never deletes anything in Gmail.

## 5. Add the account in the app

- Mail Accounts, add: Provider IMAP; server `imap.gmail.com`; port 993; SSL on; username the full address; the app password.
- **Leave "delete after days" and "local retention days" EMPTY** (the archive must not delete from Gmail or expire mail).
- The Gmail search filter shows `-label:Marketing` (it skips Marketing and all its sub-labels); "Sync only All Mail" is ticked.
- Press **Test** on the filter: it shows how many messages it would skip and how many it keeps. If it would skip more than half, it asks you to confirm: stop and look.
- Save, then press Sync.

## 6. The first 1,000 messages: what to check (and what to write down)

- The account finishes with `Failed: 0`, and the page's "Messages that could not be archived" list is empty (or explains each one).
- "Check all emails": all Valid. "Check the history log": intact.
- Spot-check 10 to 20 emails across senders: no Marketing mail got in; the mail you must keep is there (a booking confirmation, an invoice).
- Labels: chips look right on the list; the "Tagged Marketing" view is empty (anything there was tagged after it was archived).
- Things only a real Gmail account answers, and to write down: (a) a message tagged with a SUB-label of Marketing was skipped; (b) how
  Promotions/Social/Updates appear in the labels; (c) how long the daily label refresh took; (d) whether the "already archived, skip the
  download" check works (a second sync downloads nothing new); (e) that All Mail was detected without you choosing a folder.
- If anything looks wrong: STOP. Do not raise the cap. Tell me what the page and `docker compose logs` show.

**Wipe and redo** (only while the archive is a rehearsal): `docker compose down`, `sudo rm -rf originals pgdata keys manifests logs`,
recreate the folders, `docker compose up -d`, set up again. Gmail is unaffected.

## 7. Raise the cap, then call it production

Raise Gmail's cap to 5,000, then 10,000, then "Do not limit". Watch the daily download limit: the sync pauses at 2.5 GB and resumes by
itself the next day, so a 5 GB mailbox takes about three days. Only when the whole mailbox is in and checked: take a manual PBS backup
(label it "first full sync"), and from then on the daily job is your safety net.

## 8. Backups with PBS

- **What is covered:** the whole VM, so the originals, the database, the manifests and the keys are all captured at one instant. That is
  the consistent backup the runbook asks for. With the guest agent the file systems are frozen for the snapshot; Postgres recovers from
  a crash-consistent snapshot by itself.
- **Before every upgrade or risky change:** a manual PBS backup.
- **Restore test, monthly:** follow `doc/BackupVerification.md` (restore to a throw-away VM with no network to Gmail, run "Check all emails" and
  "Check the history log", compare manifests with `tools/compare-manifests.py`, then delete the throw-away VM).
- **Keep:** several daily backups plus monthly ones; a backup older than a deletion still contains that email's original, so deleted
  emails are only really gone after the Trash period AND after the older backups age out. Say so if anyone asks for an erasure.

## 9. Upgrades and rollback

1. Manual PBS backup. 2. Read `nextrelease.md` for what changed and any new setting. 3. Edit the `image:` line in `compose.yml` to the new
`sha-xxxxxxx` tag (the CI run of that commit must be green). 4. `docker compose pull && docker compose up -d`; watch the log; run both
checks on the page. Rollback = restore the PBS backup (do not run an older image against a database a newer one has migrated).

## 10. Known limits (stated plainly)

- The audit checks protect against mistakes, bugs, a changed file or row and people without the app's database login or host access;
  someone with root on the VM can change everything together (see `doc/INTEGRITY-DESIGN.md`, the exact claim).
- Labels are Gmail's metadata, not part of the original, and are refreshed daily: they are "as of the last sync".
- The claim for a Gmail account reads: everything in the mailbox except mail matching the recorded filter (`-label:Marketing`).
- Not covered: Microsoft 365 (Graph) quarantine, TIFF/fax scans (marked failed with a reason), a GPU build, misread OCR words.
