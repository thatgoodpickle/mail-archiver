#!/usr/bin/env bash
# Rewrites repository links (github.com/s1t5/mail-archiver) in README.md and doc/ to the fork.
# Idempotent. Docs-site links (s1t5.github.io), copyright credits, sponsor links and the
# upstream Docker Hub image name are deliberately left alone.
# Run after merging upstream, instead of carrying a conflict-prone commit across ~90 doc files:
#   git fetch upstream && git merge upstream/main && tools/repoint-to-fork.sh
set -euo pipefail
cd "$(dirname "$0")/.."
FROM='https://github.com/s1t5/mail-archiver'
TO='https://github.com/thatgoodpickle/mail-archiver'
files=$(git grep -l -F "$FROM" -- README.md doc || true)
[ -z "$files" ] && { echo "nothing to rewrite"; exit 0; }
echo "$files" | while read -r f; do
  # keep the fork-notice sentence that names upstream
  sed -i "s#${FROM}#${TO}#g" "$f"
done
# restore the upstream link inside the README fork notice
sed -i "s#fork of \[s1t5/mail-archiver\](${TO})#fork of [s1t5/mail-archiver](${FROM})#" README.md
echo "rewritten: $(echo "$files" | wc -l) files"
