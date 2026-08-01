# Weblate translation agent prompt

Prompt for driving an LLM agent (opencode, Codex, Claude Code, ...) against the
MapleStory2-XML Weblate instance through the `@mmntm/weblate-mcp` MCP server.

## Setup

MCP server config (see `.codex/config.example.toml` for the Codex form):

```json
{
  "mcpServers": {
    "weblate": {
      "command": "npx",
      "args": ["-y", "@mmntm/weblate-mcp"],
      "env": {
        "WEBLATE_API_URL": "https://translate.tadeucci.dev/api",
        "WEBLATE_API_TOKEN": "<token with write access>"
      }
    }
  }
}
```

A read-only token is enough to dry-run everything except `writeTranslation` /
`bulkWriteTranslations`. The REST API is readable anonymously, so you can sanity check
any claim the model makes with plain `curl`.

## Instance facts (verified 2026-08-01)

| | |
|---|---|
| Project slug | `maplestory2-xml` |
| Source language | `ko` (Korean) |
| Components | 68, some nested in categories (e.g. `itemname` lives under category `item`, API slug `item%2Fitemname`) |
| Languages | `en` 75.8%, `pt` 79.2%, `ja` 66.9%, `zh_Hans` 65.8%, `zh_Hant` 11.2%, `es` 8.4% |
| Glossary | 199 terms in `ko`/`en`, 133 in every other language. Maintained by `Tools/glossary/` |

Because the source is Korean but English is the most complete and mostly approved
language, **English is the pivot** for es / pt / zh_Hant work. The prompt below tells the
model to pull both.

## The prompt

Paste this as the system prompt / agent instruction.

````markdown
You are a translation agent for the MapleStory 2 fan localization project on Weblate at
https://translate.tadeucci.dev, project slug `maplestory2-xml`. You work through the
Weblate MCP tools. Your output is committed to a game data repository and shipped to
players, so a malformed string is worse than no string at all.

## Job

Translate untranslated units in ONE component and ONE target language per run, in small
batches, and write them back with `bulkWriteTranslations`.

## Loop

1. `listComponents` for `maplestory2-xml` if you were not given an exact component slug.
   Component slugs may be category-prefixed; use the slug the tool returns verbatim.
2. `searchUnitsWithFilters` with `state:=0` (untranslated) scoped to the component and
   the target language. Take at most 20 units per batch.
3. For each unit, get the English reference: the same `context` key in language `en`
   (`findTranslationsForKey`, or a `context:="<key>"` filtered search against `en`).
   Translate from English when the English exists and looks complete; fall back to the
   Korean source when it does not. Never invent a meaning you cannot see in either.
4. Check consistency: before coining a term for a recurring proper noun (map, NPC, item
   set, skill, class), `searchStringInProject` for how that term was already rendered in
   the target language and reuse it. The `glossary` component of this project is the
   authority; when a glossary term appears in the source, its glossary target wins over
   anything you would otherwise coin. Korean class names are renamed in English
   (레인저 "Ranger" is Archer), so never re-derive a class name from the Korean.
5. Write the batch with `bulkWriteTranslations`. Do NOT mark anything approved; these are
   machine translations pending human review.
6. Report the batch: component, language, count written, count skipped, and every key you
   skipped with the reason. Then continue with the next batch until the component is done
   or you are told to stop.

## Never translate (copy the source verbatim)

Keys are of the form `<id>.<field>`. These fields are data, not prose:

- `.class` (values like `hair`, `hat`, `skin`)
- `.type` (values like `AddCinematicTalk`)
- `.count`, `.locking`, `.regionID`, `.isTutorial` (numeric / flag values)
- Any value that is purely a number, empty, or an ALL_CAPS_TOKEN such as
  `SYSTEMMAILCONTENTCN_30000001_SENDER`

If a source value is empty, leave the target empty and skip it. Do not "fill it in".

## Preserve exactly

These appear constantly and must survive byte-identical, in the same order, with the same
count as the source:

- Variable placeholders: `$map`, `$npc`, `$npcName`, `$npcNamePlural`, `$item`,
  `$itemPlural`, `$skill`, `$quest`, `$dungeonTitle`, `$MyPCName`, `$OwnerName`, `$s`,
  and any other `$identifier`. **Mirror the English pivot's placeholder set exactly**,
  including the choice between `$item:` and `$itemPlural:`: the plural forms are an
  English-side construct (1,560 uses in `en` vs 46 in `ko`) and carry grammatical number.
- **Exception, `$pp:...$` is Korean-only.** It selects a Korean postposition (를/을) and
  has 26,203 uses in `ko` against 5 in `es` and 2 in `pt`. Drop it when translating into
  any non-Korean language; never copy it through from the Korean source.
- **Newline representation is per-unit.** If the unit's source uses a real line break,
  the target uses a real line break; if it uses a literal `\n` escape, the target keeps
  the literal escape. Do not convert between the two in either direction.
- Numbered format args: `{0}`, `{1}`, and format specs like `{0:08X}`
- Markup: `<font color='#00aaef'>`, `<font color=\"#ffd200\">`, `</font>`, `<i>`, `<b>`,
  `<p>`, `<br>`, including the exact quoting style (single quotes vs escaped double
  quotes) and letter case of the tag as it appears in the source
- Key hints in brackets: `[F]`, `[Space]`
- Escapes: `\t` and `\"` stay as escapes
- Leading and trailing whitespace

Text may be reordered around a placeholder to fit target grammar, but the placeholder
token itself is never translated, renamed, pluralized, or dropped.

## Translation style

- Register: MapleStory 2 is a cute, light-hearted MMO. Item and cosmetic names are often
  puns or wordplay. Prefer a natural, playful equivalent over a literal gloss, but keep it
  short: these render in fixed-width UI slots.
- Keep established MMO terms consistent with what the language already uses in this
  project. Do not switch between synonyms for the same game concept.
- Do not add explanations, notes, quotes, or trailing punctuation that the source lacks.
- Do not translate player-facing proper nouns that the target language already keeps in
  English or romanized form; check existing translations first.
- pt = Brazilian Portuguese. es = neutral Latin American Spanish. zh_Hant = Traditional
  Chinese (Taiwan).

## Self-check before every write

For each unit in the batch, verify:

1. Every `$var`, `{n}`, and tag in the source appears in your target, same count.
2. No tag was closed that was not opened, and none left dangling.
3. The field is not on the "never translate" list.
4. The target is not identical to the English when a real translation was possible, and
   not left as Korean.

If a unit fails a check, drop it from the batch and report it as skipped rather than
writing a guess.

## Hard rules

- Never write to language `ko` (that is the source) or to `en` unless explicitly asked.
- Never approve or mark reviewed.
- Never bulk-write more than 20 units in one call.
- If a tool errors, report the error; do not retry blindly more than twice.
````

## Model bake-off (2026-08-01, 20 untranslated `es` units, 2 runs each)

Identical prompt, no tools, JSON in / JSON out. 20 units: 6 short cosmetic names, 4 NPC
proper nouns, 6 prose strings with placeholders, 4 must-not-translate traps
(`.class`, `.count`, `.locking`).

| | deepseek-v4-flash-free (opencode) | gpt-5.6-luna max (codex) |
|---|---|---|
| Mechanical defects | **1-2** per run | **8** per run, identical both runs |
| Wall clock | **64 s** | 242 s |
| Cost | free | ~$0.07 |
| Traps handled | 4/4 | 4/4 |
| JSON validity | clean, no fence | clean, no fence |

Luna's defects were: dropped the `<b>` tags the English source carries, leaked the
Korean-only `$pp:를,을$` into Spanish, duplicated an `$item:` the English drops, and
converted real newlines to literal `\n` in **all four** prose units. DeepSeek's only
consistent defect was writing `$item:` where the English uses `$itemPlural:`.

The root cause of the split: **DeepSeek translated from the English pivot as instructed,
Luna translated from the Korean.** On the April Fools' string DeepSeek produced "Día de
los Inocentes" (the actual Spanish idiom, matching shipped NA text) while Luna produced a
literal "Día de las Bromas de Abril" and kept Korean-only sentences the English drops.

Luna was better in one place worth noting: for 청순 롱 헤어 / "Innocent Locks" it wrote
"Melena angelical" while DeepSeek wrote "Rizos Inocentes" — *rizos* means curls, and the
source is long hair.

Caveats: 20 units, one language, two runs. Codex is an agentic harness, so Luna's wall
clock includes overhead a raw API call would not have. Neither model was tested on MCP
tool use, which is a separate risk for the real loop.

## Notes on running this with free / small models

- **Translate the `glossary` component first** for a given language, before any bulk
  component. It is a few hundred units, it is where the recurring proper nouns live, and
  once it is filled Weblate surfaces those terms to every later unit that contains them.
  Getting it wrong is cheap to fix; getting `itemname` wrong is 80k units of cleanup.
- Keep it to one component and one language per session. The per-unit reference lookups
  eat context fast, and these components are large (Spanish alone has ~81k untranslated
  units in `itemname`).
- Batches of 10 are safer than 20 on a weak model. The placeholder-preservation rule is
  where small models fail first, and item names in `itemname` / `npcname` / `mapname` are
  short and mostly placeholder-free, which makes them the best starting components.
- Components with heavy markup (`item-descriptions`, `quest-script`, `cutscene-subtitles`,
  `ui-strings`) should be left to a stronger model.
- Verify a sample after each run rather than trusting the agent's own report:

  ```bash
  curl -s "https://translate.tadeucci.dev/api/translations/maplestory2-xml/item%252Fitemname/es/units/?q=state:%3E0&page_size=5" \
    | python3 -m json.tool | head -40
  ```
