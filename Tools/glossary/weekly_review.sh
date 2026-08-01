#!/usr/bin/env bash
# Weekly independent review of machine-translated output.
#
# The nightly validators catch mechanical defects (placeholders, markup, traps). They
# cannot catch tone, naturalness, or a systematic misreading of the source. That needs
# judgement, so this is the one scheduled job that spends real model budget.
#
# Two reviewers, deliberately from different families:
#   gpt-5.6-sol (Codex) - independent of the model that produced the translations
#   opus-5      (Claude) - stronger taste for whether game copy reads naturally
# Where they disagree is usually where the real problem is.
#
# Reviews a SAMPLE, not everything. Reading 80k units would cost a fortune and tell you
# no more than reading 40 will.

set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
STATE=~/.local/state/ms2-translate
OUT="$STATE/review-$(date +%F)"
SAMPLE=40
LANGS="en pt es"

mkdir -p "$OUT"
export WEBLATE_API_TOKEN
WEBLATE_API_TOKEN=$(cat ~/.config/weblate-token)
cd "$REPO" || exit 1

# 1. Snapshot the glossary so its changes are diffable week to week.
python3 Tools/glossary/snapshot_glossary.py --out "$REPO/Tools/glossary/snapshot.json"

# 2. Build the sample from what was actually written this week.
python3 Tools/glossary/build_review_sample.py --langs "$LANGS" --n "$SAMPLE" \
  --out "$OUT/sample.json" || exit 1

PROMPT="You are reviewing machine-translated MapleStory 2 game text.

Read $OUT/sample.json. Each entry has the Korean source, the English reference where one
exists, and the machine translation that was written to the live translation server.

Judge only what a mechanical validator cannot: does it read naturally to a player, is the
register right for a light-hearted MMO, is the meaning actually faithful, are proper nouns
handled consistently. Placeholder and markup correctness is already machine-checked, so
ignore it unless you spot something the checker would obviously miss.

Report ONLY entries you would reject or change, worst first, each with the id, what is
wrong, and a concrete better rendering. If a language looks systematically off (wrong
register throughout, translating from Korean when English was available), say so
explicitly at the top. Be brief and specific. If everything is acceptable, say so."

echo "=== gpt-5.6-sol review ===" > "$OUT/codex.md"
codex exec --skip-git-repo-check -C "$REPO" -s read-only \
  -m gpt-5.6-sol -c 'model_reasoning_effort="high"' \
  --output-last-message "$OUT/codex.md" "$PROMPT" < /dev/null > "$OUT/codex.log" 2>&1

echo "=== opus-5 review ===" > "$OUT/opus.md"
claude -p --model opus --permission-mode acceptEdits "$PROMPT" \
  < /dev/null >> "$OUT/opus.md" 2>"$OUT/opus.log"

echo "reviews written to $OUT"
echo "--- codex ---"; head -40 "$OUT/codex.md"
echo "--- opus ---";  head -40 "$OUT/opus.md"
