#!/usr/bin/env python3
"""Compare two signed manifests of the mail archive (for example last month's backup against today).

Usage:  compare-manifests.py <old-manifest-dir> <new-manifest-dir> [--old NAME] [--new NAME]
                              [--pubkey-sha256 HEX] [--allow-key-change]

Each directory is a copy of the archive's Manifests folder (manifest-*.json/.sig/.jsonl and public-key.pem).
Without --old/--new the newest manifest in the directory is used. For each manifest the script first checks
the signature (needs the `openssl` command; skipped with a warning when it is missing) and that the entry list
matches the hash in the signed header, then compares the two entry lists:

  MISSING  an email that was in the old manifest is not in the new one
  CHANGED  the same email id now has a different SHA-256 of its original
  NEW      emails that appeared since (informational)

Exit code 0 = nothing missing or changed, 1 = at least one MISSING/CHANGED or a failed check, 2 = usage/IO error.
Key pinning: the two directories must carry the SAME public key (SHA-256 of its DER form, as the anchor
line's pubkey= shows); with --pubkey-sha256 both must equal that value (take it from your anchor log, not from
the archive). A different key fails unless --allow-key-change is given. Otherwise a re-keyed and re-signed
manifest set would verify against its own new key.
A MISSING email can be a legitimate deletion: look the ids up in the archive's access log (type Deletion).
Reads only; it changes nothing. Python 3 standard library only.
"""
import argparse, hashlib, json, os, shutil, subprocess, sys

def newest(d):
    names = sorted(f[:-5] for f in os.listdir(d) if f.startswith("manifest-") and f.endswith(".json"))
    if not names:
        sys.exit(f"no manifest-*.json in {d}")
    return names[-1]

def key_fingerprint(d):
    """SHA-256 (hex) of the DER public key in <d>/public-key.pem, or None when it cannot be computed."""
    pub = os.path.join(d, "public-key.pem")
    if not os.path.exists(pub) or shutil.which("openssl") is None:
        return None
    r = subprocess.run(["openssl", "pkey", "-pubin", "-in", pub, "-outform", "DER"], capture_output=True)
    return hashlib.sha256(r.stdout).hexdigest() if r.returncode == 0 and r.stdout else None

def load(d, name):
    """Returns (entries {emailId: sha256}, header dict, problems list)."""
    problems = []
    hpath = os.path.join(d, name + ".json")
    with open(hpath, "rb") as f:
        hbytes = f.read()
    header = json.loads(hbytes)

    sig, pub = os.path.join(d, name + ".sig"), os.path.join(d, "public-key.pem")
    if not (os.path.exists(sig) and os.path.exists(pub)):
        problems.append(f"{name}: signature or public-key.pem missing, signature NOT checked")
    elif shutil.which("openssl") is None:
        problems.append(f"{name}: openssl not found, signature NOT checked")
    else:
        r = subprocess.run(["openssl", "dgst", "-sha256", "-verify", pub, "-signature", sig, hpath],
                           capture_output=True, text=True)
        if "Verified OK" not in r.stdout:
            problems.append(f"{name}: SIGNATURE DOES NOT VERIFY")

    epath = os.path.join(d, os.path.basename(header["entriesFile"]))
    if not os.path.exists(epath):
        problems.append(f"{name}: entries file missing")
        return {}, header, problems
    h = hashlib.sha256()
    entries = {}
    with open(epath, "rb") as f:
        for line in f:
            h.update(line)
            if line.strip():
                e = json.loads(line)
                entries[e["emailId"]] = e["sha256"]
    if h.hexdigest() != header["entriesSha256"]:
        problems.append(f"{name}: entry list does not match the hash in the signed header")
    return entries, header, problems

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("old_dir"); ap.add_argument("new_dir")
    ap.add_argument("--old"); ap.add_argument("--new")
    ap.add_argument("--pubkey-sha256"); ap.add_argument("--allow-key-change", action="store_true")
    a = ap.parse_args()
    try:
        old_name = a.old or newest(a.old_dir)
        new_name = a.new or newest(a.new_dir)
        old, oh, p1 = load(a.old_dir, old_name)
        new, nh, p2 = load(a.new_dir, new_name)
    except (OSError, KeyError, ValueError) as e:
        print(f"error: {e}", file=sys.stderr); return 2

    print(f"old: {old_name}  ({len(old)} originals, generated {oh.get('generatedAt')}, chain seq {oh.get('chainSeq')})")
    print(f"new: {new_name}  ({len(new)} originals, generated {nh.get('generatedAt')}, chain seq {nh.get('chainSeq')})")
    bad = bool(p1 or p2)
    fo, fn = key_fingerprint(a.old_dir), key_fingerprint(a.new_dir)
    if fo is None or fn is None:
        print("PROBLEM: a public key fingerprint could not be computed (public-key.pem or openssl missing)"); bad = True
    else:
        print(f"public key: old {fo[:16]}...  new {fn[:16]}...")
        if a.pubkey_sha256 and (fo != a.pubkey_sha256 or fn != a.pubkey_sha256):
            print("PROBLEM: a public key differs from the pinned --pubkey-sha256"); bad = True
        elif fo != fn and not a.allow_key_change:
            print("PROBLEM: the two manifests were signed with DIFFERENT keys (use --allow-key-change only if you changed the key on purpose)"); bad = True
    for p in p1 + p2:
        print("PROBLEM:", p)

    missing = sorted(i for i in old if i not in new)
    changed = sorted(i for i in old if i in new and old[i] != new[i])
    added = [i for i in new if i not in old]
    for i in missing: print(f"MISSING  email {i}  (old hash {old[i][:12]}...)")
    for i in changed: print(f"CHANGED  email {i}  old {old[i][:12]}...  new {new[i][:12]}...")
    print(f"NEW      {len(added)} email(s) since the old manifest")
    print(f"summary: {len(missing)} missing, {len(changed)} changed, {len(added)} new")
    if missing:
        print("Missing emails may be legitimate deletions: check the access log (type Deletion) for these ids.")
    return 1 if (missing or changed or bad) else 0

if __name__ == "__main__":
    sys.exit(main())
