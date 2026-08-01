#!/usr/bin/env python3
"""Push mined glossary terms into the MapleStory2-XML Weblate glossary.

Dry run by default: nothing is written unless --apply is passed.

    export WEBLATE_API_TOKEN=...            # never pass the token on argv
    python3 Tools/glossary/push_glossary.py --lang en --min-df 50          # preview
    python3 Tools/glossary/push_glossary.py --lang en --min-df 50 --apply  # write

The glossary component stores one unit set per language (TBX files), so a term
added to `en` does not appear under `pt`. Seeding the other languages means
posting the same Korean source term into each of their glossaries:

    python3 Tools/glossary/push_glossary.py --lang pt --seed english --min-df 50

  --seed empty    create the term untranslated (target ""), for a human/MT pass
  --seed english  create it with the English term as a starting point, flagged
                  "needs editing" so it shows up for review rather than as done
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"
COMPONENT = "glossary"
STATE_NEEDS_EDITING = 10
STATE_TRANSLATED = 20

# proposal.json uses the repo's language dir names; Weblate uses its own codes
LANG_ALIAS = {"jp": "ja", "cn": "zh_Hans"}

# The repo's jp/cn files store untranslated entries as raw developer keys
# ("NPCNAME_90000182_NAME:[F]Develop"). Seeding those as translations puts garbage
# in the glossary, which is exactly what happened before this guard existed.
TOKENISH = re.compile(r":\[F\]|^[A-Z][A-Z0-9_]{6,}")


def request(path: str, token: str, method: str = "GET", payload: dict | None = None):
    url = path if path.startswith("http") else f"{API}{path}"
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", "ms2-glossary-push")
    req.add_header("Accept", "application/json")
    if token:
        req.add_header("Authorization", f"Token {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=60) as resp:
        body = resp.read()
        return json.loads(body) if body else {}


def load_exclusions() -> set[str]:
    """Hand-curated terms that must never be pushed (see exclude.txt for reasons)."""
    path = os.path.join(os.path.dirname(__file__), "exclude.txt")
    if not os.path.exists(path):
        return set()
    out = set()
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            term = line.split("#", 1)[0].strip()
            if term:
                out.add(term)
    return out


def unit_map(lang: str, token: str) -> dict[str, dict]:
    """Korean source term -> unit, for the given language's glossary."""
    units: dict[str, dict] = {}
    url = f"{API}/translations/{PROJECT}/{COMPONENT}/{lang}/units/?page_size=500"
    while url:
        data = request(url, token)
        for unit in data.get("results", []):
            src = unit.get("source") or [""]
            units[src[0]] = unit
        url = data.get("next")
    return units


def annotate(terms: list[dict], token: str, apply: bool) -> int:
    """Write provenance onto the ko source units.

    Provenance is a source-string property, so it cannot be set on a translation
    unit, and the unit-create response does not carry a source_unit link. Hence a
    separate lookup pass against the ko glossary.
    """
    source_units = unit_map("ko", token)
    done = 0
    for t in terms:
        if t.get("canonical"):
            continue                      # canonical taxonomy needs no justification
        unit = source_units.get(t["ko"])
        if not unit or unit.get("explanation"):
            continue
        note = (f"auto-mined from repo corpus: {t['df']} occurrences "
                f"({t['component'].replace('.json', '')})")
        if not apply:
            print(f"  would annotate {t['ko']}: {note}")
            done += 1
            continue
        try:
            request(f"/units/{unit['id']}/", token, method="PATCH",
                    payload={"explanation": note})
            done += 1
        except urllib.error.HTTPError as exc:
            print(f"  note: could not annotate {t['ko']}: {exc.code}", file=sys.stderr)
        time.sleep(0.15)
    return done


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", required=True, help="Weblate language code (en, pt, es, ja, zh_Hans, zh_Hant)")
    ap.add_argument("--proposal", default=os.path.join(os.path.dirname(__file__), "proposal.json"))
    ap.add_argument("--min-df", type=int, default=50)
    ap.add_argument("--limit", type=int, default=100, help="max terms per run")
    ap.add_argument("--seed", choices=["own", "empty", "english"], default="own",
                    help="what to use as target when the language has no term of its own")
    ap.add_argument("--apply", action="store_true", help="actually write to Weblate")
    ap.add_argument("--backfill-notes", action="store_true",
                    help="only write provenance notes onto terms already in the glossary")
    args = ap.parse_args()

    token = os.environ.get("WEBLATE_API_TOKEN", "")
    if args.apply and not token:
        print("WEBLATE_API_TOKEN is not set", file=sys.stderr)
        return 2

    with open(args.proposal, encoding="utf-8") as fh:
        terms = json.load(fh)["terms"]

    repo_lang = {v: k for k, v in LANG_ALIAS.items()}.get(args.lang, args.lang)

    try:
        present = set(unit_map(args.lang, token))
    except urllib.error.HTTPError as exc:
        print(f"could not list existing terms: {exc}", file=sys.stderr)
        return 2
    print(f"{args.lang}: {len(present)} terms already present", file=sys.stderr)

    excluded = load_exclusions()
    print(f"{len(excluded)} terms excluded by exclude.txt", file=sys.stderr)

    if args.backfill_notes:
        eligible = [t for t in terms if t["ko"] in present and t["ko"] not in excluded]
        n = annotate(eligible, token, args.apply)
        print(f"annotated {n} source units" if args.apply
              else f"\ndry run: would annotate {n} source units.")
        return 0

    queued = []
    for t in terms:
        if t["ko"] in present or t["ko"] in excluded:
            continue
        # canonical taxonomy (job/race/continent names) ignores the frequency floor
        if not t.get("canonical") and t["df"] < args.min_df:
            continue
        target = t.get(repo_lang, "")
        if target and TOKENISH.search(target):
            target = ""          # untranslated in the repo, not a real rendering
        state = STATE_TRANSLATED
        if not target:
            if args.seed == "own":
                continue
            if args.seed == "english":
                target = t.get("en", "")
                state = STATE_NEEDS_EDITING
            else:
                target, state = "", 0
        if args.lang != "en" and not target and args.seed != "empty":
            continue
        queued.append((t, target, state))
        if len(queued) >= args.limit:
            break

    print(f"{len(queued)} terms queued (min_df={args.min_df}, seed={args.seed})")
    for t, target, state in queued[:15]:
        print(f"  {t['df']:>5}  {t['ko']:<18} -> {target!r} (state {state})")
    if len(queued) > 15:
        print(f"  ... and {len(queued) - 15} more")

    if not args.apply:
        print("\ndry run: nothing written. Re-run with --apply to push.")
        return 0

    written = failed = 0
    for t, target, state in queued:
        payload = {"source": [t["ko"]], "target": [target], "state": state}
        try:
            request(f"/translations/{PROJECT}/{COMPONENT}/{args.lang}/units/",
                    token, method="POST", payload=payload)
            written += 1
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode(errors="replace")[:300]
            print(f"  FAIL {t['ko']}: {exc.code} {detail}", file=sys.stderr)
            failed += 1
            if failed >= 3:
                print("aborting after 3 failures", file=sys.stderr)
                break
        time.sleep(0.2)

    print(f"written: {written}, failed: {failed}")

    if written and args.lang == "en":
        noted = annotate([t for t, _, _ in queued], token, True)
        print(f"annotated {noted} source units with provenance")

    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
