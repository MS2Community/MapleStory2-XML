#!/usr/bin/env bash
# Nightly machine-translation run for the MapleStory2-XML Weblate project.
#
# Order matters and is deliberate:
#   1. GATE   - refuse to translate at all if the glossary is broken for the target
#               language. A bad glossary term propagates into hundreds of units, so it
#               is far cheaper to stop here than to fix it downstream.
#   2. WORK   - translate a bounded batch, easiest components first. Every unit is
#               validated (placeholders, markup, newlines, traps, glossary compliance)
#               before it is written; failures are left untranslated, never guessed.
#
# Korean -> English runs first and alone: English is the pivot every other language
# translates from, so filling it in raises the ceiling for all five. Once the EN
# backlog is done, flip LANGS to the downstream set.
#
# State lives in Weblate (state:=0 is the queue), not in a local file. Each run writes
# a revert log to ~/.local/state/ms2-translate/.

set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TOKEN_FILE=~/.config/weblate-token
STATE=~/.local/state/ms2-translate
LOG="$STATE/nightly-$(date +%F).log"

# Phase 1: Korean -> English only. Swap to "pt es" (and later ja zh_Hans zh_Hant)
# once the EN backlog is drained.
LANGS="${LANGS:-en}"

# Easiest first: short names before markup-heavy prose. Lets the reject rate prove
# itself on cheap content before the job touches 50k lines of quest dialogue.
COMPONENTS="${COMPONENTS:-address-name npc-title category-name npc-function-name npc-name-plural itemname questdescription item-descriptions quest-script}"

UNITS_PER_NIGHT="${UNITS_PER_NIGHT:-300}"
BATCH="${BATCH:-10}"

mkdir -p "$STATE"
exec >> "$LOG" 2>&1
echo "=============================================================="
echo "run started $(date -Is)"

if [ ! -r "$TOKEN_FILE" ]; then
  echo "FATAL: $TOKEN_FILE missing or unreadable"
  exit 1
fi
export WEBLATE_API_TOKEN
WEBLATE_API_TOKEN=$(cat "$TOKEN_FILE")
cd "$REPO" || exit 1

for LANG_CODE in $LANGS; do
  echo "--- gate: glossary/$LANG_CODE ---"
  if ! python3 Tools/glossary/verify_glossary.py --lang "$LANG_CODE" --gate; then
    echo "GATE FAILED for $LANG_CODE: glossary has broken entries, skipping this language"
    continue
  fi

  remaining=$UNITS_PER_NIGHT
  for COMP in $COMPONENTS; do
    [ "$remaining" -le 0 ] && break
    echo "--- translate: $COMP/$LANG_CODE (budget $remaining) ---"
    python3 Tools/glossary/translate_units.py \
      --lang "$LANG_CODE" --component "$COMP" \
      --limit "$remaining" --batch "$BATCH" --apply
    # translate_units stops when a component has no untranslated units left, so the
    # budget only actually falls when work happened; re-reading it from Weblate would
    # cost another round trip for no benefit.
    written=$(grep -c '"how"' "$STATE/$COMP-$LANG_CODE-"*".jsonl" 2>/dev/null | tail -1)
    remaining=$((remaining - ${written:-0}))
  done
done

echo "run finished $(date -Is)"
# Keep a month of logs and revert records, drop the rest.
find "$STATE" -name '*.log' -mtime +30 -delete
find "$STATE" -name '*.jsonl' -mtime +30 -delete
