#!/usr/bin/env python3
"""Reference implementation of the OCR pipeline the C# engine ports (PP-OCRv5 mobile det + latin rec, ONNX Runtime).

Not used by the application. Exists so the C# port has something exact to be compared with, and so the settings
below (taken from the models' own inference.yml, confirmed by measurement) stay documented in code:
  det: long side capped at 960 px (no upscale), sides rounded to multiples of 32, BGR, (x/255 - mean) / std with
       ImageNet mean/std applied channel by channel, DB post-processing thresh 0.3, box_thresh 0.6, unclip 1.5
  rec: crop height 48, width 48*w/h, (x/255 - 0.5)/0.5, CTC argmax, blank = 0, dict (836) then space = 838 classes
Usage (needs numpy, opencv-python, onnxruntime; models in ./models as pp5_mobile_det.onnx, latin5_rec.onnx,
latin5_dict.txt):  ocr-reference.py yml <images-dir> <out-dir>
Measured 2026-11-22 (2 threads): synthetic set CER 4.0%, WER 5.1%, numbers 59/59, word recall 99.7%; real screenshots
word recall 99.5%; 0.17 s (synthetic) to 0.44 s (screenshots) per image.
"""
import os, sys, math, time, json
import numpy as np, cv2, onnxruntime as ort
os.environ.setdefault("OMP_NUM_THREADS", "2")
M = "models"
so = ort.SessionOptions(); so.intra_op_num_threads = 2; so.inter_op_num_threads = 1; so.log_severity_level = 3
det = ort.InferenceSession(f"{M}/pp5_mobile_det.onnx", so, providers=["CPUExecutionProvider"])
rec = ort.InferenceSession(f"{M}/latin5_rec.onnx", so, providers=["CPUExecutionProvider"])
chars = ["<blank>"] + open(f"{M}/latin5_dict.txt", encoding="utf-8").read().split("\n")
if chars[-1] == "": chars.pop()
chars.append(" ")
assert len(chars) == 838, len(chars)

def det_prep(img, cfg):
    h, w = img.shape[:2]
    if cfg["limit_type"] == "min":
        r = cfg["side"] / min(h, w) if min(h, w) < cfg["side"] else 1.0
    else:  # max: shrink/grow so the long side == side
        r = cfg["side"] / max(h, w) if (max(h, w) > cfg["side"] or cfg.get("grow")) else 1.0
    nh, nw = max(32, int(round(h * r / 32) * 32)), max(32, int(round(w * r / 32) * 32))
    x = cv2.resize(img, (nw, nh), interpolation=cv2.INTER_LINEAR).astype(np.float32) / 255.0
    mean = np.array(cfg["mean"], np.float32); std = np.array(cfg["std"], np.float32)
    x = (x - mean) / std
    return x.transpose(2, 0, 1)[None], (h / nh, w / nw)

def boxes_from_map(p, cfg, ratios):
    bm = (p > cfg["thresh"]).astype(np.uint8)
    if cfg.get("dilate"): bm = cv2.dilate(bm, np.ones((2, 2), np.uint8))
    cnts, _ = cv2.findContours(bm, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    out = []
    for c in cnts[:1000]:
        rect = cv2.minAreaRect(c); w, h = rect[1]
        if min(w, h) < 3: continue
        mask = np.zeros(p.shape, np.uint8); cv2.fillPoly(mask, [c.reshape(-1, 2)], 1)
        score = float(p[mask == 1].mean()) if mask.any() else 0
        if score < cfg["box_thresh"]: continue
        (cx, cy), (w, h), ang = rect
        d = w * h * cfg["unclip"] / (2 * (w + h))
        rect2 = ((cx, cy), (w + 2 * d, h + 2 * d), ang)
        pts = cv2.boxPoints(rect2)
        if min(rect2[1]) < 5: continue
        out.append(pts)
    return out

def crop(img, pts, ratios):
    pts = pts.copy(); pts[:, 0] *= ratios[1]; pts[:, 1] *= ratios[0]
    s = pts.sum(1); d = np.diff(pts, axis=1)[:, 0]
    tl, br, tr, bl = pts[np.argmin(s)], pts[np.argmax(s)], pts[np.argmin(d)], pts[np.argmax(d)]
    src = np.array([tl, tr, br, bl], np.float32)
    w = int(max(np.linalg.norm(tr - tl), np.linalg.norm(br - bl))); h = int(max(np.linalg.norm(bl - tl), np.linalg.norm(br - tr)))
    if w < 2 or h < 2: return None, src
    dst = np.array([[0, 0], [w, 0], [w, h], [0, h]], np.float32)
    c = cv2.warpPerspective(img, cv2.getPerspectiveTransform(src, dst), (w, h), borderMode=cv2.BORDER_REPLICATE, flags=cv2.INTER_CUBIC)
    if c.shape[0] / c.shape[1] >= 1.5: c = np.rot90(c)
    return c, src

def rec_run(crops):
    res = []
    for c in crops:
        h, w = c.shape[:2]; nw = min(max(int(math.ceil(48 * w / h)), 8), 3200)
        x = cv2.resize(c, (nw, 48)).astype(np.float32) / 255.0
        x = ((x - 0.5) / 0.5).transpose(2, 0, 1)[None]
        o = rec.run(None, {"x": x})[0][0]
        idx = o.argmax(1); pr = o.max(1)
        s = []; prev = 0; confs = []
        for i, p in zip(idx, pr):
            if i != 0 and i != prev: s.append(chars[i]); confs.append(p)
            prev = i
        res.append(("".join(s), float(np.mean(confs)) if confs else 0.0))
    return res

def ocr(path, cfg):
    img = cv2.imdecode(np.fromfile(path, np.uint8), cv2.IMREAD_COLOR)  # BGR
    if cfg.get("invert_dark") and img.mean() < 110: img = 255 - img
    x, ratios = det_prep(img, cfg)
    p = det.run(None, {"x": x})[0][0, 0]
    boxes = boxes_from_map(p, cfg, ratios)
    items = []
    crops, meta = [], []
    for b in boxes:
        c, src = crop(img, b, ratios)
        if c is None: continue
        crops.append(c); ys = b[:, 1] * ratios[0]; xs = b[:, 0] * ratios[1]
        meta.append(((ys.min() + ys.max()) / 2, xs.min(), ys.max() - ys.min()))
    for (cy, x0, hh), (t, conf) in zip(meta, rec_run(crops) if crops else []):
        if conf < cfg.get("rec_thresh", 0.0) or not t.strip(): continue
        items.append((cy, x0, hh, t))
    items.sort(); lines = []; cur = []; cy = None; ch = 0
    for y, x0, h, t in items:
        if cur and abs(y - cy) < 0.6 * ch: cur.append((x0, t))
        else:
            if cur: lines.append(cur)
            cur, cy, ch = [(x0, t)], y, h
    if cur: lines.append(cur)
    return "\n".join(" ".join(t for _, t in sorted(l)) for l in lines) + "\n"

CONFIGS = {
 "rapid_like": dict(limit_type="min", side=736, mean=[.5,.5,.5], std=[.5,.5,.5], thresh=.3, box_thresh=.5, unclip=1.6, dilate=True),
 "yml":        dict(limit_type="max", side=960, grow=False, mean=[.485,.456,.406], std=[.229,.224,.225], thresh=.3, box_thresh=.6, unclip=1.5, dilate=False),
 "yml_min736": dict(limit_type="min", side=736, mean=[.485,.456,.406], std=[.229,.224,.225], thresh=.3, box_thresh=.6, unclip=1.5, dilate=False),
 "half_min736_b5": dict(limit_type="min", side=736, mean=[.5,.5,.5], std=[.5,.5,.5], thresh=.3, box_thresh=.5, unclip=1.6, dilate=False),
}
if __name__ == "__main__":
    name, src, out = sys.argv[1:4]
    cfg = dict(CONFIGS[name]); 
    for kv in sys.argv[4:]:
        k, v = kv.split("="); cfg[k] = json.loads(v)
    os.makedirs(out, exist_ok=True); tot = 0; n = 0
    for f in sorted(os.listdir(src)):
        if not f.lower().endswith((".png", ".jpg")): continue
        t = time.time(); txt = ocr(os.path.join(src, f), cfg); tot += time.time() - t; n += 1
        open(os.path.join(out, os.path.splitext(f)[0] + ".txt"), "w", encoding="utf-8").write(txt)
    print(f"{name} avg {tot/max(1,n):.2f}s/img")
