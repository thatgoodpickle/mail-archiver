#!/usr/bin/env python3
"""Makes synthetic test images with KNOWN text, so any OCR engine can be scored exactly.

Usage: make-ocr-samples.py <out-dir>
Writes, per case, <name>.png (some also .jpg / a scan-like .pdf) and <name>.txt = the exact expected text in
reading order. Everything is invented text (fake names, fake numbers); nothing is real. Deterministic (fixed seed).
Requires Pillow and numpy and the usual DejaVu/Liberation/Noto fonts (paths below; missing fonts fall back to DejaVu).
Score an engine's output with tools/score-ocr.py.
"""
import os, random, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageOps

random.seed(7); np.random.seed(7)
FONTS = {
    "sans": "/usr/share/fonts/TTF/DejaVuSans.ttf",
    "sans-bold": "/usr/share/fonts/liberation/LiberationSans-Bold.ttf",
    "serif": "/usr/share/fonts/noto/NotoSerif-Regular.ttf",
    "serif-italic": "/usr/share/fonts/liberation/LiberationSerif-Italic.ttf",
    "mono": "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
}
def font(kind, size):
    try: return ImageFont.truetype(FONTS[kind], size)
    except OSError: return ImageFont.truetype(FONTS["sans"], size)

def page(lines, kind="sans", size=28, w=1240, h=None, fg=0, bg=255, margin=70, gap=1.5):
    f = font(kind, size); lh = int(size * gap)
    h = h or (margin * 2 + lh * len(lines))
    im = Image.new("L", (w, h), bg); d = ImageDraw.Draw(im); y = margin
    for ln in lines:
        d.text((margin, y), ln, font=f, fill=fg); y += lh
    return im

def noisy(im, sigma=18, blur=1.1, tilt=0.0, shade=True):
    a = np.asarray(im, dtype=np.float32)
    if shade:  # uneven lighting, like a phone photo
        h, w = a.shape
        a = a * (0.72 + 0.28 * np.linspace(0, 1, w)[None, :]) * (0.85 + 0.15 * np.linspace(1, 0, h)[:, None])
    a += np.random.normal(0, sigma, a.shape)
    im = Image.fromarray(np.clip(a, 0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(blur))
    return im.rotate(tilt, expand=True, fillcolor=235) if tilt else im

cases = {}

cases["01_clean_print"] = (page([
    "Meeting notes, Tuesday 14 October 2026",
    "Attendees: Alice Example, Bob Sample, Carla Test.",
    "The committee approved the budget of 48,250 dollars for",
    "the harbour renovation. Work starts on 3 November and",
    "should finish before the end of February. Questions go",
    "to project@example.test or call 555 0142.",
]), "png")

cases["02_small_text"] = (page([
    "Terms and conditions (summary). The supplier delivers the goods within 14 days of",
    "payment. Returns are accepted for 30 days if the packaging is unopened. Claims for",
    "damage in transit must be reported in writing within 5 working days of delivery.",
    "Reference: TC-2026-0173. Valid from 1 January 2026.",
], size=15, w=1240, margin=50, gap=1.4), "png")

cases["03_invoice_table"] = (page([
    "INVOICE No. 2026-00417            Date: 12/11/2026",
    "Customer: Example Trading Pty Ltd   ABN 12 345 678 901",
    "",
    "Item                       Qty      Unit      Total",
    "Harbour survey               2    450.00     900.00",
    "Site inspection              5    120.50     602.50",
    "Report printing             10      8.75      87.50",
    "",
    "Subtotal                                   1,590.00",
    "GST 10%                                      159.00",
    "TOTAL DUE                                  1,749.00",
    "Pay by 12/12/2026. Account 062-000 / 1234 5678",
], kind="mono", size=24, margin=60), "png")

cases["04_skewed_4deg"] = (page([
    "Delivery note 88213",
    "Two pallets of timber, one crate of fittings.",
    "Signed on arrival by R. Sample at 09:40.",
    "Please keep this copy for your records.",
], size=30).rotate(4, expand=True, fillcolor=255), "png")

cases["05_noisy_photo"] = (noisy(page([
    "Receipt - Corner Hardware",
    "Cable ties 100 pack        6.90",
    "Wood screws 4.0x40        12.40",
    "Masking tape               3.25",
    "TOTAL                     22.55",
    "Thank you for shopping with us.",
], kind="sans-bold", size=30, fg=20), tilt=-2.0), "jpg")

cases["06_accents"] = (page([
    "Café Zürich, Mañana, Ñandú, Škoda, Ångström, Straße.",
    "Crème brûlée costs 9,50 € and naïve résumés are fine.",
    "Prices: £12.40, $8.15, ¥1,200, 5 % discount.",
], size=30), "png")

cases["07_serif_mixed"] = (page([
    "A Letter of Reference",
    "To whom it may concern, I have known Jane Sample for",
    "six years and can recommend her without reservation.",
], kind="serif", size=30), "png")

cases["08_low_contrast"] = (page([
    "Faded carbon copy of the order form.",
    "Order 55-9031 for 12 units, delivery Friday.",
    "Authorised by M. Test, department 4.",
], size=30, fg=150, bg=225), "png")

cases["09_white_on_dark"] = (page([
    "STATION CLOSED",
    "Platform 2 is closed until 06:00 on Monday.",
    "Please use platform 4 for all services.",
], kind="sans-bold", size=34, fg=245, bg=25), "png")

cases["10_id_card_fake"] = (page([
    "SAMPLE STATE   DRIVER LICENCE (SPECIMEN)",
    "Surname: SAMPLE",
    "Given names: JANE ALICE",
    "Licence no: 123456789",
    "Date of birth: 01/02/1990",
    "Expiry: 30/06/2029",
    "Address: 7 Test Street, Exampletown 2000",
], kind="mono", size=24, w=900, margin=40, bg=235), "png")

# two columns, read left column first
left = ["Left column starts here.", "It talks about the first topic", "in a few short lines of text."]
right = ["Right column follows.", "It covers the second topic", "and ends the page."]
im = Image.new("L", (1240, 300), 255); d = ImageDraw.Draw(im); f = font("sans", 26)
for i, t in enumerate(left): d.text((60, 60 + i * 44), t, font=f, fill=0)
for i, t in enumerate(right): d.text((660, 60 + i * 44), t, font=f, fill=0)
cases["11_two_columns"] = (im, "png", left + right)

out = sys.argv[1] if len(sys.argv) > 1 else "ocr-samples"
os.makedirs(out, exist_ok=True)
# The expected text, in reading order, with the table's column gaps collapsed to single spaces (the scorer ignores whitespace differences).
EXPECTED = {
    "01_clean_print": ["Meeting notes, Tuesday 14 October 2026", "Attendees: Alice Example, Bob Sample, Carla Test.", "The committee approved the budget of 48,250 dollars for", "the harbour renovation. Work starts on 3 November and", "should finish before the end of February. Questions go", "to project@example.test or call 555 0142."],
    "02_small_text": ["Terms and conditions (summary). The supplier delivers the goods within 14 days of", "payment. Returns are accepted for 30 days if the packaging is unopened. Claims for", "damage in transit must be reported in writing within 5 working days of delivery.", "Reference: TC-2026-0173. Valid from 1 January 2026."],
    "03_invoice_table": ["INVOICE No. 2026-00417 Date: 12/11/2026", "Customer: Example Trading Pty Ltd ABN 12 345 678 901", "Item Qty Unit Total", "Harbour survey 2 450.00 900.00", "Site inspection 5 120.50 602.50", "Report printing 10 8.75 87.50", "Subtotal 1,590.00", "GST 10% 159.00", "TOTAL DUE 1,749.00", "Pay by 12/12/2026. Account 062-000 / 1234 5678"],
    "04_skewed_4deg": ["Delivery note 88213", "Two pallets of timber, one crate of fittings.", "Signed on arrival by R. Sample at 09:40.", "Please keep this copy for your records."],
    "05_noisy_photo": ["Receipt - Corner Hardware", "Cable ties 100 pack 6.90", "Wood screws 4.0x40 12.40", "Masking tape 3.25", "TOTAL 22.55", "Thank you for shopping with us."],
    "06_accents": ["Café Zürich, Mañana, Ñandú, Škoda, Ångström, Straße.", "Crème brûlée costs 9,50 € and naïve résumés are fine.", "Prices: £12.40, $8.15, ¥1,200, 5 % discount."],
    "07_serif_mixed": ["A Letter of Reference", "To whom it may concern, I have known Jane Sample for", "six years and can recommend her without reservation."],
    "08_low_contrast": ["Faded carbon copy of the order form.", "Order 55-9031 for 12 units, delivery Friday.", "Authorised by M. Test, department 4."],
    "09_white_on_dark": ["STATION CLOSED", "Platform 2 is closed until 06:00 on Monday.", "Please use platform 4 for all services."],
    "10_id_card_fake": ["SAMPLE STATE DRIVER LICENCE (SPECIMEN)", "Surname: SAMPLE", "Given names: JANE ALICE", "Licence no: 123456789", "Date of birth: 01/02/1990", "Expiry: 30/06/2029", "Address: 7 Test Street, Exampletown 2000"],
    "11_two_columns": left + right,
}
for name, spec in cases.items():
    im, ext = spec[0], spec[1]
    im = im.convert("L")
    if ext == "jpg":
        im.save(os.path.join(out, name + ".jpg"), quality=55)
    else:
        im.save(os.path.join(out, name + ".png"))
    open(os.path.join(out, name + ".txt"), "w", encoding="utf-8").write("\n".join(EXPECTED[name]) + "\n")

# a scan-like PDF (image only, no text layer) from the invoice case
cases["03_invoice_table"][0].convert("L").save(os.path.join(out, "12_invoice_scan.pdf"), resolution=150)
open(os.path.join(out, "12_invoice_scan.txt"), "w", encoding="utf-8").write("\n".join(EXPECTED["03_invoice_table"]) + "\n")
print(f"wrote {len(cases) + 1} cases to {out}")
