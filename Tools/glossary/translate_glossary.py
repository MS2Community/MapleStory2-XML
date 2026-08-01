#!/usr/bin/env python3
"""Machine-translate the Weblate glossary for one language, in small validated batches.

Why the model does not write directly: glossary units have an empty context/key, and the
weblate MCP server addresses units by key. `writeTranslation` fails with
"Translation unit not found for key ..." for every glossary term. So the model only
produces text; this script validates it and writes by unit id over the REST API.

The API token is never placed in a model prompt. Free model tiers may retain prompt data.

    export WEBLATE_API_TOKEN=...
    python3 Tools/glossary/translate_glossary.py --lang pt              # dry run
    python3 Tools/glossary/translate_glossary.py --lang pt --apply
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request

API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"
COMPONENT = "glossary"
STATE_TRANSLATED = 20

LANG_NAME = {
    "pt": "Brazilian Portuguese",
    "es": "neutral Latin American Spanish",
    "zh_Hant": "Traditional Chinese (Taiwan)",
    "ja": "Japanese",
    "zh_Hans": "Simplified Chinese",
}

HANGUL = re.compile(r"[가-힣]")
SUSPECT = re.compile(r"^(sure|here|okay|note|i )", re.I)
TOKENISH = re.compile(r":\[F\]|^[A-Z][A-Z0-9_]{6,}")


def request(path: str, token: str, method: str = "GET", payload: dict | None = None):
    url = (path if path.startswith("http") else f"{API}{path}").replace("http://", "https://")
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", "ms2-glossary-mt")
    req.add_header("Accept", "application/json")
    req.add_header("Authorization", f"Token {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=90) as resp:
        body = resp.read()
        return json.loads(body) if body else {}


def units(lang: str, token: str, query: str = "") -> list[dict]:
    out, url = [], (f"{API}/translations/{PROJECT}/{COMPONENT}/{lang}/units/"
                    f"?page_size=500{query}")
    while url:
        data = request(url, token)
        out += data.get("results", [])
        url = data.get("next")
    return out


def ask_model(model: str, prompt: str, workdir: str) -> str:
    proc = subprocess.run(
        ["opencode", "run", "--pure", "-m", model, prompt],
        capture_output=True, text=True, cwd=workdir, stdin=subprocess.DEVNULL, timeout=900)
    if proc.returncode != 0:
        raise RuntimeError(f"opencode failed: {proc.stderr[-300:]}")
    return proc.stdout


def parse_json_array(text: str) -> list[dict]:
    text = re.sub(r"```(?:json)?", "", text)
    start, end = text.find("["), text.rfind("]")
    if start < 0 or end < 0:
        raise ValueError("no JSON array in model output")
    return json.loads(text[start:end + 1])


def build_prompt(lang: str, batch: list[dict]) -> str:
    target = LANG_NAME.get(lang, lang)
    lines = [
        f"You are translating glossary terms for the MMO MapleStory 2 into {target}.",
        "",
        "These are game terms: NPC names, place names, character classes, monster",
        "families and item names. They are the canonical renderings that every other",
        "string in the game will be made consistent with, so precision matters more",
        "than flair.",
        "",
        "Rules:",
        "- Personal and place names are normally transliterated or kept as the English",
        "  form. Do not invent a descriptive phrase where the English uses a name.",
        "- Descriptive terms (character classes, monster families, rarity tiers) are",
        "  translated properly into the target language.",
        "- Keep them short. These render in tooltips and fixed-width UI.",
        "- No explanations, no notes, no quotes, no trailing punctuation.",
        "- Never leave Korean characters in the output.",
        "",
        "Output a single JSON array and nothing else. No markdown fence, no commentary.",
        'Each element: {"id": <the id integer>, "t": "<your translation>"}',
        "Exactly one element per input term, same order.",
        "",
        "TERMS:",
    ]
    payload = [{k: v for k, v in t.items() if v} for t in batch]
    return "\n".join(lines) + "\n" + json.dumps(payload, ensure_ascii=False, indent=1) + "\n"


def validate(term: dict, text: str) -> str | None:
    """Return a rejection reason, or None if the translation is acceptable."""
    if not isinstance(text, str) or not text.strip():
        return "empty"
    text = text.strip()
    if HANGUL.search(text):
        return "korean left in output"
    if len(text) > 80:
        return f"too long ({len(text)} chars)"
    if "\n" in text or "```" in text:
        return "multiline or fenced"
    if SUSPECT.match(text):
        return "looks like commentary"
    if TOKENISH.search(text):
        return "raw developer key, not a translation"
    if text == term.get("ko"):
        return "identical to korean source"
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", required=True)
    ap.add_argument("--model", default="opencode/deepseek-v4-flash-free")
    ap.add_argument("--batch", type=int, default=10)
    ap.add_argument("--limit", type=int, default=30, help="max terms this run")
    ap.add_argument("--workdir", default="/tmp/mt")
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    token = os.environ.get("WEBLATE_API_TOKEN", "")
    if not token:
        print("WEBLATE_API_TOKEN is not set", file=sys.stderr)
        return 2
    os.makedirs(args.workdir, exist_ok=True)

    # reference material: English always, Simplified Chinese as an extra hint for zh_Hant
    en_by_src = {u["source"][0]: u["target"][0] for u in units("en", token)}
    hans_by_src = ({u["source"][0]: u["target"][0] for u in units("zh_Hans", token)}
                   if args.lang == "zh_Hant" else {})

    todo = [u for u in units(args.lang, token)
            if u["state"] < STATE_TRANSLATED or u["fuzzy"]]
    print(f"{args.lang}: {len(todo)} terms need translation")
    todo = todo[:args.limit]

    batches = [todo[i:i + args.batch] for i in range(0, len(todo), args.batch)]
    accepted, rejected = [], []

    for n, group in enumerate(batches, 1):
        payload = []
        for u in group:
            src = u["source"][0]
            item = {"id": u["id"], "ko": src, "en": en_by_src.get(src, "")}
            if hans_by_src.get(src):
                item["zh_Hans"] = hans_by_src[src]
            payload.append(item)
        prompt = build_prompt(args.lang, payload)
        print(f"batch {n}/{len(batches)} ({len(group)} terms) ...", flush=True)
        try:
            raw = ask_model(args.model, prompt, args.workdir)
            results = {r["id"]: r.get("t", "") for r in parse_json_array(raw)
                       if isinstance(r, dict) and "id" in r}
        except Exception as exc:
            print(f"  batch failed: {exc}", file=sys.stderr)
            rejected += [(u, "batch error") for u in group]
            continue
        for item, u in zip(payload, group):
            text = results.get(item["id"], "")
            reason = validate(item, text)
            if reason:
                rejected.append((u, reason))
            else:
                accepted.append((u, text.strip(), item))

    print(f"\naccepted {len(accepted)}, rejected {len(rejected)}")
    for u, text, item in accepted[:40]:
        print(f"  {item['ko']:<18} en={item.get('en','')[:22]:<24} -> {text}")
    for u, reason in rejected[:10]:
        print(f"  REJECT {u['source'][0]}: {reason}")

    if not args.apply:
        print("\ndry run: nothing written. Re-run with --apply.")
        return 0

    written = 0
    for u, text, _ in accepted:
        try:
            request(f"/units/{u['id']}/", token, method="PATCH",
                    payload={"target": [text], "state": STATE_TRANSLATED})
            written += 1
        except urllib.error.HTTPError as exc:
            print(f"  FAIL {u['source'][0]}: {exc.code} "
                  f"{exc.read().decode(errors='replace')[:160]}", file=sys.stderr)
        time.sleep(0.15)
    print(f"written: {written}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
