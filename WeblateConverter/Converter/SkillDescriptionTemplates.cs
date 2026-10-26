using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace WeblateConverter.Converter;

// Skill descriptions ship one row per skill level (korSkillDescription*.xml, keyed id -> level by
// StringTableManager_LoadLeveledDescriptions, GMS2 0x140c9aaf0) with every number baked in, so most
// of a skill's rows differ only in their numbers. Weblate gets one template per skill/feature/locale
// instead, key "<id>|*-<feature>-<locale>", with the numbers that change between levels as {0}, {1}...
// The per-level numbers live in WeblateConverter/LevelValues/<table>.json, outside the Weblate file mask,
// and JSON -> XML expands every template back into the rows the client reads.
//
// A group stays per-level (the plain "<id>|<level>-..." rows) when its wording changes between levels,
// or when any existing translation cannot be folded into the template without changing its text.
public static class SkillDescriptionTemplates {
    public const string ALL_LEVELS = "*";

    public sealed record Table(string JsonName, string XmlPrefix);

    public static readonly Table[] Tables = [
        new("stringskilldescription.json", "korskilldescription"),
        new("stringadditionaldescription.json", "koradditionaldescription"),
    ];

    public static Table? Find(string jsonName) {
        return Tables.FirstOrDefault(table => table.JsonName.Equals(jsonName, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex Number = new(@"[0-9]+(?:\.[0-9]+)?", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"\{([0-9]+)\}", RegexOptions.Compiled);

    // Untranslated rows exported from the client carry their string-table id instead of text, e.g.
    // KORSKILLDESCRIPTION_10100261_1_UIDESCRIPTION:[F]JobChange_02. In a translation they are treated as
    // untranslated; the Korean source keeps them, since they are its only row for that id.
    private static bool IsClientPlaceholder(JObject row) {
        return row.Properties().Any(prop => {
            string text = prop.Value.ToString();
            return text.StartsWith("KORSKILLDESCRIPTION_", StringComparison.Ordinal)
                || text.StartsWith("KORADDITIONALDESCRIPTION_", StringComparison.Ordinal);
        });
    }

    public static bool IsTemplateKey(string key) {
        return TryParseKey(key, out _, out string level, out _, out _) && level == ALL_LEVELS;
    }

    private static bool TryParseKey(string key, out string id, out string level, out string feature, out string locale) {
        id = level = feature = locale = "";
        string[] parts = key.Split('-');
        if (parts.Length != 3) {
            return false;
        }
        string[] identity = parts[0].Split('|');
        if (identity.Length > 2) {
            return false;
        }
        id = identity[0];
        level = identity.Length == 2 ? identity[1] : "";
        feature = parts[1];
        locale = parts[2];
        return true;
    }

    private static string GroupKey(string id, string feature, string locale) => $"{id}|{ALL_LEVELS}-{feature}-{locale}";

    private sealed class Group {
        public required string Id;
        public required string Feature;
        public required string Locale;
        public readonly List<(string Level, string Key, JObject Row)> Rows = new();
    }

    // Groups and their levels come out in a fixed order (id, then base before feature rows, locale rows
    // last, then by name; levels ascending) rather than XML order, so XML -> JSON -> XML -> JSON is
    // stable even though the XML writer orders same-level feature rows by patch version.
    private static List<Group> GroupRows(JObject rows) {
        var groups = new List<Group>();
        var byIdentity = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (JProperty prop in rows.Properties()) {
            if (!TryParseKey(prop.Name, out string id, out string level, out string feature, out string locale)) {
                throw new InvalidDataException($"Malformed key '{prop.Name}'");
            }
            string identity = $"{id}-{feature}-{locale}";
            if (!byIdentity.TryGetValue(identity, out Group? group)) {
                group = new Group { Id = id, Feature = feature, Locale = locale };
                byIdentity[identity] = group;
                groups.Add(group);
            }
            group.Rows.Add((level, prop.Name, prop.Value as JObject ?? new JObject()));
        }
        foreach (Group group in groups) {
            var ordered = group.Rows.OrderBy(row => NumericOrMax(row.Level)).ToList();
            group.Rows.Clear();
            group.Rows.AddRange(ordered);
        }
        return groups
            .OrderBy(group => NumericOrMax(group.Id))
            .ThenBy(group => group.Locale == "" ? 0 : 1)
            .ThenBy(group => group.Feature == "" ? 0 : 1)
            .ThenBy(group => group.Feature, StringComparer.Ordinal)
            .ThenBy(group => group.Locale, StringComparer.Ordinal)
            .ToList();
    }

    private static long NumericOrMax(string value) {
        if (value == "") {
            return -1;
        }
        return long.TryParse(value, out long number) ? number : long.MaxValue;
    }

    private sealed class Template {
        public readonly JObject Text = new();
        // level -> attribute -> numbers for {0}, {1}...; only attributes that have placeholders.
        public readonly JObject Values = new();
    }

    // Every level must carry the same attributes in the same order and the same wording once numbers
    // are masked. The numbers that differ between levels become the placeholders, in text order.
    private static Template? TryTemplate(Group group) {
        if (group.Rows.Any(row => row.Level == "" || row.Level == ALL_LEVELS)) {
            return null;
        }
        string[] attributes = group.Rows[0].Row.Properties().Select(prop => prop.Name).ToArray();
        if (group.Rows.Any(row => !row.Row.Properties().Select(prop => prop.Name).SequenceEqual(attributes))) {
            return null;
        }

        var template = new Template();
        foreach ((string level, _, _) in group.Rows) {
            template.Values[level] = new JObject();
        }
        foreach (string attribute in attributes) {
            var texts = group.Rows.Select(row => row.Row[attribute]?.ToString() ?? "").ToList();
            if (texts.Select(text => Number.Replace(text, "#")).Distinct().Count() != 1) {
                return null;
            }
            var tokens = texts.Select(text => Number.Matches(text).Select(match => match.Value).ToArray()).ToList();
            var varying = Enumerable.Range(0, tokens[0].Length)
                .Where(index => tokens.Select(levelTokens => levelTokens[index]).Distinct().Count() > 1)
                .ToList();

            template.Text[attribute] = ReplaceNumbers(texts[0], index => {
                int slot = varying.IndexOf(index);
                return slot < 0 ? null : $"{{{slot}}}";
            });
            if (varying.Count == 0) {
                continue;
            }
            for (int row = 0; row < group.Rows.Count; row++) {
                ((JObject) template.Values[group.Rows[row].Level]!)[attribute] =
                    new JArray(varying.Select(index => tokens[row][index]));
            }
        }
        return template;
    }

    // Folds a translation's per-level rows into the source template's placeholders without changing a
    // single expanded row: every number that changes between the translation's levels must have the
    // exact per-level sequence of one source placeholder of the same attribute. Returns null otherwise.
    private static JObject? TryFoldTranslation(Group translation, Group source, Template template) {
        var sourceLevels = source.Rows.Select(row => row.Level).ToList();
        if (translation.Rows.Count != sourceLevels.Count
            || !translation.Rows.Select(row => row.Level).OrderBy(level => level, StringComparer.Ordinal)
                .SequenceEqual(sourceLevels.OrderBy(level => level, StringComparer.Ordinal))) {
            return null;
        }
        var rowsByLevel = translation.Rows.ToDictionary(row => row.Level, row => row.Row);
        var levelRows = sourceLevels.Select(level => rowsByLevel[level]).ToList();
        string[] attributes = levelRows[0].Properties().Select(prop => prop.Name).ToArray();
        if (levelRows.Any(row => !row.Properties().Select(prop => prop.Name).SequenceEqual(attributes))) {
            return null;
        }

        var folded = new JObject();
        foreach (string attribute in attributes) {
            var texts = levelRows.Select(row => row[attribute]?.ToString() ?? "").ToList();
            if (texts.Any(text => Placeholder.IsMatch(text))
                || texts.Select(text => Number.Replace(text, "#")).Distinct().Count() != 1) {
                return null;
            }
            var tokens = texts.Select(text => Number.Matches(text).Select(match => match.Value).ToArray()).ToList();

            var slots = new List<string[]>();
            if (template.Values[sourceLevels[0]]?[attribute] is JArray firstLevel) {
                for (int slot = 0; slot < firstLevel.Count; slot++) {
                    int captured = slot;
                    slots.Add(sourceLevels
                        .Select(level => template.Values[level]![attribute]![captured]!.ToString())
                        .ToArray());
                }
            }

            bool unmapped = false;
            string text = ReplaceNumbers(texts[0], index => {
                string[] sequence = tokens.Select(levelTokens => levelTokens[index]).ToArray();
                if (sequence.Distinct().Count() == 1) {
                    return null;
                }
                int slot = slots.FindIndex(candidate => candidate.SequenceEqual(sequence));
                if (slot < 0) {
                    unmapped = true;
                    return null;
                }
                return $"{{{slot}}}";
            });
            if (unmapped) {
                return null;
            }
            folded[attribute] = text;
        }
        return folded;
    }

    private static string ReplaceNumbers(string text, Func<int, string?> replacement) {
        int index = 0;
        return Number.Replace(text, match => replacement(index++) ?? match.Value);
    }

    public sealed class BuildResult {
        public readonly JObject Source = new();
        public readonly JObject Values = new();
        public readonly Dictionary<string, JObject> Translations = new(StringComparer.Ordinal);
        public int Templates;
        public int TemplatedRows;
        public int PerLevelGroups;
        public int PerLevelRows;
        public readonly Dictionary<string, int> DroppedPlaceholderRows = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> KeysWithoutSource = new(StringComparer.Ordinal);
    }

    // sourceRows and translationRows are the per-level JSON parsed straight from the XML.
    public static BuildResult Build(JObject sourceRows, IReadOnlyDictionary<string, JObject> translationRows) {
        var result = new BuildResult();
        var translationGroups = new Dictionary<string, Dictionary<string, Group>>(StringComparer.Ordinal);
        foreach ((string language, JObject rows) in translationRows) {
            result.Translations[language] = new JObject();
            result.DroppedPlaceholderRows[language] = 0;
            result.KeysWithoutSource[language] = 0;
            translationGroups[language] = GroupRows(rows).ToDictionary(group => $"{group.Id}-{group.Feature}-{group.Locale}");
        }

        var sourceIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (Group group in GroupRows(sourceRows)) {
            string identity = $"{group.Id}-{group.Feature}-{group.Locale}";
            sourceIdentities.Add(identity);

            Template? template = TryTemplate(group);
            var folded = new Dictionary<string, JObject>(StringComparer.Ordinal);
            if (template != null) {
                foreach ((string language, Dictionary<string, Group> groups) in translationGroups) {
                    if (!groups.TryGetValue(identity, out Group? translation)
                        || translation.Rows.All(row => IsClientPlaceholder(row.Row))) {
                        continue;
                    }
                    JObject? text = translation.Rows.Any(row => IsClientPlaceholder(row.Row))
                        ? null
                        : TryFoldTranslation(translation, group, template);
                    if (text == null) {
                        template = null;
                        break;
                    }
                    folded[language] = text;
                }
            }

            if (template != null) {
                string key = GroupKey(group.Id, group.Feature, group.Locale);
                result.Source[key] = template.Text;
                result.Values[key] = template.Values;
                foreach ((string language, JObject text) in folded) {
                    result.Translations[language][key] = text;
                }
                foreach ((string language, Dictionary<string, Group> groups) in translationGroups) {
                    if (!folded.ContainsKey(language) && groups.TryGetValue(identity, out Group? translation)) {
                        result.DroppedPlaceholderRows[language] += translation.Rows.Count;
                    }
                }
                result.Templates++;
                result.TemplatedRows += group.Rows.Count;
                continue;
            }

            foreach ((_, string key, JObject row) in group.Rows) {
                result.Source[key] = row;
            }
            foreach ((string language, Dictionary<string, Group> groups) in translationGroups) {
                if (!groups.TryGetValue(identity, out Group? translation)) {
                    continue;
                }
                foreach ((_, string key, JObject row) in translation.Rows) {
                    if (IsClientPlaceholder(row)) {
                        result.DroppedPlaceholderRows[language]++;
                    } else {
                        result.Translations[language][key] = row;
                    }
                }
            }
            result.PerLevelGroups++;
            result.PerLevelRows += group.Rows.Count;
        }

        // Weblate only offers keys the Korean source has; keep anything else rather than lose it.
        foreach ((string language, Dictionary<string, Group> groups) in translationGroups) {
            foreach ((string identity, Group translation) in groups) {
                if (sourceIdentities.Contains(identity)) {
                    continue;
                }
                foreach ((_, string key, JObject row) in translation.Rows) {
                    result.Translations[language][key] = row;
                    result.KeysWithoutSource[language]++;
                }
            }
        }
        return result;
    }

    // Template keys become one row per level, in the template's position, so rows of different features
    // for the same id and level keep the source's relative order (the client keeps the last one).
    public static JObject Expand(JObject rows, JObject values) {
        var expanded = new JObject();
        foreach (JProperty prop in rows.Properties()) {
            if (!IsTemplateKey(prop.Name)) {
                expanded[prop.Name] = prop.Value;
                continue;
            }
            foreach ((string key, JObject row) in ExpandTemplate(prop.Name, prop.Value as JObject ?? new JObject(), values)) {
                expanded[key] = row;
            }
        }
        return expanded;
    }

    private static IEnumerable<(string Key, JObject Row)> ExpandTemplate(string templateKey, JObject text, JObject values) {
        TryParseKey(templateKey, out string id, out _, out string feature, out string locale);
        if (values[templateKey] is not JObject levels || levels.Count == 0) {
            throw new InvalidDataException($"'{templateKey}' has no per-level values in LevelValues");
        }
        foreach (JProperty level in levels.Properties()) {
            var numbers = level.Value as JObject ?? new JObject();
            var row = new JObject();
            foreach (JProperty attribute in text.Properties()) {
                string value = attribute.Value.ToString();
                var slots = numbers[attribute.Name] as JArray;
                row[attribute.Name] = Placeholder.Replace(value, match => {
                    int slot = int.Parse(match.Groups[1].Value);
                    if (slots == null || slot >= slots.Count) {
                        throw new InvalidDataException(
                            $"'{templateKey}' {attribute.Name} uses {{{slot}}}, but level {level.Name} has {slots?.Count ?? 0} value(s)");
                    }
                    return slots[slot]!.ToString();
                });
            }
            yield return ($"{id}|{level.Name}-{feature}-{locale}", row);
        }
    }

    // Problems that would make JSON -> XML fail or write a broken row, one message each.
    public static List<string> Validate(JObject rows, JObject values, JObject? source) {
        var issues = new List<string>();
        foreach (JProperty prop in rows.Properties()) {
            if (!IsTemplateKey(prop.Name)) {
                continue;
            }
            if (source != null && source[prop.Name] == null) {
                issues.Add($"'{prop.Name}' has no Korean source template");
                continue;
            }
            try {
                _ = ExpandTemplate(prop.Name, prop.Value as JObject ?? new JObject(), values).ToList();
            } catch (InvalidDataException ex) {
                issues.Add(ex.Message);
            }
        }
        return issues;
    }
}
