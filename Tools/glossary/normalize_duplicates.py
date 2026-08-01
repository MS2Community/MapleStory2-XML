#!/usr/bin/env python3
"""Make identical source strings share one translation within a component/language.

The same Korean string rendered three different ways is the most visible kind of
inconsistency in game text, and it is entirely mechanical to detect: group translated
units by source, and any group with more than one distinct target is a defect.

The winner is the most frequent rendering, ties broken by longest (the fuller form is
usually the considered one, e.g. "Ciudad Kerning" over "Kerning"). Review with a dry run
before applying: majority is a heuristic, not an oracle.

    python3 Tools/glossary/normalize_duplicates.py --lang en --component address-name
    python3 Tools/glossary/normalize_duplicates.py --lang en --component address-name --apply
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"
STATE_TRANSLATED = 20
# A raw developer key is never a valid rendering, so it can neither win a vote nor
# survive one: it is always a loser to be overwritten by the real translation.
TOKENISH = re.compile(r":\[F\]|^[A-Z][A-Z0-9_]{6,}")


def request(path, token, method="GET", payload=None):
    url = (path if path.startswith("http") else f"{API}{path}").replace("http://", "https://")
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", "ms2-normalize")
    req.add_header("Accept", "application/json")
    req.add_header("Authorization", f"Token {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=120) as resp:
        body = resp.read()
        return json.loads(body) if body else {}


def component_slug(name, token):
    for c in request(f"/projects/{PROJECT}/components/?page_size=100", token).get("results", []):
        if c["slug"] == name:
            return c["url"].rstrip("/").split("/")[-1]
    return name


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", required=True)
    ap.add_argument("--component", required=True)
    ap.add_argument("--max-units", type=int, default=20000)
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    token = os.environ.get("WEBLATE_API_TOKEN", "")
    if not token:
        print("WEBLATE_API_TOKEN is not set", file=sys.stderr)
        return 2

    slug = component_slug(args.component, token)
    units, url = [], (f"{API}/translations/{PROJECT}/{slug}/{args.lang}/units/"
                      f"?q=state:%3E%3D20&page_size=200")
    while url and len(units) < args.max_units:
        d = request(url, token)
        units += d.get("results", [])
        url = d.get("next")
    print(f"{args.component}/{args.lang}: {len(units)} translated units")

    groups = collections.defaultdict(list)
    for u in units:
        src, tgt = u["source"][0].strip(), u["target"][0].strip()
        if src and tgt:
            groups[src].append(u)

    conflicts = {src: g for src, g in groups.items()
                 if len({u["target"][0].strip() for u in g}) > 1}
    print(f"source strings with more than one rendering: {len(conflicts)}")

    plan = []
    for src, g in sorted(conflicts.items(), key=lambda kv: -len(kv[1])):
        counts = collections.Counter(u["target"][0].strip() for u in g)
        real = {t: n for t, n in counts.items() if not TOKENISH.search(t)}
        if not real:
            continue                      # every rendering is a dev key: nothing to pick
        winner = max(real.items(), key=lambda kv: (kv[1], len(kv[0])))[0]
        losers = [u for u in g if u["target"][0].strip() != winner]
        plan += [(u, winner) for u in losers]
        if len(plan) <= 60:
            print(f"  {src[:34]!r}: {dict(counts)} -> {winner!r} ({len(losers)} to change)")

    print(f"\nunits to rewrite: {len(plan)}")
    if not args.apply or not plan:
        if plan:
            print("dry run: nothing written. Re-run with --apply.")
        return 0

    written = 0
    for u, winner in plan:
        try:
            request(f"/units/{u['id']}/", token, method="PATCH",
                    payload={"target": [winner], "state": STATE_TRANSLATED})
            written += 1
        except urllib.error.HTTPError as exc:
            print(f"  FAIL {u['id']}: {exc.code}", file=sys.stderr)
        time.sleep(0.12)
    print(f"written: {written}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
