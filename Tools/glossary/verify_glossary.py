#!/usr/bin/env python3
"""Find defects in glossary translations by checking them against official game text.

GMS2 shipped with pt-BR (and other locales), so the repo already contains human,
official renderings of most proper nouns. That corpus is ground truth: if the glossary
says 페리온 is "perião" but every official pt string renders it "Périon", the glossary is
wrong, and that is decidable without asking anyone.

Two passes:
  1. corpus check (deterministic): for each glossary term, collect official target-language
     strings whose Korean counterpart contains the term. If the proposed glossary target
     never appears in any of them, flag it.
  2. correction (model): hand each flagged term its Korean source, English, current target
     and the official corpus evidence, and ask for the corpus-attested form.

    export WEBLATE_API_TOKEN=...
    python3 Tools/glossary/verify_glossary.py --lang pt            # report only
    python3 Tools/glossary/verify_glossary.py --lang pt --fix      # propose fixes
    python3 Tools/glossary/verify_glossary.py --lang pt --fix --apply
"""
from __future__ import annotations

import argparse
import collections
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
JSON_ROOT = os.path.join(REPO, "WeblateConverter", "Json")
API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"
COMPONENT = "glossary"
STATE_TRANSLATED = 20

# Weblate language code -> repo directory name
REPO_DIR = {"ja": "jp", "zh_Hans": "cn"}
HANGUL = re.compile(r"[가-힣]")
TOKENISH = re.compile(r":\[F\]|^[A-Z][A-Z0-9_]{6,}")
LANG_NAME = {"pt": "Brazilian Portuguese", "es": "Latin American Spanish",
             "zh_Hant": "Traditional Chinese", "ja": "Japanese", "zh_Hans": "Simplified Chinese"}


def request(path, token, method="GET", payload=None):
    url = (path if path.startswith("http") else f"{API}{path}").replace("http://", "https://")
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", "ms2-glossary-verify")
    req.add_header("Accept", "application/json")
    req.add_header("Authorization", f"Token {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=90) as resp:
        body = resp.read()
        return json.loads(body) if body else {}


def glossary_units(lang, token):
    out, url = [], f"{API}/translations/{PROJECT}/{COMPONENT}/{lang}/units/?page_size=500"
    while url:
        d = request(url, token)
        out += d.get("results", [])
        url = d.get("next")
    return out


def aligned_corpus(lang):
    """(ko_string, target_string) pairs of GENUINELY LOCALIZED official text.

    ~40% of the pt and es files are English passthrough: the target string is byte
    identical to the English one because nobody has translated it yet. Treating those
    as ground truth is how a verifier "corrects" Arquero to "Ranger". Any pair whose
    target equals its English counterpart is therefore dropped, not counted.
    """
    ko_dir = os.path.join(JSON_ROOT, "ko")
    en_dir = os.path.join(JSON_ROOT, "en")
    tgt_dir = os.path.join(JSON_ROOT, REPO_DIR.get(lang, lang))
    if not os.path.isdir(tgt_dir):
        return [], 0
    pairs, dropped = [], 0
    for fname in os.listdir(tgt_dir):
        ko_path = os.path.join(ko_dir, fname)
        tgt_path = os.path.join(tgt_dir, fname)
        en_path = os.path.join(en_dir, fname)
        if not os.path.exists(ko_path):
            continue
        try:
            ko = json.load(open(ko_path, encoding="utf-8"))
            tg = json.load(open(tgt_path, encoding="utf-8"))
            en = json.load(open(en_path, encoding="utf-8")) if os.path.exists(en_path) else {}
        except Exception:
            continue
        for key, ko_entry in ko.items():
            tg_entry, en_entry = tg.get(key), en.get(key)
            if not isinstance(ko_entry, dict) or not isinstance(tg_entry, dict):
                continue
            for field, ko_val in ko_entry.items():
                tg_val = tg_entry.get(field)
                if not (isinstance(ko_val, str) and isinstance(tg_val, str)
                        and ko_val.strip() and tg_val.strip()):
                    continue
                en_val = en_entry.get(field) if isinstance(en_entry, dict) else None
                if isinstance(en_val, str) and en_val.strip() == tg_val.strip():
                    dropped += 1
                    continue
                pairs.append((ko_val, tg_val))
    return pairs, dropped


def evidence_index(pairs, terms):
    """term -> list of official target strings whose Korean side contains the term."""
    by_start = collections.defaultdict(list)
    for t in terms:
        by_start[t[:2]].append(t)
    found = collections.defaultdict(list)
    for ko_val, tg_val in pairs:
        for i in range(len(ko_val) - 1):
            for term in by_start.get(ko_val[i:i + 2], ()):
                if ko_val.startswith(term, i) and len(found[term]) < 40:
                    found[term].append(tg_val)
    return found


def ask_model(model, prompt, workdir="/tmp/mt"):
    os.makedirs(workdir, exist_ok=True)
    proc = subprocess.run(["opencode", "run", "--pure", "-m", model, prompt],
                          capture_output=True, text=True, cwd=workdir,
                          stdin=subprocess.DEVNULL, timeout=900)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr[-300:])
    return proc.stdout


def parse_array(text):
    text = re.sub(r"```(?:json)?", "", text)
    return json.loads(text[text.find("["):text.rfind("]") + 1])


def fix_prompt(lang, batch):
    return "\n".join([
        f"You are auditing glossary terms for MapleStory 2 in {LANG_NAME.get(lang, lang)}.",
        "",
        "MapleStory 2 shipped officially in this language, so the OFFICIAL EXAMPLES below",
        "are real strings from the shipped game. They are authoritative: the glossary term",
        "must match how the official text renders that name, including accents and casing.",
        "",
        "For each entry decide whether 'current' matches the official examples.",
        "  - If it does, return it unchanged.",
        "  - If the examples show a different official rendering, return that rendering.",
        "  - If the examples do not actually contain this name, return 'current' unchanged.",
        "Return only the term itself, never a sentence, never an explanation.",
        "",
        'Output a single JSON array, nothing else. Each element:',
        '{"id": <id>, "t": "<corrected or unchanged term>", "changed": true|false}',
        "",
        "ENTRIES:",
        json.dumps(batch, ensure_ascii=False, indent=1),
    ])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", required=True)
    ap.add_argument("--model", default="opencode/deepseek-v4-flash-free")
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--min-evidence", type=int, default=3,
                    help="a correction must appear in at least this many distinct "
                         "official strings before it may be written")
    ap.add_argument("--only-korean-leaks", action="store_true",
                    help="only touch entries whose target still contains Korean. These "
                         "are broken regardless of how thin the corpus is, so this is the "
                         "safe mode for languages with little official localized text.")
    ap.add_argument("--gate", action="store_true",
                    help="exit non-zero if the glossary has objectively broken entries "
                         "(Korean left in a target, or a raw developer key). Used by the "
                         "nightly job to refuse to translate against a broken glossary.")
    ap.add_argument("--fix", action="store_true", help="ask the model for corrections")
    ap.add_argument("--apply", action="store_true", help="write accepted corrections")
    args = ap.parse_args()

    token = os.environ.get("WEBLATE_API_TOKEN", "")
    if not token:
        print("WEBLATE_API_TOKEN is not set", file=sys.stderr)
        return 2

    units = [u for u in glossary_units(args.lang, token) if u["target"][0].strip()]
    terms = {u["source"][0]: u for u in units}
    print(f"{args.lang}: {len(terms)} translated glossary terms")

    if args.gate:
        allow = set()
        allow_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "gate-allow.txt")
        if os.path.exists(allow_path):
            for line in open(allow_path, encoding="utf-8"):
                entry = line.split("#", 1)[0].strip()
                if not entry:
                    continue
                parts = entry.split(None, 1)
                if len(parts) == 2 and parts[0] == args.lang:
                    allow.add(parts[1].strip())
        broken = []
        for u in units:
            tgt = u["target"][0].strip()
            if u["source"][0].strip() in allow:
                continue
            if args.lang != "ko" and HANGUL.search(tgt):
                broken.append((u["source"][0], tgt, "korean in target"))
            elif TOKENISH.search(tgt):
                broken.append((u["source"][0], tgt, "raw developer key"))
        empty = [u for u in glossary_units(args.lang, token) if not u["target"][0].strip()]
        for u in empty:
            broken.append((u["source"][0], "", "empty target"))
        for ko, tgt, why in broken:
            print(f"  BROKEN {ko} -> {tgt[:40]!r}: {why}")
        print(f"gate: {len(broken)} broken entries")
        return 1 if broken else 0

    pairs, dropped = aligned_corpus(args.lang)
    print(f"official corpus: {len(pairs)} localized ko/{args.lang} pairs "
          f"({dropped} dropped as English passthrough)")
    if len(pairs) < 500:
        print("too little genuinely localized text for this language: "
              "corpus verification would be unreliable, refusing to run")
        return 0

    ev = evidence_index(pairs, list(terms))
    flagged, unsupported = [], 0
    for ko, unit in terms.items():
        samples = ev.get(ko, [])
        if not samples:
            unsupported += 1
            continue
        target = unit["target"][0].strip()
        if any(target.lower() in s.lower() for s in samples):
            continue
        if args.only_korean_leaks and not HANGUL.search(target):
            continue
        flagged.append((unit, ko, target, samples))

    print(f"\ncorpus-backed: {len(terms) - unsupported} | no evidence: {unsupported} "
          f"| FLAGGED: {len(flagged)}\n")
    for unit, ko, target, samples in flagged[:25]:
        print(f"  {ko} -> {target!r}")
        for s in samples[:2]:
            print(f"      official: {s[:80]!r}")

    if not args.fix or not flagged:
        return 0

    en_terms = {u["source"][0]: u["target"][0].strip()
                for u in glossary_units("en", token)}
    batches = [flagged[i:i + args.batch] for i in range(0, len(flagged), args.batch)]
    fixes, unverified, english_regressions = [], [], []
    for n, group in enumerate(batches, 1):
        payload = [{"id": u["id"], "ko": ko, "en": "", "current": tgt,
                    "official_examples": samples[:5]}
                   for u, ko, tgt, samples in group]
        print(f"audit batch {n}/{len(batches)} ...", flush=True)
        try:
            res = {r["id"]: r for r in parse_array(ask_model(args.model, fix_prompt(args.lang, payload)))}
        except Exception as exc:
            print(f"  batch failed: {exc}", file=sys.stderr)
            continue
        for u, ko, tgt, samples in group:
            r = res.get(u["id"])
            if not r:
                continue
            new = str(r.get("t", "")).strip()
            if not new or new == tgt or len(new) > 80 or "\n" in new:
                continue
            # The model is not trusted: a correction is only accepted if it literally
            # occurs in the official shipped text for this term. Without this gate it
            # invents plausible-looking names (라네모네 -> "Raneomone").
            # A "correction" that is just the English term is a regression, not a fix:
            # it means the evidence was untranslated text that slipped the passthrough
            # filter. This is how es tried to turn Arquero back into "Ranger".
            if new.lower() == en_terms.get(ko, "").lower() and tgt.lower() != new.lower():
                english_regressions.append((ko, tgt, new))
                continue
            hits = len({s for s in samples if new.lower() in s.lower()})
            if hits >= args.min_evidence:
                fixes.append((u, ko, tgt, new, hits))
            else:
                unverified.append((ko, tgt, new, hits))

    print(f"\ncorpus-attested corrections: {len(fixes)}")
    for _, ko, old, new, hits in fixes:
        print(f"  {ko}: {old!r} -> {new!r}  ({hits} official strings)")
    print(f"\nrejected, under {args.min_evidence} official strings: {len(unverified)}")
    for ko, old, new, hits in unverified:
        print(f"  {ko}: {old!r} -/-> {new!r}  ({hits})")
    if english_regressions:
        print(f"\nrejected, would replace a translation with the English term: "
              f"{len(english_regressions)}")
        for ko, old, new in english_regressions:
            print(f"  {ko}: {old!r} -/-> {new!r}")

    if not args.apply:
        print("\ndry run: nothing written. Re-run with --apply.")
        return 0

    written = 0
    for u, ko, old, new, _ in fixes:
        try:
            request(f"/units/{u['id']}/", token, method="PATCH",
                    payload={"target": [new], "state": STATE_TRANSLATED})
            written += 1
        except urllib.error.HTTPError as exc:
            print(f"  FAIL {ko}: {exc.code}", file=sys.stderr)
        time.sleep(0.15)
    print(f"written: {written}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
