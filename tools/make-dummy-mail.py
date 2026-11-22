#!/usr/bin/env python3
"""Loads synthetic test emails into a test IMAP server (greenmail) for trying lean storage and attachment text.

Usage: make-dummy-mail.py [host] [port]      (defaults 127.0.0.1 3143; user test / testpass, no TLS)
Synthetic data only. Each run creates new Message-IDs, so every run adds new emails. Unique words to search for:
  kumquatinvoice   inside a PDF with a text layer
  rhubarbcontract  inside a .docx
  persimmonnotes   inside a .txt attachment
  pineapples       in a plain text body
  zeppelin, harmonica, kumquat   drawn INSIDE a picture (found only when the OCR engine is on)
  harpsichord      drawn inside the picture of a scanned PDF (only found with OCR)
The 1x1 "photo.png" and the blank scan PDF have no text: they stay NeedsOcr, or become Done with empty text once
the OCR engine has looked at them (the 1x1 image is too small to read and is Failed with that reason).
Python 3 standard library only.
"""
import imaplib, io, os, sys, time, zipfile
from email.message import EmailMessage
from email.utils import formatdate, make_msgid

def pdf(text=None):
    """A minimal valid one-page PDF. With text it has a text layer; without, the page is empty (like a scan)."""
    stream = b"BT /F1 18 Tf 72 700 Td (" + text.encode() + b") Tj ET" if text else b""
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream",
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    out = io.BytesIO(); out.write(b"%PDF-1.4\n"); offs = []
    for i, o in enumerate(objs, 1):
        offs.append(out.tell()); out.write(b"%d 0 obj\n" % i + o + b"\nendobj\n")
    x = out.tell()
    out.write(b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1))
    for o in offs: out.write(b"%010d 00000 n \n" % o)
    out.write(b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, x))
    return out.getvalue()

def docx(text):
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr("_rels/.rels", '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        z.writestr("word/document.xml", '<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>' + text + '</w:t></w:r></w:p></w:body></w:document>')
    return b.getvalue()

# 1x1 PNG: an image has no text layer, so it is queued for OCR
PNG = bytes.fromhex("89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000d4944415478da63f8cfc0f01f0005000201a2d6b2a70000000049454e44ae426082")

import base64
TEXT_PNG = base64.b64decode("""
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
""")
SCAN_JPG = base64.b64decode("""
/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAoHBwgHBgoICAgLCgoLDhgQDg0NDh0VFhEYIx8lJCIfIiEmKzcvJik0KSEiMEExNDk7
Pj4+JS5ESUM8SDc9Pjv/wAALCAClBEwBAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF
BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW
V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi
4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/APZqKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKzPEmuQeG/D15rFwheO1j3bAcFmJAUZ7ZJArzzT7n4t+KLOPV7G
90zSrS4XzIIZEBLIeh5RzyPUiuv8HHxmpvIfFq2bhNn2ae2x+8zu3ZA9Pl7DrXT0UUUUUUUUUUVW1FmTTbp0YqywuQQcEHBrlPhL
f3mpeAbW5v7ue7naWUGWeQuxAc45PNdpXB+G9Rvp/i14ospr24ktYIYTFA8rGOMlVztUnA/Cu8ork/H3iq+8LWuly2MVvI15fpbS
eerEBSDkjBHPFdZXJ+CPFV94lutdivYreNdOv2tovJVgWUE8tknnjtiusoooopGztO04OOK8wm0f4xwQSTN4s0krGpYgRLnAGf8A
nhWT4VvPiv4v0f8AtTT/ABPYRQ+a0e24gjVsjGekJGOfWtzxjN4q0P4TXEmraxu1dJ0zdWLmL5TIMAFVXtx0r0DTHaTSrN3YszQI
WYnJJ2jmrVFFFFFFFed/EDUtcvfFei+ENE1JtMbUEeWa5T7wVQTgEcjhW6EZ45rpvCPh/UPDumzWupa/c61LJMZFnuN25BgDaNzN
xwT171vUUUUUUUUUUUUVleJdJu9c0ObT7HVZtKnkKlbuDO9MMCcYZTyBjr3rQtYngtIYZJWmeONVaRurkDBJ+tS0VheN7ie08E6z
cW00kM0dpIySRsVZTjqCOQaj8BXM954G0e5up5J5pLZWeSVyzMfUk8muhoooooooorFk0S+fxhFrY1q4WzS28k6aN3ls2T8/3sZ5
/u9utbVFcF4w1G+tviV4RtLe9uIbe4eXzoY5WVJcYxuUHB/Gu9oooooqtqLMmm3ToxVlhcgg4IODXKfCW/vNS8A2tzf3c93O0soM
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
ncrA8fmR+NcRHqkPjbx5oV74Q8PXGnCzuBJeXxiWMPHkblbbkHgMOTk5xXTXX/Jf7P8A7A5/m9R+PtP1jQ/F+neOdGsWv1toTb3l
sgJYpzzxz0Y89sA9M0lv8atOv5orWw8P6tPduwUxbFwpJ9QSf0qTxv8A8lU8Ff78v9K6bx7/AMiFrn/XjL/6Ca5i08Py+J/gZZ6V
bkC4ks0eHJwC6tuA/HGPxrL0n4vf2BpMGj+INA1KPVLSMQ7UQAS7RgE5IIzjsD7Vr/DTStXudX1nxjrVo1nNqzBYLdgQyxjuQecc
KBnrjPcU3wj/AMlg8Y/7kP8AIVN8UNJ1XztH8UaLbG6udFmLyQKCS8ZwTwOTjGDjnDE9qy7r4sP4k0u40vw/oGoHUpoWR2mVRFbg
j5mLA9hnqB/Stn4M/wDJOLP/AK7S/wDoZru683ufgZ4YurqW4e/1YPK5dgs0eMk54/d1xcHwy0WX4p3HhVrq/wDsUVmJ1kEiebuw
vGduMcntXpnij4aaL4seya/ur6I2UPkx+RIgyvvlTzXnHxG+F2h+EPC/9qafdX8s32hI9txIjLg5z0QHPHrXqXgfwfp/hDSWi0+a
5lW7KzP9oZWIbaOmFHFcx8TZH8R+JtA8D27HbczC6vNp+7GM/wBA5/75r0pESKNY41CogAVQOAB2rmPGHxB0nwTNaxalb3kzXSsy
fZkRgNuM53MPWvLPAPxN0Xws2sG+tb+T7feGeLyI0OFOeDlxzz2zXsXiTxGvh/wpc6+tqblYY0kEJfYWDMB1wcfe9DVnw/qv9u6B
Y6r5Hkfa4Vl8rfu2ZHTOBn8q0aKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK4v4d+H9U0KfxA2pWvkC81F54P3itvQk4Pyk4+h5rtKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK
KKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKKK/9k=
""")

def scan_pdf(jpeg, w=1100, h=165):
    """A one-page PDF that is only a full-page JPEG, like a scanner makes."""
    out = io.BytesIO(); out.write(b"%PDF-1.4\n"); offs = []
    content = b"q %d 0 0 %d 0 0 cm /Im0 Do Q" % (w, h)
    def obj(n, head, stream=None):
        offs.append(out.tell()); out.write(b"%d 0 obj\n" % n + head)
        if stream is not None: out.write(b"\nstream\n" + stream + b"\nendstream")
        out.write(b"\nendobj\n")
    obj(1, b"<< /Type /Catalog /Pages 2 0 R >>")
    obj(2, b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    obj(3, b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>" % (w, h))
    obj(4, b"<< /Length %d >>" % len(content), content)
    obj(5, b"<< /Type /XObject /Subtype /Image /Width %d /Height %d /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode /Length %d >>" % (w, h, len(jpeg)), jpeg)
    x = out.tell(); out.write(b"xref\n0 %d\n0000000000 65535 f \n" % (len(offs) + 1))
    for o in offs: out.write(b"%010d 00000 n \n" % o)
    out.write(b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(offs) + 1, x))
    return out.getvalue()

def base(subject, n):
    m = EmailMessage()
    m["From"] = "Alice Test <alice@example.test>"; m["To"] = "test@example.test"
    m["Subject"] = subject; m["Date"] = formatdate(time.time() - n * 3600)
    m["Message-ID"] = make_msgid(domain="example.test")
    return m

def mail(subject, n, body, att=None):
    m = base(subject, n); m.set_content(body)
    if att:
        data, mt, st, name = att
        m.add_attachment(data, maintype=mt, subtype=st, filename=name)
    return m

msgs = [
    mail("Plain text hello", 1, "Hello, a plain text dummy message about pineapples."),
    mail("PDF with text layer", 2, "See attached.", (pdf("kumquatinvoice total due"), "application", "pdf", "invoice.pdf")),
    mail("Word contract", 3, "Contract attached.", (docx("rhubarbcontract between the parties"), "application", "vnd.openxmlformats-officedocument.wordprocessingml.document", "contract.docx")),
    mail("Text notes", 4, "Notes attached.", (b"persimmonnotes from the meeting\n", "text", "plain", "notes.txt")),
    mail("Image needing OCR", 5, "A photo.", (PNG, "image", "png", "photo.png")),
    mail("Photo with words", 7, "A picture with writing in it.", (TEXT_PNG, "image", "png", "words.png")),
    mail("Scanned letter", 8, "A scan.", (scan_pdf(SCAN_JPG), "application", "pdf", "letter.pdf")),
    mail("Blank scan PDF", 6, "A scan.", (pdf(None), "application", "pdf", "scan.pdf")),
]

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 3143
c = imaplib.IMAP4(host, port); c.login("test", "testpass")
for m in msgs:
    c.append("INBOX", "", imaplib.Time2Internaldate(time.time()), m.as_bytes())
c.logout()
print(len(msgs), "messages loaded")
