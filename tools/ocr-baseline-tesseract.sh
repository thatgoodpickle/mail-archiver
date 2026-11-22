#!/usr/bin/env bash
# Baseline: runs Tesseract over the sample images and scores it. Usage: ocr-baseline-tesseract.sh <samples-dir> <out-dir>
# Needs `tesseract` (with the eng data). Image files only (the scan PDF is skipped: Tesseract cannot read PDFs).
set -euo pipefail
sd=${1:?samples dir}; od=${2:?output dir}
mkdir -p "$od"
for img in "$sd"/*.png "$sd"/*.jpg; do
  [ -e "$img" ] || continue
  n=$(basename "${img%.*}")
  tesseract "$img" "$od/$n" -l eng >/dev/null 2>&1
done
"$(dirname "$0")/score-ocr.py" "$sd" "$od"
