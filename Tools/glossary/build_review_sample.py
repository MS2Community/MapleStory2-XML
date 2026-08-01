#!/usr/bin/env python3
"""Sample recently machine-written units so an independent model can review them.

Reads the revert logs the nightly job leaves in ~/.local/state/ms2-translate/, which are
the authoritative record of what this tooling actually wrote. Weblate change history
cannot be used for this: every write is attributed to the human token owner, so machine
edits are indistinguishable from manual ones there.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import random
import subprocess
import time
import urllib.parse

API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"
STATE_DIR = os.path.expanduser("~/.local/state/ms2-translate")


def api(url, token):
    url = url.replace("http://", "https://")
    out = subprocess.run(["curl", "-sL", "-m", "60", "-H", f"Authorization: Token {token}", url],
                         capture_output=True, text=True)
    try:
        return json.loads(out.stdout)
    except json.JSONDecodeError:
        return {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--langs", default="en pt es")
    ap.add_argument("--n", type=int, default=40)
    ap.add_argument("--days", type=int, default=8)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    token = os.environ.get("WEBLATE_API_TOKEN", "")
    cutoff = time.time() - args.days * 86400
    langs = args.langs.split()

    written = []
    for path in glob.glob(os.path.join(STATE_DIR, "*.jsonl")):
        if os.path.getmtime(path) < cutoff:
            continue
        base = os.path.basename(path)
        lang = base.rsplit("-", 3)[0].split("-")[-1] if "-" in base else ""
        for line in open(path, encoding="utf-8"):
            try:
                rec = json.loads(line)
            except json.JSONDecodeError:
                continue
            if rec.get("how") == "trap":
                continue          # copied verbatim, nothing to judge
            rec["_file"] = base
            written.append(rec)

    if not written:
        print("no machine-written units in the window; nothing to review")
        json.dump([], open(args.out, "w", encoding="utf-8"))
        return 0

    random.seed(int(time.time()) // 86400)   # stable within a day, varies week to week
    sample = random.sample(written, min(args.n * len(langs), len(written)))

    out = []
    for rec in sample:
        entry = {"id": rec["id"], "context": rec.get("context", ""),
                 "file": rec["_file"], "machine_translation": rec.get("after", "")}
        unit = api(f"{API}/units/{rec['id']}/", token)
        if unit:
            entry["ko"] = (unit.get("source") or [""])[0]
            entry["current_target"] = (unit.get("target") or [""])[0]
            entry["language"] = unit.get("language_code", "")
        out.append(entry)

    json.dump(out, open(args.out, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(f"{len(out)} units sampled from {len(written)} machine-written -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
