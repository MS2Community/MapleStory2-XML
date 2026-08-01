#!/usr/bin/env python3
"""Mine glossary candidates for the MapleStory2-XML Weblate project.

Builds a ko -> {en, pt, es, ja, zh} term table from the aligned "name" components,
then scores each term by how often it actually occurs in the Korean prose corpus
(quest scripts, NPC dialogue, item descriptions). Terms that recur across many
strings are the ones where inconsistent translation is most visible in game.

Usage:
    python3 Tools/glossary/mine_glossary.py [--min-df 20] [--out proposal.json]

Output is a JSON proposal consumed by push_glossary.py. Nothing is written to
Weblate by this script.
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import sys
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
JSON_ROOT = os.path.join(REPO, "WeblateConverter", "Json")
API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"

LANGS = ["en", "pt", "es", "jp", "cn", "zh_Hant"]

# Components whose entries are names: these are the glossary candidates.
NAME_SRC = {
    "npcname.json": ["name"],
    "npctitle.json": ["name"],
    "mapname.json": ["name"],
    "skillname.json": ["name"],
    "itemname.json": ["name"],
    "titlename.json": ["name"],
    "petname.json": ["name"],
    "continentname.json": ["name"],
    "islandname.json": ["name"],
    "addressname.json": ["name"],
    "jobname.json": ["name"],
    "jobgroupname.json": ["name"],
    "racename.json": ["name"],
    "guildnpcname.json": ["name"],
    "interactname.json": ["name"],
    "dungeonmissionname.json": ["name"],
}

# Canonical taxonomy: small, closed sets where every entry belongs in the glossary
# regardless of how often it shows up in prose. Korean class names in particular are
# renamed in English (레인저 "Ranger" -> "Archer"), which no translator will guess.
# jobgroupname is deliberately absent: its entries are jobname plus a "직업군"
# (job group) suffix that the English side drops, so they make lopsided terms.
CANONICAL = {
    "jobname.json", "racename.json",
    "continentname.json", "islandname.json",
}

# Prose corpus: where a term appearing under two different translations is noticed.
PROSE = [
    "scriptquest.json",
    "scriptnpc.json",
    "questdescription_final.json",
    "koritemdescription.json",
    "achievedescription.json",
    "achievename.json",
    "stringguide.json",
    "stringtrigger.json",
    "stringadditionaldescription.json",
    "stringfieldenterance.json",
    "systemmailcontentkr.json",
]

PLACEHOLDER = re.compile(r"\$[A-Za-z]+|\{[^}]*\}|<[^>]*>|\\n|\\t")
HANGUL = re.compile(r"[가-힣]")
# English side that is really a raw key, a placeholder, or a screaming token
BAD_EN = re.compile(r"[_\[\]{}<>$]|^[A-Z]{3,}")

# Common nouns whose "term" status is an artifact of an NPC being called e.g. "Cat".
# Translating these consistently is not a glossary problem.
GENERIC_EN = {
    "person", "test", "button", "bag", "like", "greet", "special", "machine",
    "cat", "dragon", "soldier", "knight", "demon", "fairy", "beggar", "blue",
    "gray", "shadow", "shadowy", "shiny", "thorn", "mark", "pirate", "empress",
    "anvil", "balloon", "arena", "revive", "eve", "porte", "per",
}


def load(lang: str, fname: str) -> dict:
    path = os.path.join(JSON_ROOT, lang, fname)
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def fetch_existing_glossary(lang: str) -> dict:
    """ko -> target for terms already in the Weblate glossary (read-only, no token)."""
    url = f"{API}/translations/{PROJECT}/glossary/{lang}/units/?page_size=1000"
    req = urllib.request.Request(url, headers={"User-Agent": "ms2-glossary-miner"})
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.load(resp)
    except Exception as exc:  # offline / instance down: proceed without dedup
        print(f"warning: could not read existing glossary ({exc})", file=sys.stderr)
        return {}
    return {u["source"][0]: u["target"][0] for u in data.get("results", [])}


def build_candidates() -> tuple[dict, dict]:
    cand: dict[str, dict] = {}
    origin: dict[str, str] = {}
    for fname, fields in NAME_SRC.items():
        ko = load("ko", fname)
        if not ko:
            continue
        others = {lang: load(lang, fname) for lang in LANGS}
        for key, entry in ko.items():
            if not isinstance(entry, dict):
                continue
            for field in fields:
                kov = entry.get(field)
                if not isinstance(kov, str):
                    continue
                kov = kov.strip()
                if not (2 <= len(kov) <= 20) or PLACEHOLDER.search(kov):
                    continue
                if not HANGUL.search(kov):
                    continue
                slot = cand.setdefault(kov, {})
                origin.setdefault(kov, fname)
                for lang, other in others.items():
                    if lang in slot:
                        continue
                    oe = other.get(key)
                    if isinstance(oe, dict):
                        ov = oe.get(field)
                        if isinstance(ov, str) and ov.strip():
                            slot[lang] = ov.strip()
    return cand, origin


def score(cand: dict) -> tuple[collections.Counter, int]:
    """Document frequency of each candidate across the Korean prose corpus."""
    by_start = collections.defaultdict(list)
    for term in cand:
        by_start[term[:2]].append(term)

    df = collections.Counter()
    scanned = 0
    for fname in PROSE:
        for entry in load("ko", fname).values():
            if not isinstance(entry, dict):
                continue
            for value in entry.values():
                if not isinstance(value, str) or len(value) < 4:
                    continue
                scanned += 1
                hits = set()
                for i in range(len(value) - 1):
                    for term in by_start.get(value[i:i + 2], ()):
                        if value.startswith(term, i):
                            hits.add(term)
                df.update(hits)
    return df, scanned


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--min-df", type=int, default=20,
                    help="minimum prose occurrences to keep a term (default 20)")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "proposal.json"))
    args = ap.parse_args()

    cand, origin = build_candidates()
    print(f"candidate terms: {len(cand)}", file=sys.stderr)

    df, scanned = score(cand)
    print(f"prose strings scanned: {scanned}", file=sys.stderr)

    existing = fetch_existing_glossary("en")
    print(f"already in glossary: {len(existing)}", file=sys.stderr)

    rows = []
    for term, tr in cand.items():
        canonical = origin[term] in CANONICAL
        n = df[term]
        if not canonical and n < args.min_df:
            continue
        en = tr.get("en", "")
        if not en or term in existing:
            continue
        # 2-char Korean terms match inside unrelated words; require 3+ or a space.
        # Canonical taxonomy is exempt: 시프 (Thief) is a real term at 2 chars.
        if not canonical and len(term) < 3 and " " not in term:
            continue
        if BAD_EN.search(en) or en == term:
            continue
        if not canonical and en.lower() in GENERIC_EN:
            continue
        rows.append({"df": n, "ko": term, "component": origin[term],
                     "canonical": canonical, **tr})
    rows.sort(key=lambda r: (not r["canonical"], -r["df"]))

    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump({"scanned_prose_strings": scanned, "min_df": args.min_df,
                   "terms": rows}, fh, ensure_ascii=False, indent=1)

    print(f"{'df':>5}  {'ko':<20} {'en':<34} {'pt':<22} {'es'}")
    for t in rows[:40]:
        print(f"{t['df']:>5}  {t['ko']:<20} {t['en']:<34} "
              f"{t.get('pt',''):<22} {t.get('es','')}")
    print(f"\n{len(rows)} terms -> {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
