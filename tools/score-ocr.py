#!/usr/bin/env python3
"""Scores OCR output against the known text made by tools/make-ocr-samples.py.

Usage: score-ocr.py [--ignore-missing] <samples-dir> <ocr-output-dir>
For each <name>.txt (truth) in the samples dir, reads <name>.txt from the output dir (what the engine returned)
and prints: CER (character error rate), WER (word error rate), number recall (share of the digit-containing
tokens, such as prices and dates, that came out exactly) and word recall (share of the expected words found,
ignoring order: the fair measure for multi-column layouts and screenshots, where reading order is ambiguous). Whitespace and line breaks are ignored; case, accents
and punctuation count. Lower CER/WER is better; number recall 100% is what matters most for invoices.
--ignore-missing leaves out cases for which the engine wrote no output file (use it for image-only engines, which
cannot read the scan PDF); without it a missing output counts as everything wrong.
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
    args = [a for a in sys.argv[1:] if a != "--ignore-missing"]
    ignore = len(args) != len(sys.argv) - 1
    if len(args) != 2: sys.exit(__doc__)
    sd, od = args
    rows, tot = [], [0, 0, 0, 0, 0, 0, 0, 0]
    for f in sorted(os.listdir(sd)):
        if not f.endswith(".txt"): continue
        truth = norm(open(os.path.join(sd, f), encoding="utf-8").read())
        op = os.path.join(od, f)
        if ignore and not os.path.exists(op):
            continue
        got = norm(open(op, encoding="utf-8").read()) if os.path.exists(op) else ""
        ce = lev(truth, got); we = lev(truth.split(), got.split())
        nums = [w for w in truth.split() if re.search(r"\d", w)]
        gw = set(got.split()); hit = sum(1 for w in nums if w in gw)
        from collections import Counter
        have, want = Counter(got.split()), Counter(truth.split())
        found = sum(min(c, have[w]) for w, c in want.items())
        rows.append((f[:-4], ce / max(1, len(truth)), we / max(1, len(truth.split())), hit, len(nums), os.path.exists(op), found / max(1, len(truth.split()))))
        tot[0] += ce; tot[1] += len(truth); tot[2] += we; tot[3] += len(truth.split()); tot[4] += hit; tot[5] += len(nums); tot[6] += found; tot[7] += len(truth.split())
    print(f"{'case':<28}{'CER':>8}{'WER':>8}{'numbers':>10}{'words':>8}")
    for n, c, w, h, t, ok, wr in rows:
        print(f"{n:<28}{c:>7.1%}{w:>8.1%}{(str(h)+'/'+str(t)) if t else '-':>10}{wr:>8.1%}{'' if ok else '   (no output)'}")
    print(f"{'ALL':<28}{tot[0]/max(1,tot[1]):>7.1%}{tot[2]/max(1,tot[3]):>8.1%}{(str(tot[4])+'/'+str(tot[5])):>10}{tot[6]/max(1,tot[7]):>8.1%}")

main()
