#!/usr/bin/env python3
"""Dump the whole Weblate glossary to a sorted JSON file so changes are diffable in git.

For the Tier C languages (es, zh_Hant) there is no official corpus to check the glossary
against, so the glossary itself is the authority. A committed snapshot is the only way to
review a change to it: `git diff` shows exactly which term moved and to what.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess

API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"
LANGS = ["ko", "en", "pt", "es", "ja", "zh_Hans", "zh_Hant"]


def api(url, token):
    url = url.replace("http://", "https://")
    out = subprocess.run(["curl", "-sL", "-m", "90", "-H", f"Authorization: Token {token}", url],
                         capture_output=True, text=True)
    return json.loads(out.stdout)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    args = ap.parse_args()
    token = os.environ.get("WEBLATE_API_TOKEN", "")

    snapshot = {}
    for lang in LANGS:
        url = f"{API}/translations/{PROJECT}/glossary/{lang}/units/?page_size=500"
        terms = {}
        while url:
            data = api(url, token)
            for u in data.get("results", []):
                terms[u["source"][0]] = u["target"][0]
            url = data.get("next")
        snapshot[lang] = dict(sorted(terms.items()))

    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(snapshot, fh, ensure_ascii=False, indent=1, sort_keys=True)
        fh.write("\n")
    print(f"snapshot: {', '.join(f'{k}={len(v)}' for k, v in snapshot.items())} -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
