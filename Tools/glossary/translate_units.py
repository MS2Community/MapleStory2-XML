#!/usr/bin/env python3
"""Machine-translate untranslated units of one component/language, with hard validation.

Every rule enforced here was learned from a real defect, either in the model bake-off
(docs/weblate-translation-prompt.md) or from the glossary work. Nothing is written that
fails validation; rejects are logged with a reason so the prompt can be improved.

    export WEBLATE_API_TOKEN=...
    python3 Tools/glossary/translate_units.py --lang en --component itemname --limit 50
    python3 Tools/glossary/translate_units.py --lang en --component itemname --apply
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
import urllib.parse
import urllib.request

API = "https://translate.tadeucci.dev/api"
PROJECT = "maplestory2-xml"
STATE_TRANSLATED = 20
STATE_DIR = os.path.expanduser("~/.local/state/ms2-translate")

VAR = re.compile(r"\$[a-zA-Z]+:[^$]*\$|\$[a-zA-Z]+")
TAG = re.compile(r"<[^>]+>")
HANGUL = re.compile(r"[가-힣]")
TOKENISH = re.compile(r":\[F\]|^[A-Z][A-Z0-9_]{6,}")
NUMERICISH = re.compile(r"^[\d\s|.,%-]*$")
TRAP_FIELDS = {"class", "count", "locking", "regionID", "isTutorial", "type"}

LANG_NAME = {
    "en": "English", "pt": "Brazilian Portuguese", "es": "neutral Latin American Spanish",
    "zh_Hant": "Traditional Chinese (Taiwan)", "ja": "Japanese", "zh_Hans": "Simplified Chinese",
}


def request(path, token, method="GET", payload=None):
    url = (path if path.startswith("http") else f"{API}{path}").replace("http://", "https://")
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("User-Agent", "ms2-translate")
    req.add_header("Accept", "application/json")
    req.add_header("Authorization", f"Token {token}")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=120) as resp:
        body = resp.read()
        return json.loads(body) if body else {}


def component_slug(name, token):
    """Accept a human slug and return the API slug (categories are path-encoded)."""
    data = request(f"/projects/{PROJECT}/components/?page_size=100", token)
    for c in data.get("results", []):
        if c["slug"] == name:
            return c["url"].rstrip("/").split("/")[-1]
    return name


def glossary_map(lang, token):
    out, url = {}, f"{API}/translations/{PROJECT}/glossary/{lang}/units/?page_size=500"
    while url:
        d = request(url, token)
        for u in d.get("results", []):
            src, tgt = u["source"][0].strip(), u["target"][0].strip()
            if src and tgt and len(src) >= 3 and not HANGUL.search(tgt if lang != "ko" else ""):
                out[src] = tgt
        url = d.get("next")
    return out


def untranslated(slug, lang, token, limit):
    out, url = [], (f"{API}/translations/{PROJECT}/{slug}/{lang}/units/"
                    f"?q=state:%3D0&page_size=200")
    while url and len(out) < limit:
        d = request(url, token)
        out += [u for u in d.get("results", []) if u["source"][0].strip()]
        url = d.get("next")
    return out[:limit]


def pivot_for(slug, contexts, token):
    """context -> translated English string (the reference translation)."""
    found = {}
    for ctx in contexts:
        q = urllib.parse.quote(f'context:="{ctx}"')
        try:
            d = request(f"/translations/{PROJECT}/{slug}/en/units/?q={q}&page_size=2", token)
        except urllib.error.HTTPError:
            continue
        for u in d.get("results", []):
            if u["target"][0].strip() and u["state"] >= STATE_TRANSLATED:
                found[u["context"]] = u["target"][0]
    return found


def existing_translation(slug, lang, source, token):
    """An accepted translation of this exact source elsewhere in the component.

    Identical source strings must not get different renderings just because they landed
    in different batches: 번지 came out as "Street", "St." and "No." across three runs
    before this existed.
    """
    q = urllib.parse.quote(f'source:="{source}" AND state:>=20')
    try:
        d = request(f"/translations/{PROJECT}/{slug}/{lang}/units/?q={q}&page_size=20", token)
    except urllib.error.HTTPError:
        return None
    seen = {}
    for u in d.get("results", []):
        t = u["target"][0].strip()
        if t:
            seen[t] = seen.get(t, 0) + 1
    if not seen:
        return None
    return max(seen.items(), key=lambda kv: kv[1])[0]


def classify(unit):
    """'copy' = data field, mirror the source. 'skip' = not real content. None = translate."""
    field = unit["context"].rsplit(".", 1)[-1] if "." in unit["context"] else ""
    src = unit["source"][0].strip()
    if TOKENISH.search(src):
        # The Korean source is itself a developer key: an unreleased or dev-only entry.
        # Writing it through would mark garbage as translated, so leave it untranslated.
        return "skip"
    if field in TRAP_FIELDS or NUMERICISH.fullmatch(src):
        return "copy"
    return None


def relevant_glossary(sources, gloss):
    hits = {}
    for src in sources:
        for ko, tgt in gloss.items():
            if ko in src:
                hits[ko] = tgt
    return hits


def build_prompt(lang, batch, gloss_hits):
    target = LANG_NAME.get(lang, lang)
    lines = [
        f"Translate MapleStory 2 game text into {target}.",
        "",
        "The source is Korean. Where an 'en' field is present it is the shipped English",
        "translation: translate from it, because it reflects the localized wording players",
        "actually saw. Fall back to the Korean only when 'en' is absent.",
        "",
        "HARD RULES:",
        "- Preserve every $placeholder exactly ($item:123$, $map:1$, $npc:2$, $quest:3$).",
        "  Mirror the 'en' field's placeholder set exactly, including $itemPlural vs $item.",
        "  Text may be reordered around a placeholder; the placeholder is never translated.",
        "- $pp:...$ selects a Korean grammar particle. DROP it. Never emit it.",
        "- Preserve markup tags exactly as they appear (<font ...>, </font>, <i>, <b>),",
        "  including quoting style and case. Do not add tags that are not in the source.",
        "- Newlines: if the source uses a real line break, use a real line break. If it uses",
        "  a literal \\n escape, keep the literal escape. Never convert between the two.",
        "- Keep the | separator count identical to the source.",
        "- Never leave Korean characters in the output.",
        "- No explanations, no notes, no quotes that the source does not have.",
    ]
    if gloss_hits:
        lines += ["", "GLOSSARY (authoritative, use these exact renderings):"]
        lines += [f"  {ko} = {tgt}" for ko, tgt in sorted(gloss_hits.items())[:60]]
    lines += [
        "",
        "Output a single JSON array and nothing else. No markdown fence, no commentary.",
        'Each element: {"id": <id integer>, "t": "<translation>"}',
        "Exactly one element per input unit, same order.",
        "",
        "UNITS:",
    ]
    return "\n".join(lines) + "\n" + json.dumps(batch, ensure_ascii=False, indent=1) + "\n"


def validate(unit, pivot, text, lang, gloss):
    """Return a rejection reason, or None."""
    src = unit["source"][0]
    if not isinstance(text, str) or not text.strip():
        return "empty"
    if len(text) > max(400, len(src) * 4):
        return "implausibly long"
    if HANGUL.search(text):
        return "korean left in output"
    if TOKENISH.search(text.strip()):
        return "raw developer key"
    if "```" in text:
        return "markdown fence"

    ref = pivot or src
    want = sorted(v for v in VAR.findall(ref) if not v.startswith("$pp"))
    have = sorted(v for v in VAR.findall(text) if not v.startswith("$pp"))
    if want != have:
        return f"placeholder mismatch: want {want}, got {have}"
    if any(v.startswith("$pp") for v in VAR.findall(text)):
        return "korean particle $pp leaked into target"

    src_tags = set(TAG.findall(src)) | set(TAG.findall(pivot or ""))
    out_tags = set(TAG.findall(text))
    if out_tags - src_tags:
        return f"invented markup {sorted(out_tags - src_tags)}"
    if set(TAG.findall(ref)) - out_tags:
        return f"dropped markup {sorted(set(TAG.findall(ref)) - out_tags)}"

    if src.count("|") != text.count("|"):
        return f"pipe count {src.count('|')} -> {text.count('|')}"
    if "\\n" in src and "\\n" not in text:
        return "literal \\n escape lost"
    if "\n" in src and "\\n" in text and "\n" not in text:
        return "real newline converted to literal escape"

    # glossary compliance: a term the glossary decided must appear as decided
    for ko, tgt in gloss.items():
        if ko in src and len(tgt) >= 3 and tgt.lower() not in text.lower():
            return f"glossary term {ko!r} should render as {tgt!r}"
    return None


def ask_model(model, prompt, workdir):
    proc = subprocess.run(["opencode", "run", "--pure", "-m", model, prompt],
                          capture_output=True, text=True, cwd=workdir,
                          stdin=subprocess.DEVNULL, timeout=1200)
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr[-300:])
    return proc.stdout


def parse_array(text):
    text = re.sub(r"```(?:json)?", "", text)
    s, e = text.find("["), text.rfind("]")
    if s < 0 or e < 0:
        raise ValueError("no JSON array in output")
    return json.loads(text[s:e + 1])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", required=True)
    ap.add_argument("--component", required=True)
    ap.add_argument("--model", default="opencode/deepseek-v4-flash-free")
    ap.add_argument("--batch", type=int, default=10)
    ap.add_argument("--limit", type=int, default=50)
    ap.add_argument("--workdir", default="/tmp/mt")
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    token = os.environ.get("WEBLATE_API_TOKEN", "")
    if not token:
        print("WEBLATE_API_TOKEN is not set", file=sys.stderr)
        return 2
    os.makedirs(args.workdir, exist_ok=True)
    os.makedirs(STATE_DIR, exist_ok=True)

    slug = component_slug(args.component, token)
    gloss = glossary_map(args.lang, token)
    units = untranslated(slug, args.lang, token, args.limit)
    print(f"{args.component}/{args.lang}: {len(units)} untranslated units fetched "
          f"| glossary: {len(gloss)} terms")
    if not units:
        return 0

    kinds = {u["id"]: classify(u) for u in units}
    traps = [u for u in units if kinds[u["id"]] == "copy"]
    skipped = [u for u in units if kinds[u["id"]] == "skip"]
    work = [u for u in units if kinds[u["id"]] is None]
    pivot = pivot_for(slug, [u["context"] for u in work], token) if args.lang != "en" else {}
    print(f"data fields copied: {len(traps)} | skipped (dev-key source): {len(skipped)} "
          f"| to translate: {len(work)} | with en pivot: {len(pivot)}")

    accepted = [(u, u["source"][0], "trap") for u in traps]
    rejected = []

    # One representative per distinct source string; the rest inherit its translation.
    by_source = {}
    for u in work:
        by_source.setdefault(u["source"][0], []).append(u)
    reps, reused = [], 0
    for src, group in by_source.items():
        memory = existing_translation(slug, args.lang, src, token)
        if memory:
            for u in group:
                accepted.append((u, memory, "memory"))
            reused += len(group)
        else:
            reps.append(group[0])
    if reused or len(reps) < len(work):
        print(f"reused existing translations: {reused} | distinct sources to translate: "
              f"{len(reps)} (from {len(work)} units)")

    batches = [reps[i:i + args.batch] for i in range(0, len(reps), args.batch)]
    for n, group in enumerate(batches, 1):
        payload = []
        for u in group:
            item = {"id": u["id"], "ko": u["source"][0]}
            if pivot.get(u["context"]):
                item["en"] = pivot[u["context"]]
            payload.append(item)
        hits = relevant_glossary([u["source"][0] for u in group], gloss)
        print(f"batch {n}/{len(batches)} ({len(group)} units, {len(hits)} glossary hits)",
              flush=True)
        try:
            res = {r["id"]: r.get("t", "") for r in
                   parse_array(ask_model(args.model, build_prompt(args.lang, payload, hits),
                                         args.workdir))
                   if isinstance(r, dict) and "id" in r}
        except Exception as exc:
            print(f"  batch failed: {exc}", file=sys.stderr)
            rejected += [(u, "batch error") for u in group]
            continue
        for u in group:
            text = res.get(u["id"], "")
            reason = validate(u, pivot.get(u["context"]), text, args.lang, hits)
            if reason:
                rejected.append((u, reason))
            else:
                for twin in by_source.get(u["source"][0], [u]):
                    accepted.append((twin, text, "model" if twin is u else "twin"))

    total = len(accepted) + len(rejected)
    rate = 100 * len(rejected) / max(total, 1)
    print(f"\naccepted {len(accepted)} | rejected {len(rejected)} ({rate:.0f}%)")
    reasons = {}
    for u, r in rejected:
        key = r.split(":")[0]
        reasons[key] = reasons.get(key, 0) + 1
    for k, v in sorted(reasons.items(), key=lambda x: -x[1]):
        print(f"  {v:>4}  {k}")
    for u, text, how in accepted[:8]:
        print(f"  [{how}] {u['source'][0][:38]!r} -> {text[:44]!r}")

    if not args.apply:
        print("\ndry run: nothing written. Re-run with --apply.")
        return 0

    stamp = time.strftime("%Y%m%d-%H%M%S")
    logpath = os.path.join(STATE_DIR, f"{args.component}-{args.lang}-{stamp}.jsonl")
    written = 0
    with open(logpath, "w", encoding="utf-8") as log:
        for u, text, how in accepted:
            try:
                request(f"/units/{u['id']}/", token, method="PATCH",
                        payload={"target": [text], "state": STATE_TRANSLATED})
                log.write(json.dumps({"id": u["id"], "context": u["context"],
                                      "before": u["target"][0], "after": text,
                                      "how": how}, ensure_ascii=False) + "\n")
                written += 1
            except urllib.error.HTTPError as exc:
                print(f"  FAIL {u['context']}: {exc.code}", file=sys.stderr)
            time.sleep(0.12)
    print(f"written: {written} | revert log: {logpath}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
