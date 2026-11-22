#!/usr/bin/env python3
"""Scores OCR output against the known text made by tools/make-ocr-samples.py.

Usage: score-ocr.py <samples-dir> <ocr-output-dir>
For each <name>.txt (truth) in the samples dir, reads <name>.txt from the output dir (what the engine returned)
and prints: CER (character error rate), WER (word error rate), and number recall (share of the digit-containing
tokens, such as prices and dates, that came out exactly). Whitespace and line breaks are ignored; case, accents
and punctuation count. Lower CER/WER is better; number recall 100% is what matters most for invoices.
Python 3 standard library only.
"""
import os, re, sys

def norm(t): return re.sub(r"\s+", " ", t).strip()

def lev(a, b):
    if len(a) < len(b): a, b = b, a
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]

def main():
    if len(sys.argv) != 3: sys.exit(__doc__)
    sd, od = sys.argv[1], sys.argv[2]
    rows, tot = [], [0, 0, 0, 0, 0, 0]
    for f in sorted(os.listdir(sd)):
        if not f.endswith(".txt"): continue
        truth = norm(open(os.path.join(sd, f), encoding="utf-8").read())
        op = os.path.join(od, f)
        got = norm(open(op, encoding="utf-8").read()) if os.path.exists(op) else ""
        ce = lev(truth, got); we = lev(truth.split(), got.split())
        nums = [w for w in truth.split() if re.search(r"\d", w)]
        gw = set(got.split()); hit = sum(1 for w in nums if w in gw)
        rows.append((f[:-4], ce / max(1, len(truth)), we / max(1, len(truth.split())), hit, len(nums), os.path.exists(op)))
        tot[0] += ce; tot[1] += len(truth); tot[2] += we; tot[3] += len(truth.split()); tot[4] += hit; tot[5] += len(nums)
    print(f"{'case':<22}{'CER':>8}{'WER':>8}{'numbers':>10}")
    for n, c, w, h, t, ok in rows:
        print(f"{n:<22}{c:>7.1%}{w:>8.1%}{(str(h)+'/'+str(t)) if t else '-':>10}{'' if ok else '   (no output)'}")
    print(f"{'ALL':<22}{tot[0]/max(1,tot[1]):>7.1%}{tot[2]/max(1,tot[3]):>8.1%}{(str(tot[4])+'/'+str(tot[5])):>10}")

main()
