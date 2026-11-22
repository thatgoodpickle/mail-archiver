#!/usr/bin/env bash
# Collects the audit chain head ("anchor") from the archive host and keeps it on THIS machine, so a
# later rewrite of the chain on the archive host can be detected. Run it from a machine the archive host
# cannot log in to (cron/systemd timer, e.g. daily). It only reads; it changes nothing on the archive.
#
# Usage:  pull-anchor.sh <base-url> <anchors-file>
#   <base-url>      e.g. http://clio:5555
#   <anchors-file>  append-only log kept here; commit/copy it wherever you like (git, backup)
# Secret: the admin API key is read from the file named by ANCHOR_KEY_FILE (chmod 600). It is never put on
#         a command line: curl gets the header on stdin.
#
# Checks before it appends (exit 1 and no append when one fails):
#   - the manifest public-key fingerprint is the one stored before (pinned once a manifest exists)
#   - the chain verifies (GET chain/verify -> "ok":true)
#   - the new head is not behind the last stored one, and an equal seq has the same hash
#   - the archive STILL holds the stored head's hash at that sequence number (GET chain/entries/N).
#     This is the check that catches a rewritten chain: it can verify on its own and still differ here.
set -euo pipefail

base=${1:?base url}; file=${2:?anchors file}
keyfile=${ANCHOR_KEY_FILE:?set ANCHOR_KEY_FILE to a file holding the API key}
[ -r "$keyfile" ] || { echo "cannot read $keyfile" >&2; exit 2; }

get() {  # $1 = path ; header via stdin so the key never shows in the process list
  printf 'Authorization: Bearer %s\n' "$(tr -d '\r\n' < "$keyfile")" | curl -fsS -H @- "$base/api/v1/integrity/$1"
}

verify=$(get chain/verify)
case $verify in *'"ok":true'*) ;; *) echo "CHAIN DOES NOT VERIFY: $verify" >&2; exit 1 ;; esac

line=$(get chain/anchor | tr -d '\r')
seq=$(sed -n 's/.* seq=\([0-9]*\) .*/\1/p' <<<"$line")
hash=$(sed -n 's/.* hash=\([0-9a-f]*\) .*/\1/p' <<<"$line")
[ -n "$seq" ] && [ -n "$hash" ] || { echo "unexpected anchor: $line" >&2; exit 1; }

if [ -s "$file" ]; then
  last=$(tail -n 1 "$file")
  lseq=$(sed -n 's/.* seq=\([0-9]*\) .*/\1/p' <<<"$last")
  lhash=$(sed -n 's/.* hash=\([0-9a-f]*\) .*/\1/p' <<<"$last")
  if [ "$seq" -lt "$lseq" ]; then echo "ALARM: chain head went BACKWARDS ($lseq -> $seq)" >&2; exit 1; fi
  # The manifest signing key must not change behind our back (a re-keyed, re-signed manifest set would verify).
  # '-' means "no manifest yet"; from then on the fingerprint is pinned.
  pub=$(sed -n 's/.* pubkey=\([0-9a-f-]*\).*/\1/p' <<<"$line")
  lpub=$(sed -n 's/.* pubkey=\([0-9a-f-]*\).*/\1/p' <<<"$last")
  if [ -n "$lpub" ] && [ "$lpub" != "-" ] && [ "$pub" != "$lpub" ]; then
    echo "ALARM: the manifest signing key changed (was $lpub, now $pub)" >&2; exit 1
  fi
  if [ "$seq" -eq "$lseq" ] && [ "$hash" != "$lhash" ]; then echo "ALARM: same seq $seq, different hash" >&2; exit 1; fi
  if [ "$seq" -eq "$lseq" ]; then echo "no change (seq $seq)"; exit 0; fi
  # Ahead of the stored head: the old head must still be in the chain, byte for byte.
  old=$(get "chain/entries/$lseq") || { echo "ALARM: stored head seq $lseq no longer exists in the archive" >&2; exit 1; }
  case $old in *"\"hash\":\"$lhash\""*) ;; *) echo "ALARM: entry $lseq changed: stored $lhash, archive says $old" >&2; exit 1 ;; esac
fi
printf '%s\n' "$line" >> "$file"
echo "stored: $line"
