# Production setup and first real sync (fork)

For: the archive running on one VM (here: `clio`), backed up as a whole VM by Proxmox Backup Server (PBS), reachable on the
internal network only, syncing Gmail accounts over IMAP. Written for the first real mailbox, with no test Gmail account:
the first run is therefore treated as a **rehearsal that can be wiped and repeated**, because the app only READS from Gmail
and Gmail keeps every message.

## 1. Before you start

- [ ] Free disk on the VM: `df -h /opt/stacks`. Two different numbers, do not mix them up:
      (1) **What the archive is expected to use:** about 0.7 to 0.9 times the mailbox size (an unproven estimate: it comes from a
      compression test on a sample, not from real mail). (2) **What to have free before you start:** at least **3 times the mailbox
      size**, as a safety margin (database, the Trash, logs, growth, and the estimate being wrong). Use the storage figure Gmail shows
      for that account as the "mailbox size". After the first 1,000 messages, measure for real: `du -sh originals pgdata`, divide by
      the number of emails, and scale to the whole mailbox.
- [ ] The VM is in the PBS backup job (whole VM, daily). Enable the QEMU guest agent on the VM so a backup can freeze the file systems.
- [ ] The test stack is removed (`/opt/stacks/mail-archive-test`), or at least stopped, so nothing confuses the two.
- [ ] A password manager for: the admin password you will choose, the Gmail app password, and the database password.

## 2. Create the stack

```
sudo mkdir -p /opt/stacks/mail-archive && sudo chown "$USER": /opt/stacks/mail-archive
cd /opt/stacks/mail-archive
mkdir -p originals pgdata logs keys manifests
# Fetch the compose file from a COMMIT (or a release tag), never from the branch name: a branch moves, and the file must match the image.
# REF = the full 40-character commit you deploy (the image tag sha-xxxxxxx is its first 7 characters). Find it, for example, with:
#   git ls-remote https://github.com/thatgoodpickle/mail-archiver integrity
REF=<full commit hash or tag>
curl -fsSL "https://raw.githubusercontent.com/thatgoodpickle/mail-archiver/$REF/deploy/compose.production.yml" -o compose.yml

umask 077
printf 'DB_PASSWORD=%s\nADMIN_PASSWORD=%s\nBIND_ADDR=%s\nAPP_PORT=5555\n' \
  "$(openssl rand -hex 24)" "$(openssl rand -hex 24)" "<the VM's LAN IP>" > .env
umask 022

docker compose up -d
docker compose logs --tail 30 mailarchiver
```
Use the VM's own LAN address for `BIND_ADDR` (for example `10.0.0.5`), so the app is not published on every interface: this is the
real access control. **Do not rely on ufw for this port:** Docker publishes ports through its own iptables rules, which are evaluated
before ufw, so a ufw rule such as "allow from the LAN to 5555" neither opens nor closes a Docker-published port. If you need more
than `BIND_ADDR` (for example only one workstation may connect), limit it on the router or VLAN, or add rules to the `DOCKER-USER`
iptables chain (the chain Docker leaves for your own rules; it sees the container port after address translation, so match the
original port with `-m conntrack --ctorigdstport 5555`). Check the port is free (`ss -ltn | grep 5555`) and change `APP_PORT` if not.

**Pin both images by digest** (a registry tag can be pushed again; a digest cannot). The compose file reads two optional values from
`.env`. Fetch the digests yourself, once, on a machine you trust, and append them (the values below are placeholders; this guide
contains no real digests):
```
docker buildx imagetools inspect ghcr.io/thatgoodpickle/mailarchiver:sha-741adab --format '{{.Manifest.Digest}}'
docker buildx imagetools inspect postgres:17-alpine --format '{{.Manifest.Digest}}'
printf 'APP_DIGEST=@sha256:<digest of the app image>\nPOSTGRES_DIGEST=@sha256:<digest of the postgres image>\n' >> .env
```
Leave them out and the stack runs on the tags alone (acceptable for the rehearsal, not for production). On an upgrade (section 9)
fetch the new app digest and change the tag and `APP_DIGEST` together. Take a new Postgres digest only on purpose, within major version 17.

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
7. **The app only reads from Gmail during a sync** (folders are opened read-only, so nothing is marked as read or changed). Three actions
   WRITE to the mailbox, and must not be used on a real account: the account's "delete after days" and "local retention days" options, and
   "Copy to Mailbox" (restore). Leave all three alone.

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

Raise Gmail's cap to 5,000, then 10,000, then "Do not limit". **After EVERY raise, open the account's page and press "Full resync".**
This is required: the ordinary sync only looks for mail delivered after the last sync (minus 12 hours), and a raised cap reveals OLDER mail,
which that search never finds. Without the Full resync the account looks complete while thousands of older messages are missing. A Full
resync does not download what is already archived again (the sync checks the Message-ID and size first). Watch the daily download limit: the sync pauses at 2.5 GB and resumes by
itself the next day, so a 5 GB mailbox takes about three days. **Run the coverage check** before you call it complete (replaces the manual rule of comparing the filter's kept count with the archived count): on
the account's page press **Coverage check**, then **Run the coverage check**. It asks Gmail for the identity of every message in All Mail (no text is
downloaded, nothing is changed in the mailbox) and sorts each message into exactly one group: archived, deleted by you, set aside after failing,
filtered out by the Gmail search filter, not yet seen. The groups must add up to the messages on the server, and **"not yet seen" must be 0**. If it is not,
the page lists those messages with a Gmail search link each: press Full resync again and read the log. Mail that arrived after the last sync shows up there
too. The check covers only the synced folders (Spam and Trash are outside it) and proves the archive holds what Gmail's IMAP served, not that Google's copy
was byte for byte the same. See `DeleteMemory.md`. Only when the whole mailbox is in, the counts
match and the checks are clean: take a manual PBS backup
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
`sha-xxxxxxx` tag (the CI run of that commit must be green), set the matching `APP_DIGEST` in `.env` (section 2) and fetch the compose file of
that same commit (section 2, `REF`) if it changed. 4. `docker compose pull && docker compose up -d`; watch the log; run both
checks on the page. Rollback = restore the PBS backup (do not run an older image against a database a newer one has migrated).

## 10. Known limits (stated plainly)

- The audit checks protect against mistakes, bugs, a changed file or row and people without the app's database login or host access;
  someone with root on the VM can change everything together (see `doc/INTEGRITY-DESIGN.md`, the exact claim).
- Labels are Gmail's metadata, not part of the original, and are refreshed daily: they are "as of the last sync".
- The claim for a Gmail account reads: everything in the mailbox except mail matching the recorded filter (`-label:Marketing`).
- Not covered: Microsoft 365 (Graph) quarantine, TIFF/fax scans (marked failed with a reason), a GPU build, misread OCR words.
