# Glossary tooling

Keeps the Weblate glossary for `maplestory2-xml` in sync with the terms that actually
recur in the game text, so bulk/machine translation stays consistent on proper nouns.

## Why

Translators (human or MT) working unit-by-unit have no way to know that 레인저 ("Ranger")
is **Archer** in English, or that 크리티아스 is **Kritias**. Weblate surfaces glossary terms
next to any string containing them, which is the cheapest consistency mechanism available.

## Scripts

```bash
# 1. mine candidates from the repo (read-only, no token needed)
python3 Tools/glossary/mine_glossary.py --min-df 20      # -> proposal.json

# 2. preview what would be pushed
python3 Tools/glossary/push_glossary.py --lang en --min-df 100

# 3. push (needs a write token; never pass it on argv)
WEBLATE_API_TOKEN=$(cat ~/.config/weblate-token) \
  python3 Tools/glossary/push_glossary.py --lang en --min-df 100 --apply

# seed the other languages with the English term as a starting point,
# flagged "needs editing" so it shows up for review instead of counting as done
WEBLATE_API_TOKEN=$(cat ~/.config/weblate-token) \
  python3 Tools/glossary/push_glossary.py --lang pt --min-df 100 --seed english --apply
```

`push_glossary.py` is idempotent: it lists what is already in the glossary and skips it,
so re-running is safe.

## How terms are chosen

`mine_glossary.py` aligns ko↔other entries in the name components (`npcname`, `itemname`,
`skillname`, `mapname`, `titlename`, ...) and scores each Korean term by how many prose
strings contain it (quest scripts, NPC dialogue, item descriptions: ~243k strings). High
document frequency means an inconsistent rendering would be visible in many places.

Two escape hatches on top of the frequency score:

- **`CANONICAL` components** (`jobname`, `racename`, `continentname`, `islandname`) are
  included regardless of frequency. They are small closed sets, and class names are
  renamed rather than transliterated in English.
- **`exclude.txt`** is the hand-curated reject list, with a reason per term. Mined output
  is not safe to push blind: 달콤한 is an adjective ("sweet") that the English side renders
  as both "Sweetheart" and "Sweet" depending on the title, so as a glossary term it would
  push a wrong translation everywhere it appears.

## Translating and self-checking the glossary

`translate_glossary.py` machine-translates a language's glossary; `verify_glossary.py`
then audits the result against **official shipped game text**, which is the part that
makes this safe to run without a human reading every term.

```bash
export WEBLATE_API_TOKEN=$(cat ~/.config/weblate-token)

python3 Tools/glossary/translate_glossary.py --lang pt --apply    # translate
python3 Tools/glossary/verify_glossary.py    --lang pt            # report defects
python3 Tools/glossary/verify_glossary.py    --lang pt --fix --apply
```

GMS2 shipped with pt-BR, so the repo already holds human, official renderings of most
proper nouns. The verifier mines them: for each glossary term it collects official
target-language strings whose Korean side contains that term, and flags the term when
the glossary rendering never appears in any of them. That caught, with no human input:
`페리온` "perião" → **Périon**, `룬 블레이더` "Lâmina Rúnica" → **Espadachim Rúnico**,
`소울 바인더` → **Aprisionador de Almas**, `에메랄드` "Emerald" → **Esmeralda**. 23 of 28
flagged pt terms were corrected automatically.

### Three guards, learned by watching it go wrong

The model proposes corrections, but it is never trusted. A correction is written only if:

1. **It literally occurs in the official text** for that term. Without this the model
   invents plausible names.
2. **The evidence is genuinely localized.** ~40% of the pt and es files are English
   passthrough (target byte-identical to English because nobody translated it yet).
   Counting those as ground truth is how a verifier "corrects" Arquero to "Ranger".
   Pairs whose target equals its English counterpart are dropped.
3. **At least `--min-evidence` (default 3) distinct official strings** contain it. One
   stray match is usually a substring artifact, not a convention.
4. **It is not simply the English term.** A "fix" that turns a translation back into
   English means the evidence was untranslated text that slipped the passthrough filter.

A fifth guard sits on the way in rather than the way out: the repo's jp/cn files store
untranslated entries as raw developer keys (`NPCNAME_90000182_NAME:[F]Develop`). Seeding
those as translations put 15 of them into the ja and zh_Hans glossaries before the guard
existed. Both `push_glossary.py` and `translate_glossary.py` now reject them.

## Ground rules per language

Which rules apply is decided by **measured evidence coverage**, not by a belief about
which regions got an official release. Measure it before trusting a language:

```bash
python3 Tools/glossary/verify_glossary.py --lang <code>   # prints coverage, writes nothing
```

Read the `corpus-backed: N | no evidence: M` line. `N` is how many of the 133 glossary
terms actually occur in localized official text for that language.

| lang | localized pairs | terms with evidence | tier |
|---|---|---|---|
| zh_Hans | 269,585 | **133 / 133** | A: authority |
| ja | 260,320 | **133 / 133** | A: authority |
| pt | 36,561 | 72 / 133 | B: authority where evidence exists |
| es | 4,775 | 40 / 133 | C: no authority, glossary decides |
| zh_Hant | 3,860 | 29 / 133 | C: no authority, glossary decides |

**Tier A and B: the shipped game text is the authority.** Run the full
`--fix --apply` loop. The corpus outranks anything a model or a translator invents,
because it is what players actually saw. This is what produced `페리온` → `ペリオン` and
`勇士部落`, `커닝시티` → `カニングシティ` and `黑金市`, and the pt class names
(`룬 블레이더` → Espadachim Rúnico). Tier B differs from A only in reach: pt has no
evidence for 61 terms, and those are simply left alone.

**Tier C: there is no external authority, so the glossary IS the authority.** Do not run
auto-correction. With this little localized text, "corrections" are drawn from fragments
and English passthrough: es proposed turning `레인저` "Arquero" back into "Ranger" and
`소울 바인더` into "vínculos de almas". Instead:

1. **Pick one rendering per term and enforce it everywhere.** The glossary entry is the
   decision. It does not need to match any pre-existing string, it needs to be used
   consistently from here on. Consistency is the whole value; there is no correct answer
   to be discovered.
2. **Auto-fix only what is objectively broken**, regardless of corpus size:
   `--only-korean-leaks` (target still contains Hangul), empty targets, and raw
   developer keys. These are wrong under any editorial policy.
3. **Never accept a "correction" that is just the English term.** Guarded automatically,
   see guard 4 below.
4. If a term later gains real evidence (someone translates more of the game), rerun the
   coverage check: a Tier C language can be promoted.

Re-run `verify_glossary.py --lang es` after any bulk translation to catch Korean leaks
and dev keys, but keep `--only-korean-leaks` on until coverage improves.

## The scheduled workflow

Installed in this box's crontab:

```
30 3 * * *  Tools/glossary/nightly.sh         # machine translation, gated
0  5 * * 0  Tools/glossary/weekly_review.sh   # independent review of a sample
```

### Nightly: gate, then work

`nightly.sh` refuses to translate at all if `verify_glossary.py --gate` finds a broken
glossary entry (Korean left in a target, a raw developer key, an empty target) for the
language it is about to work on. A bad glossary term propagates into hundreds of units,
so stopping costs one night and fixing it downstream costs a cleanup pass.
`gate-allow.txt` holds the deliberate exceptions, currently one.

It then translates a bounded budget (`UNITS_PER_NIGHT`, default 300), easiest components
first, so the reject rate proves itself on short names before the job touches 50k lines of
quest dialogue. Measured reject rate is 0% on name components and **5% on `quest-script`**,
the hardest one, and every reject is a defect the validator caught rather than a guess
written to the server.

**Phase 1 is Korean to English only** (`LANGS="en"`). English is the pivot the other five
languages translate from, and the bake-off showed models translating from Korean produce
noticeably worse output than from English, so filling EN raises the ceiling everywhere.
The tradeoff, accepted deliberately: an error in machine-translated EN propagates to five
languages, which is what the weekly review exists to catch. Flip `LANGS` to `"pt es"` once
the EN backlog drains.

Every override is an environment variable, so a manual run is easy:

```bash
UNITS_PER_NIGHT=20 COMPONENTS="npc-title" bash Tools/glossary/nightly.sh
```

### State lives in Weblate, not on disk

`state:=0` is the queue. There is no progress file to corrupt or resync. The only local
state is a per-run revert log in `~/.local/state/ms2-translate/*.jsonl` recording every
unit id with its before and after, kept 30 days.

That log exists because Weblate provenance is not usable here: **every API write is
attributed to the human token owner**, so machine edits are indistinguishable from manual
ones in the change history. Unit labels are silently ignored on PATCH, so labelling is not
an option either. Creating a dedicated bot user in Weblate and using its token would fix
this properly, and is the recommended next step.

### Consistency is enforced, not hoped for

Two mechanisms, both mechanical:

- **Translation memory.** Before translating, `translate_units.py` looks for an accepted
  translation of the exact same source elsewhere in the component and reuses it. Without
  this, 번지 came out as "Street", "St." and "No." across three runs, because each batch
  translated independently. Identical sources within a run also share one translation.
- **Glossary injection and compliance.** Glossary terms occurring in a batch's sources are
  injected into the prompt as authoritative, and a unit is rejected before writing if a
  glossary term in its source does not appear in the required rendering in its target.

`normalize_duplicates.py` cleans up existing damage: it groups translated units by source
and rewrites minority renderings to the majority one. Dev keys can never win a vote.

### Weekly: the judgement pass

Validators catch mechanical defects. They cannot catch a plausible mistranslation:
`택배기사` ("delivery driver") became "Delivery Knight" because 기사 also means knight, and
nothing mechanical will ever flag that. So `weekly_review.sh` samples ~40 machine-written
units per language and has **two independent reviewers** read them, gpt-5.6-sol via Codex
and opus-5 via `claude -p`. Different model families on purpose: where they disagree is
usually where the real problem is. It also refreshes `snapshot.json` so glossary changes
show up in `git diff`.

## Weblate API notes

Learned the hard way; keep these in mind if you extend the scripts.

- Adding a unit to a language's glossary auto-creates the matching `ko` source unit, so
  `ko` and `en` stay in step. Other languages do **not** get the term: each language has
  its own unit set (the component is a set of bilingual TBX files). Seeding is per-language.
- `explanation` is a **source-string** property. Posting it with a translation unit is
  silently dropped, and PATCHing it onto a translation unit returns
  `"Source strings properties can be set only on source strings"`. It has to be PATCHed
  onto the `ko` unit, which is what `--backfill-notes` does.
- The unit-create response does not include a usable `source_unit` link, so annotation
  needs a separate lookup pass against the `ko` glossary.
- Components can live in categories: `itemname`'s API slug is `item%2Fitemname`, so build
  paths from what the API returns rather than from the component name.
