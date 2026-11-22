#!/usr/bin/env bash
# Downloads the two OCR models the attachment-text engine uses and checks their SHA-256. Used by the Dockerfile
# and by CI; single source of truth for the pinned revisions. Usage: fetch-ocr-models.sh <target-dir>
#   det.onnx  PaddlePaddle/PP-OCRv5_mobile_det_onnx         (Apache-2.0, 4.8 MB)
#   rec.onnx  PaddlePaddle/latin_PP-OCRv5_mobile_rec_onnx   (Apache-2.0, 8.0 MB)
# Revisions are Hugging Face commit hashes, so a moved branch cannot change what is downloaded; the hash check
# stops anything else. Needs curl and sha256sum. Re-running is harmless (files that already verify are kept).
set -euo pipefail

dir=${1:?target directory}
mkdir -p "$dir"

DET_URL="https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det_onnx/resolve/e6f4fa85f00e168c862bc462aebca69eef9b3d3d/inference.onnx"
DET_SHA="a431985659dc921974177a95adcfbb90fd9e51989a5e04d70d0b75f597b6e61d"
REC_URL="https://huggingface.co/PaddlePaddle/latin_PP-OCRv5_mobile_rec_onnx/resolve/89d3a50e2c27e2e7cceeab0e944c25c807d5db4f/inference.onnx"
REC_SHA="7888113072263cb471b93f66dd5e2ad70548dc526fa1ace760d0d973dd121498"

fetch() {  # url sha file
  if [ -f "$3" ] && echo "$2  $3" | sha256sum -c --status 2>/dev/null; then echo "ok (kept): $3"; return; fi
  curl -fsSL --retry 3 --retry-delay 2 "$1" -o "$3.part"
  echo "$2  $3.part" | sha256sum -c --status || { echo "checksum mismatch for $1" >&2; rm -f "$3.part"; exit 1; }
  mv "$3.part" "$3"
  echo "ok: $3"
}

fetch "$DET_URL" "$DET_SHA" "$dir/det.onnx"
fetch "$REC_URL" "$REC_SHA" "$dir/rec.onnx"
