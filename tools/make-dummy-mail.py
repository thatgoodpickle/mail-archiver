#!/usr/bin/env python3
"""Loads synthetic test emails into a test IMAP server (greenmail) for trying lean storage and attachment text.

Usage: make-dummy-mail.py [host] [port]      (defaults 127.0.0.1 3143; user test / testpass, no TLS)
Synthetic data only. Each run creates new Message-IDs, so every run adds new emails. Unique words to search for:
  kumquatinvoice   inside a PDF with a text layer
  rhubarbcontract  inside a .docx
  persimmonnotes   inside a .txt attachment
  pineapples       in a plain text body
The image attachment and the "blank scan" PDF have no text: the attachment-text worker marks them NeedsOcr.
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
    mail("Blank scan PDF", 6, "A scan.", (pdf(None), "application", "pdf", "scan.pdf")),
]

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 3143
c = imaplib.IMAP4(host, port); c.login("test", "testpass")
for m in msgs:
    c.append("INBOX", "", imaplib.Time2Internaldate(time.time()), m.as_bytes())
c.logout()
print(len(msgs), "messages loaded")
