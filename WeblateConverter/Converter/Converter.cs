using System.Xml;
using Newtonsoft.Json.Linq;
using WeblateConverter.Enum;

namespace WeblateConverter.Converter;

public class Converter {
    private string? _cachedProjectRoot;

    /// <summary>
    /// Resolves the repository root robustly, regardless of where the executable lives
    /// (Debug/Release, dotnet run, published single-file, or a CI artifacts directory).
    /// Order: MS2_REPO_ROOT env var, then walk up from the exe location, then the CWD,
    /// looking for a directory that contains both the "Xml" and "WeblateConverter" folders.
    /// </summary>
    private string? GetProjectRoot() {
        if (_cachedProjectRoot != null) {
            return _cachedProjectRoot;
        }

        // 1. Explicit override for CI / non-standard layouts.
        string? envRoot = Environment.GetEnvironmentVariable("MS2_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && IsProjectRoot(envRoot)) {
            return _cachedProjectRoot = Path.GetFullPath(envRoot);
        }

        // 2. Walk up from the executable location, then 3. from the working directory.
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }) {
            string? dir = start;
            while (!string.IsNullOrEmpty(dir)) {
                if (IsProjectRoot(dir)) {
                    return _cachedProjectRoot = dir;
                }
                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        Console.WriteLine("Project root not found (set MS2_REPO_ROOT to the repository root)");
        return null;
    }

    private static bool IsProjectRoot(string dir) {
        return Directory.Exists(Path.Combine(dir, "Xml"))
            && Directory.Exists(Path.Combine(dir, "WeblateConverter"));
    }

    public bool ConvertXmlToJson(string locale) {
        if (ProcessLocaleStrings(locale)) {
            Console.WriteLine("Conversion successful");
            return true;
        }

        Console.WriteLine("Conversion failed");
        return false;
    }

    public bool ConvertKoreanWithMissingKeys() {
        if (ProcessKoreanWithMissingKeys()) {
            Console.WriteLine("Conversion successful");
            return true;
        }

        Console.WriteLine("Conversion failed");
        return false;
    }

    public bool ConvertJsonToXml(string locale) {
        if (ProcessJsonToXml(locale)) {
            Console.WriteLine("Conversion successful");
            return true;
        }

        Console.WriteLine("Conversion failed");
        return false;
    }

    public bool ConvertJsonToXmlAll() {
        Console.WriteLine("Converting JSON to XML for all languages found in Json folder...");

        string[] availableLanguages = GetAvailableLanguages();
        if (availableLanguages.Length == 0) {
            Console.WriteLine("No language directories found in Json folder");
            return false;
        }

        Console.WriteLine($"Found {availableLanguages.Length} languages: {string.Join(", ", availableLanguages)}");

        int successCount = 0;
        int failureCount = 0;
        foreach (string language in availableLanguages) {
            Console.WriteLine($"\n--- Processing language: {language} ---");

            try {
                if (ProcessJsonToXml(language)) {
                    Console.WriteLine($"Successfully converted {language}");
                    successCount++;
                } else {
                    Console.WriteLine($"Failed to convert {language}");
                    failureCount++;
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error converting {language}: {ex.Message}");
                failureCount++;
            }
        }

        Console.WriteLine("\n--- Conversion Summary ---");
        Console.WriteLine($"Total languages processed: {availableLanguages.Length}");
        Console.WriteLine($"Successful conversions: {successCount}");
        Console.WriteLine($"Failed conversions: {failureCount}");

        return failureCount == 0;
    }

    // Set of JSON files that fan out to multiple / synthetic XML files, so there is no
    // 1:1 source XML to structurally compare against. These get key-integrity checks only.
    private static readonly HashSet<string> SplitOrCombinedJson = new(StringComparer.OrdinalIgnoreCase) {
        "skillname.json", "stringadditionaldescription.json",
        "stringskilldescription.json", "questdescription_final.json"
    };

    /// <summary>
    /// Verifies JSON->XML for every locale without writing anything: confirms each generated
    /// file has the same element/attribute structure as the source XML, and that every key is
    /// well-formed. Returns true only if all locales are clean (usable as a CI gate).
    /// </summary>
    public bool VerifyAll() {
        string[] languages = GetAvailableLanguages();
        if (languages.Length == 0) {
            Console.WriteLine("No language directories found in Json folder");
            return false;
        }

        bool ok = true;
        foreach (string language in languages) {
            Console.WriteLine($"\n=== Verifying {language} ===");
            if (!Verify(language)) {
                ok = false;
            }
        }

        Console.WriteLine(ok ? "\nVerification passed for all locales." : "\nVerification FAILED.");
        return ok;
    }

    public bool Verify(string locale) {
        FileInfo[] jsonFiles = GetJsonFiles(locale);
        if (jsonFiles.Length == 0) {
            Console.WriteLine($"No JSON files found for locale {locale}");
            return false;
        }

        string? projectRoot = GetProjectRoot();
        if (projectRoot == null) {
            return false;
        }

        // The structural schema is language-independent; compare against this locale's own
        // source XML, falling back to the base folders when a per-locale file is absent.
        string[] schemaLocales = { GetOutputLocale(locale), "en", "kr" };

        int checkedFiles = 0, skipped = 0, structuralIssues = 0, keyIssues = 0;

        foreach (FileInfo jsonFile in jsonFiles) {
            JObject json;
            string jsonContent;
            try {
                jsonContent = File.ReadAllText(jsonFile.FullName);
                json = JObject.Parse(jsonContent);
            } catch (Exception ex) {
                Console.WriteLine($"  [PARSE] {jsonFile.Name}: {ex.Message}");
                structuralIssues++;
                continue;
            }

            // 1. Key integrity — a malformed key would silently corrupt attributes on output.
            foreach (JProperty prop in json.Properties()) {
                if (!KeyIsWellFormed(prop.Name, jsonFile.Name)) {
                    Console.WriteLine($"  [KEY] {jsonFile.Name}: '{prop.Name}' is not well-formed (delimiter collision?)");
                    keyIssues++;
                }
            }

            // 2. Structural comparison against the source XML (skip fan-out/synthetic files
            //    and empty translation files, which have nothing to compare).
            if (SplitOrCombinedJson.Contains(jsonFile.Name) || json.Count == 0) {
                skipped++;
                continue;
            }

            string xmlName = jsonFile.Name.Replace(".json", ".xml");
            SortedSet<string>? srcSig = FindSchemaSignature(projectRoot, schemaLocales, xmlName);
            if (srcSig == null) {
                // No populated source XML anywhere to define this file's schema.
                Console.WriteLine($"  [NO SOURCE] {jsonFile.Name}: no populated source XML to compare structure");
                skipped++;
                continue;
            }

            try {
                var generatedDoc = new XmlDocument();
                generatedDoc.LoadXml(ConvertJsonToXml(jsonContent, jsonFile.Name));
                SortedSet<string> genSig = StructuralSignature(generatedDoc);

                if (!genSig.SetEquals(srcSig)) {
                    var missing = new SortedSet<string>(srcSig, StringComparer.Ordinal);
                    missing.ExceptWith(genSig);
                    var extra = new SortedSet<string>(genSig, StringComparer.Ordinal);
                    extra.ExceptWith(srcSig);

                    // A wrong element name, or a key-attribute swap (an attribute present only in
                    // the source AND one present only in the generated output), is a converter
                    // lookup fault. Attribute differences in only one direction are value-column
                    // drift in the JSON data (a missing/typo'd column) — reported but not failed.
                    bool elementFault = missing.Concat(extra).Any(s => s.StartsWith("el:"));
                    var attrMissing = missing.Where(s => s.StartsWith("attr:")).ToList();
                    var attrExtra = extra.Where(s => s.StartsWith("attr:")).ToList();
                    bool keyAttrFault = attrMissing.Count > 0 && attrExtra.Count > 0;

                    if (elementFault || keyAttrFault) {
                        structuralIssues++;
                        Console.WriteLine($"  [STRUCT] {jsonFile.Name}: source={string.Join(",", srcSig)} | generated={string.Join(",", genSig)}");
                    } else {
                        string drift = attrMissing.Count > 0
                            ? $"source-only: {string.Join(", ", attrMissing)}"
                            : $"json-only: {string.Join(", ", attrExtra)}";
                        Console.WriteLine($"  [DATA]   {jsonFile.Name}: value-column drift ({drift})");
                    }
                }
                checkedFiles++;
            } catch (Exception ex) {
                Console.WriteLine($"  [ERROR] {jsonFile.Name}: {ex.Message}");
                structuralIssues++;
            }
        }

        Console.WriteLine($"  {locale}: verified {checkedFiles} file(s), {skipped} skipped, " +
                          $"{structuralIssues} structural issue(s), {keyIssues} key issue(s)");
        return structuralIssues == 0 && keyIssues == 0;
    }

    // Returns the structural signature of the first source XML (across the candidate locales)
    // that actually contains data. Empty source files carry no schema, so they are skipped.
    private static SortedSet<string>? FindSchemaSignature(string projectRoot, string[] locales, string xmlName) {
        foreach (string locale in locales) {
            string path = Path.Combine(projectRoot, "Xml", "string", locale, xmlName);
            if (!File.Exists(path)) {
                continue;
            }
            try {
                var doc = new XmlDocument();
                doc.Load(path);
                SortedSet<string> sig = StructuralSignature(doc);
                if (sig.Count > 0) {
                    return sig;
                }
            } catch {
                // Unreadable/malformed source — try the next candidate locale.
            }
        }
        return null;
    }

    // Element names + attribute names, excluding feature/locale (their presence depends on
    // emptiness) and ignoring order — the parts that must match between source and generated.
    private static SortedSet<string> StructuralSignature(XmlDocument document) {
        var signature = new SortedSet<string>(StringComparer.Ordinal);
        XmlNode? root = document.SelectSingleNode("ms2");
        if (root == null) {
            return signature;
        }

        foreach (XmlNode child in root.ChildNodes) {
            if (child.NodeType != XmlNodeType.Element) {
                continue;
            }
            signature.Add("el:" + child.Name);
            if (child.Attributes == null) {
                continue;
            }
            foreach (XmlAttribute attribute in child.Attributes) {
                if (attribute.Name is "feature" or "locale") {
                    continue;
                }
                signature.Add("attr:" + attribute.Name);
            }
        }
        return signature;
    }

    // A well-formed key is exactly "identity-feature-locale" (3 dash-segments); the identity is
    // "primary" or, for compound-key files, "primary|secondary". Extra dashes mean a collision.
    private bool KeyIsWellFormed(string combinedKey, string fileName) {
        // An empty primary key (e.g. "--") is legitimate data — some files carry empty
        // placeholder rows like <key id="" name="" /> — so only the segment count matters.
        string[] parts = combinedKey.Split('-');
        if (parts.Length != 3) {
            return false;
        }

        string identity = parts[0];
        string? secondaryAttr = GetSecondaryKeyAttributeName(fileName);
        if (secondaryAttr != null) {
            // primary, or primary|secondary — never more than one pipe.
            return identity.Split('|').Length <= 2;
        }
        return !identity.Contains('|');
    }

    public void Select() {
        Console.WriteLine("Select 1 to convert XML to JSON");
        Console.WriteLine("Select 2 to convert JSON to XML");
        Console.WriteLine("Select 3 to convert latest Korean XML (kr_latest) to JSON (ko) with missing keys from other languages");
        Console.WriteLine("Select 4 to convert JSON to XML for all languages found in Json folder");
        Console.WriteLine("Select 5 to verify JSON to XML conversion integrity for all languages");
        string? input = Console.ReadLine();

        if (input == "1") {
            XmlToJson();
        } else if (input == "2") {
            JsonToXml();
        } else if (input == "3") {
            XmlToJsonKoreanWithMissingKeys();
        } else if (input == "4") {
            JsonToXmlAllLanguages();
        } else if (input == "5") {
            VerifyAll();
        } else {
            Console.WriteLine("Invalid input");
            return;
        }
    }
    private void XmlToJson() {
        Console.WriteLine("Enter the locale you want to convert to JSON");
        string? locale = Console.ReadLine();

        // get all enum names in Locale
        string[] enumNames = System.Enum.GetNames(typeof(Locale));

        // Check if locale1 is a valid enum
        while (System.Enum.TryParse<Locale>(locale, out _) == false) {
            Console.WriteLine($"Invalid locale, valid locales are: {string.Join(", ", enumNames)}");
            locale = Console.ReadLine();
        }

        if (ProcessLocaleStrings(locale)) {
            Console.WriteLine("Conversion successful");
        } else {
            Console.WriteLine("Conversion failed");
        }

        // return to Select if user wants to, otherwise exit
        Console.WriteLine("Do you want to convert another locale? (y/n)");
        string? input = Console.ReadLine();
        if (input == "y") {
            Select();
        } else {
            return;
        }
    }

    private void XmlToJsonKoreanWithMissingKeys() {
        Console.WriteLine("Converting latest Korean XML (kr_latest) to JSON (ko) with missing keys from other languages...");

        if (ProcessKoreanWithMissingKeys()) {
            Console.WriteLine("Conversion successful");
        } else {
            Console.WriteLine("Conversion failed");
        }

        // return to Select if user wants to, otherwise exit
        Console.WriteLine("Do you want to perform another conversion? (y/n)");
        string? input = Console.ReadLine();
        if (input == "y") {
            Select();
        } else {
            return;
        }
    }

    private void JsonToXml() {
        Console.WriteLine("Enter the locale you want to convert from JSON to XML");
        string? locale = Console.ReadLine();

        // get all enum names in Locale
        string[] enumNames = System.Enum.GetNames(typeof(Locale));

        // Check if locale is a valid enum
        while (System.Enum.TryParse<Locale>(locale, out _) == false) {
            Console.WriteLine($"Invalid locale, valid locales are: {string.Join(", ", enumNames)}");
            locale = Console.ReadLine();
        }

        if (ProcessJsonToXml(locale)) {
            Console.WriteLine("Conversion successful");
        } else {
            Console.WriteLine("Conversion failed");
        }

        // return to Select if user wants to, otherwise exit
        Console.WriteLine("Do you want to convert another locale? (y/n)");
        string? input = Console.ReadLine();
        if (input == "y") {
            Select();
        } else {
            return;
        }
    }

    private bool ProcessLocaleStrings(string locale) {
        FileInfo[] files = GetFiles(locale);

        if (files.Length == 0) {
            Console.WriteLine($"No {locale} files found");
            return false;
        }

        // Parse all enStrings to XML
        Dictionary<string, XmlDocument> xmls = new Dictionary<string, XmlDocument>();
        foreach (FileInfo file in files) {
            try {
                XmlDocument xml = new XmlDocument();
                xml.Load(file.FullName);
                xmls.Add(file.Name, xml);
            } catch (Exception ex) {
                Console.WriteLine($"Error loading {file.Name}: {ex.Message}");
            }
        }


        var questJsons = new List<string>();
        var scriptQuestJsons = new List<string>();
        var skillJsons = new List<string>();
        var skillDescriptions = new List<string>();
        var skillAdditionalDescriptions = new List<string>();
        foreach ((string name, XmlDocument xml) in xmls) {
            if (name == null) {
                continue;
            }

            if (name.Contains("questdescription")) {
                Console.WriteLine(" questdescription");
            }
            Console.WriteLine($"Parsing {name}");
            string? json = Parse(xml, name);
            if (json == null) {
                Console.WriteLine($"Failed to parse {name}");
                continue;
            }

            if (locale is not "kr" and not "ko") {
                switch (name) {
                    case not null when name.StartsWith("questdescription"):
                        questJsons.Add(json);
                        continue;
                    case not null when name.StartsWith("skillname"):
                        skillJsons.Add(json);
                        continue;
                    case not null when name.StartsWith("korskilldescription"):
                        skillDescriptions.Add(json);
                        continue;
                    case not null when name.StartsWith("koradditionaldescription"):
                        skillAdditionalDescriptions.Add(json);
                        continue;
                    case not null when name.StartsWith("scriptquest"):
                        scriptQuestJsons.Add(json);
                        continue;
                }
            }


            SaveToJson(name!, json, locale);
        }

        if (locale is not "kr" and not "ko") {
            SaveToJson("questdescription_final.xml", CombineJsons(questJsons), locale);
            SaveToJson("skillname.xml", CombineJsons(skillJsons), locale);
            SaveToJson("stringskilldescription.xml", CombineJsons(skillDescriptions), locale);
            SaveToJson("stringadditionaldescription.xml", CombineJsons(skillAdditionalDescriptions), locale);
            SaveToJson("scriptquest.xml", CombineJsons(scriptQuestJsons), locale);
        }
        return true;
    }

    private bool ProcessKoreanWithMissingKeys() {
        string masterLocale = "kr_latest"; // XML source uses latest Korean strings
        string outputLocale = "ko"; // JSON output goes to "ko"
        string[] otherLocales = {
            "en",
            "jp",
            "cn"
        };

        // Get Korean files first
        FileInfo[] koreanFiles = GetFiles(masterLocale);
        if (koreanFiles.Length == 0) {
            Console.WriteLine($"No {masterLocale} files found");
            return false;
        }

        // Parse Korean XMLs
        Dictionary<string, XmlDocument> koreanXmls = new Dictionary<string, XmlDocument>();
        foreach (FileInfo file in koreanFiles) {
            try {
                XmlDocument xml = new XmlDocument();
                xml.Load(file.FullName);
                koreanXmls.Add(file.Name, xml);
            } catch (Exception ex) {
                Console.WriteLine($"Error loading {file.Name}: {ex.Message}");
            }
        }

        // Get all keys from other languages that might be missing in Korean
        var (missingKeysByFile, sourceKeyData) = GetMissingKeysFromOtherLanguages(masterLocale, otherLocales);

        // Process Korean files and add missing keys
        var questJsons = new List<JObject>();
        var skillJsons = new List<JObject>();
        var skillDescriptions = new List<JObject>();
        var skillAdditionalDescriptions = new List<JObject>();

        foreach ((string fileName, XmlDocument xml) in koreanXmls) {
            if (fileName == null) {
                continue;
            }

            Console.WriteLine($"Processing {fileName} with missing keys...");

            // Parse the Korean XML to JSON
            string? json = Parse(xml, fileName);
            if (json == null) {
                Console.WriteLine($"Failed to parse {fileName}");
                continue;
            }

            // Add missing keys to the JSON
            JObject enhancedJsonObject = AddMissingKeysToJsonObject(json, fileName, missingKeysByFile, sourceKeyData);

            // Handle special file groupings (same logic as original)
            switch (fileName) {
                case not null when fileName.StartsWith("questdescription"):
                    questJsons.Add(enhancedJsonObject);
                    continue;
                case not null when fileName.StartsWith("skillname"):
                    skillJsons.Add(enhancedJsonObject);
                    continue;
                case not null when fileName.StartsWith("korskilldescription"):
                    skillDescriptions.Add(enhancedJsonObject);
                    continue;
                case not null when fileName.StartsWith("koradditionaldescription"):
                    skillAdditionalDescriptions.Add(enhancedJsonObject);
                    continue;
            }

            SaveToJson(fileName!, enhancedJsonObject.ToString(), outputLocale);
        }

        // Save combined files with missing keys from other languages
        SaveToJson("questdescription_final.xml", AddMissingKeysToJsonString(CombineJsons(questJsons), "questdescription_final.xml", missingKeysByFile, sourceKeyData), outputLocale);
        SaveToJson("skillname.xml", AddMissingKeysToJsonString(CombineJsons(skillJsons), "skillname.xml", missingKeysByFile, sourceKeyData), outputLocale);
        SaveToJson("stringskilldescription.xml", AddMissingKeysToJsonString(CombineJsons(skillDescriptions), "stringskilldescription.xml", missingKeysByFile, sourceKeyData), outputLocale);
        SaveToJson("stringadditionaldescription.xml", AddMissingKeysToJsonString(CombineJsons(skillAdditionalDescriptions), "stringadditionaldescription.xml", missingKeysByFile, sourceKeyData), outputLocale);

        return true;
    }

    // Generation writes in place into Xml/string/<locale>, so untranslated files and entries are
    // preserved automatically (they simply aren't rewritten). The one thing that needs cleaning
    // is the individual quest/script source files, which are replaced by the combined outputs
    // (questdescription_final.xml / scriptquest.xml) — remove them so they don't co-exist.
    private void RemoveAbsorbedSourceFiles(string locale) {
        string? projectRoot = GetProjectRoot();
        if (projectRoot == null) {
            return;
        }

        string outputLocale = GetOutputLocale(locale);
        string dir = Path.Combine(projectRoot, "Xml", "string", outputLocale);
        if (!Directory.Exists(dir)) {
            return;
        }

        int removed = 0;
        foreach (string file in Directory.GetFiles(dir, "*.xml")) {
            if (IsAbsorbedByCombinedOutput(Path.GetFileName(file))) {
                File.Delete(file);
                removed++;
            }
        }

        if (removed > 0) {
            Console.WriteLine($"Removed {removed} individual quest/script file(s) from {outputLocale} (replaced by combined outputs)");
        }
    }

    private static bool IsAbsorbedByCombinedOutput(string fileName) {
        return fileName.StartsWith("questdescription_", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("scriptquest_", StringComparison.OrdinalIgnoreCase);
    }

    private bool ProcessJsonToXml(string locale) {
        // Get JSON files from the locale directory
        FileInfo[] jsonFiles = GetJsonFiles(locale);
        if (jsonFiles.Length == 0) {
            Console.WriteLine($"No JSON files found for locale {locale}");
            return false;
        }

        // Generation writes in place; drop the individual quest/script files that the combined
        // outputs replace so they don't co-exist.
        RemoveAbsorbedSourceFiles(locale);

        bool anyFailure = false;
        foreach (FileInfo jsonFile in jsonFiles) {
            Console.WriteLine($"Converting {jsonFile.Name} to XML...");

            try {
                string jsonContent = File.ReadAllText(jsonFile.FullName);

                // An empty translation file would blank an existing in-place file — skip it so
                // the current (source / previously generated) content is preserved.
                if (JObject.Parse(jsonContent).Count == 0) {
                    Console.WriteLine($"  Keeping existing file for empty {jsonFile.Name}");
                    continue;
                }

                // Handle questdescription_final.json specially - convert with sorted questIDs
                if (jsonFile.Name == "questdescription_final.json") {
                    ConvertQuestDescriptionFinalWithSorting(jsonContent, locale);
                } else if (jsonFile.Name == "skillname.json") {
                    // Handle skillname.json specially - split into multiple XML files based on skill ID ranges
                    ConvertSkillNameWithSplitting(jsonContent, locale);
                } else if (jsonFile.Name == "stringadditionaldescription.json") {
                    // Handle stringadditionaldescription.json specially - split into multiple XML files based on skill ID ranges
                    ConvertStringAdditionalDescriptionWithSplitting(jsonContent, locale);
                } else if (jsonFile.Name == "stringskilldescription.json") {
                    // Handle stringskilldescription.json specially - split into multiple XML files based on skill ID ranges
                    ConvertStringSkillDescriptionWithSplitting(jsonContent, locale);
                } else {
                    string xmlContent = ConvertJsonToXml(jsonContent, jsonFile.Name);

                    // Save XML file
                    string xmlFileName = jsonFile.Name.Replace(".json", ".xml");
                    SaveXmlFile(xmlFileName, xmlContent, locale);

                    Console.WriteLine($"Successfully converted {jsonFile.Name} to {xmlFileName}");
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error converting {jsonFile.Name}: {ex.Message}");
                anyFailure = true;
            }
        }

        return !anyFailure;
    }

    private void JsonToXmlAllLanguages() {
        Console.WriteLine("Converting JSON to XML for all languages found in Json folder...");

        // Get all language directories from the Json folder
        string[] availableLanguages = GetAvailableLanguages();

        if (availableLanguages.Length == 0) {
            Console.WriteLine("No language directories found in Json folder");
            return;
        }

        Console.WriteLine($"Found {availableLanguages.Length} languages: {string.Join(", ", availableLanguages)}");

        int successCount = 0;
        int failureCount = 0;

        foreach (string language in availableLanguages) {
            Console.WriteLine($"\n--- Processing language: {language} ---");

            try {
                if (ProcessJsonToXml(language)) {
                    Console.WriteLine($"✓ Successfully converted {language}");
                    successCount++;
                } else {
                    Console.WriteLine($"✗ Failed to convert {language}");
                    failureCount++;
                }
            } catch (Exception ex) {
                Console.WriteLine($"✗ Error converting {language}: {ex.Message}");
                failureCount++;
            }
        }

        Console.WriteLine($"\n--- Conversion Summary ---");
        Console.WriteLine($"Total languages processed: {availableLanguages.Length}");
        Console.WriteLine($"Successful conversions: {successCount}");
        Console.WriteLine($"Failed conversions: {failureCount}");

        // return to Select if user wants to, otherwise exit
        Console.WriteLine("\nDo you want to perform another conversion? (y/n)");
        string? input = Console.ReadLine();
        if (input == "y") {
            Select();
        } else {
            return;
        }
    }

    private string[] GetAvailableLanguages() {
        string? projectRoot = GetProjectRoot();
        if (projectRoot == null) {
            Console.WriteLine("Project root not found");
            return [];
        }

        // Combine the project root with the relative path to the "Json" folder
        string jsonPath = Path.Combine(projectRoot, "WeblateConverter", "Json");

        if (!Directory.Exists(jsonPath)) {
            Console.WriteLine($"Json directory not found at: {jsonPath}");
            return [];
        }

        // Get all subdirectories (language folders)
        DirectoryInfo jsonDir = new DirectoryInfo(jsonPath);
        DirectoryInfo[] languageDirs = jsonDir.GetDirectories();

        return languageDirs.Select(dir => dir.Name).ToArray();
    }

    private string CombineJsons(List<JObject> jsonObjects) {
        var combinedObject = new JObject();
        foreach (JObject jsonObj in jsonObjects) {
            foreach (var (key, value) in jsonObj) {
                combinedObject[key] = value;
            }
        }
        return combinedObject.ToString();
    }
    private string CombineJsons(List<string> jsons) {
        var jsonObject = new JObject();
        foreach (string json in jsons) {
            JObject jObject = JObject.Parse(json);
            foreach (var (key, value) in jObject) {
                jsonObject[key] = value;
            }
        }
        return jsonObject.ToString();
    }

    public bool SaveToJson(string name, string json, string locale) {
        string? projectRoot = GetProjectRoot();
        if (projectRoot == null) {
            Console.WriteLine("Project root not found");
            return false;
        }

        string jsonPath = Path.Combine(projectRoot, "WeblateConverter", "Json", locale, name.Replace(".xml", ".json"));
        string? directoryPath = Path.GetDirectoryName(jsonPath);

        if (!Directory.Exists(directoryPath)) {
            Directory.CreateDirectory(Path.Combine(projectRoot, "WeblateConverter", "Json", locale));
        }

        File.WriteAllText(jsonPath, json);
        return true;
    }

    private FileInfo[] GetFiles(string locale) {
        string? projectRoot = GetProjectRoot();
        if (projectRoot == null) {
            Console.WriteLine("Project root not found");
            return [];
        }

        // Combine the project root with the relative path to the "Xml" folder
        string xmlPath = Path.Combine(projectRoot, "Xml");

        Console.WriteLine($"Reading XML files from {xmlPath}");

        // Get directory information
        var directoryInfo = new DirectoryInfo(xmlPath);

        // Output directory information
        Console.WriteLine($"Directory: {directoryInfo.FullName}");

        // get specific directory name in directoryInfo
        DirectoryInfo? stringDirectory = directoryInfo.GetDirectories("string").FirstOrDefault();

        if (stringDirectory == null) {
            Console.WriteLine("String directory not found");
            return [];
        }

        // Get en directory information
        DirectoryInfo? localeDirectory = stringDirectory.GetDirectories(locale).FirstOrDefault();

        if (localeDirectory == null) {
            Console.WriteLine($"{locale} directory not found");
            return [];
        }

        // Get all files in the en directory
        FileInfo[] files = localeDirectory.GetFiles();
        return files;
    }

    public string? Parse(XmlDocument document, string fileName) {
        XmlNode? node = document.SelectSingleNode("ms2");
        if (node == null) {
            Console.WriteLine("ms2 node not found");
            return null;
        }

        if (node.ChildNodes.Count == 0) {
            Console.WriteLine($"No child nodes found in {document.BaseURI}");
            return null;
        }

        // Get key attribute names for this file type
        string primaryKeyAttr = GetKeyAttributeName(fileName);
        string? secondaryKeyAttr = GetSecondaryKeyAttributeName(fileName);

        var jsonObject = new JObject();
        foreach (XmlNode childNode in node.ChildNodes) {
            // Skip comment nodes
            if (childNode.NodeType == XmlNodeType.Comment) {
                continue;
            }

            XmlAttributeCollection? attributes = childNode.Attributes;
            if (attributes == null || attributes.Count == 0) {
                Console.WriteLine($"No attributes found in node in {document.BaseURI}");
                continue;
            }

            // Get primary key value
            XmlAttribute? primaryKeyAttribute = attributes[primaryKeyAttr];
            if (primaryKeyAttribute == null) {
                Console.WriteLine($"Primary key attribute '{primaryKeyAttr}' not found in {document.BaseURI}");
                continue;
            }
            string primaryKey = primaryKeyAttribute.Value;

            // Get secondary key value if needed
            string secondaryKey = "";
            if (secondaryKeyAttr != null) {
                XmlAttribute? secondaryKeyAttribute = attributes[secondaryKeyAttr];
                if (secondaryKeyAttribute != null) {
                    secondaryKey = secondaryKeyAttribute.Value;
                }
            }

            var attributesObject = new JObject();
            string feature = "";
            string locale = "";

            // Process all attributes
            foreach (XmlAttribute attribute in attributes) {
                if (attribute == null) {
                    continue;
                }

                // Skip primary and secondary key attributes as they're handled separately
                if (attribute.Name == primaryKeyAttr ||
                    (secondaryKeyAttr != null && attribute.Name == secondaryKeyAttr)) {
                    continue;
                }

                if (attribute.Name == "feature") {
                    feature = string.IsNullOrEmpty(attribute.Value) ? "" : attribute.Value;
                    continue;
                }
                if (attribute.Name == "locale") {
                    locale = string.IsNullOrEmpty(attribute.Value) ? "" : attribute.Value;
                    continue;
                }

                attributesObject[attribute.Name] = attribute.Value;
            }

            // Build the combined key
            string combinedKey;
            if (secondaryKeyAttr != null && !string.IsNullOrEmpty(secondaryKey)) {
                // Use compound key format: primaryKey|secondaryKey-feature-locale
                combinedKey = $"{primaryKey}|{secondaryKey}-{feature}-{locale}";
            } else {
                // Use simple key format: primaryKey-feature-locale
                combinedKey = $"{primaryKey}-{feature}-{locale}";
            }

            jsonObject[combinedKey] = attributesObject;
        }

        return jsonObject.ToString();
    }

    private (Dictionary<string, HashSet<string>>, Dictionary<string, Dictionary<string, JObject>>) GetMissingKeysFromOtherLanguages(string masterLocale, string[] otherLocales) {
        var missingKeysByFile = new Dictionary<string, HashSet<string>>();
        var sourceKeyData = new Dictionary<string, Dictionary<string, JObject>>();

        // Get all keys from master language (Korean)
        var masterKeys = GetAllKeysFromLanguage(masterLocale);

        // Check each other language for keys not in master
        foreach (string locale in otherLocales) {
            Console.WriteLine($"Scanning {locale} for missing keys...");
            var localeKeyData = GetAllKeysAndDataFromLanguage(locale);

            foreach (var (fileName, keyData) in localeKeyData) {
                // Map individual files to their combined file names if applicable
                string[] targetFiles = GetTargetFileNames(fileName);

                foreach (string targetFileName in targetFiles) {
                    if (!missingKeysByFile.ContainsKey(targetFileName)) {
                        missingKeysByFile[targetFileName] = new HashSet<string>();
                    }
                    if (!sourceKeyData.ContainsKey(targetFileName)) {
                        sourceKeyData[targetFileName] = new Dictionary<string, JObject>();
                    }

                    // Find keys that exist in this locale but not in master
                    if (masterKeys.ContainsKey(fileName)) {
                        foreach (var (key, data) in keyData) {
                            if (!masterKeys[fileName].Contains(key)) {
                                missingKeysByFile[targetFileName].Add(key);
                                sourceKeyData[targetFileName][key] = data;
                                Console.WriteLine($"Found missing key in {fileName} -> {targetFileName}: {key} (from {locale})");
                            }
                        }
                    } else {
                        // If the file doesn't exist in master at all, add all keys
                        foreach (var (key, data) in keyData) {
                            missingKeysByFile[targetFileName].Add(key);
                            sourceKeyData[targetFileName][key] = data;
                            Console.WriteLine($"Found missing key in {fileName} -> {targetFileName} (file not in master): {key} (from {locale})");
                        }
                    }
                }
            }
        }

        return (missingKeysByFile, sourceKeyData);
    }

    private Dictionary<string, HashSet<string>> GetAllKeysFromLanguage(string locale) {
        var keysByFile = new Dictionary<string, HashSet<string>>();

        FileInfo[] files = GetFiles(locale);
        if (files.Length == 0) {
            Console.WriteLine($"No files found for locale {locale}");
            return keysByFile;
        }

        foreach (FileInfo file in files) {
            try {
                XmlDocument xml = new XmlDocument();
                xml.Load(file.FullName);

                var keys = ExtractKeysFromXml(xml);
                if (keys.Count > 0) {
                    keysByFile[file.Name] = keys;
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error processing {file.Name} for {locale}: {ex.Message}");
            }
        }

        return keysByFile;
    }

    private Dictionary<string, Dictionary<string, JObject>> GetAllKeysAndDataFromLanguage(string locale) {
        var keyDataByFile = new Dictionary<string, Dictionary<string, JObject>>();

        FileInfo[] files = GetFiles(locale);
        if (files.Length == 0) {
            Console.WriteLine($"No files found for locale {locale}");
            return keyDataByFile;
        }

        foreach (FileInfo file in files) {
            try {
                XmlDocument xml = new XmlDocument();
                xml.Load(file.FullName);

                var keyData = ExtractKeysAndDataFromXml(xml);
                if (keyData.Count > 0) {
                    keyDataByFile[file.Name] = keyData;
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error processing {file.Name} for {locale}: {ex.Message}");
            }
        }

        return keyDataByFile;
    }

    private HashSet<string> ExtractKeysFromXml(XmlDocument document) {
        var keys = new HashSet<string>();

        XmlNode? node = document.SelectSingleNode("ms2");
        if (node == null) {
            return keys;
        }

        foreach (XmlNode childNode in node.ChildNodes) {
            // Skip comment nodes
            if (childNode.NodeType == XmlNodeType.Comment) {
                continue;
            }

            if (childNode.Attributes?.Count == 0) {
                continue;
            }

            // Get first attribute (the key)
            XmlAttribute keyAttribute = childNode.Attributes![0];
            string key = keyAttribute.Value;

            // Build the full key with feature and locale (same logic as Parse method)
            string feature = "";
            string locale = "";

            for (int i = 1; i < childNode.Attributes.Count; i++) {
                XmlAttribute attribute = childNode.Attributes[i];
                if (attribute.Name == "feature") {
                    feature = string.IsNullOrEmpty(attribute.Value) ? "" : attribute.Value;
                } else if (attribute.Name == "locale") {
                    locale = string.IsNullOrEmpty(attribute.Value) ? "" : attribute.Value;
                }
            }

            string fullKey = key + "-" + feature + "-" + locale;
            keys.Add(fullKey);
        }

        return keys;
    }

    private Dictionary<string, JObject> ExtractKeysAndDataFromXml(XmlDocument document) {
        var keyData = new Dictionary<string, JObject>();

        XmlNode? node = document.SelectSingleNode("ms2");
        if (node == null) {
            return keyData;
        }

        foreach (XmlNode childNode in node.ChildNodes) {
            // Skip comment nodes
            if (childNode.NodeType == XmlNodeType.Comment) {
                continue;
            }

            if (childNode.Attributes?.Count == 0) {
                continue;
            }

            // Get first attribute (the key)
            XmlAttribute keyAttribute = childNode.Attributes![0];
            string key = keyAttribute.Value;

            // Build the full key and data object (same logic as Parse method)
            string feature = "";
            string locale = "";
            var attributesObject = new JObject();

            for (int i = 1; i < childNode.Attributes.Count; i++) {
                XmlAttribute attribute = childNode.Attributes[i];
                if (attribute.Name == "feature") {
                    feature = string.IsNullOrEmpty(attribute.Value) ? "" : attribute.Value;
                } else if (attribute.Name == "locale") {
                    locale = string.IsNullOrEmpty(attribute.Value) ? "" : attribute.Value;
                } else {
                    attributesObject[attribute.Name] = attribute.Value;
                }
            }

            string fullKey = key + "-" + feature + "-" + locale;
            keyData[fullKey] = attributesObject;
        }

        return keyData;
    }

    private JObject AddMissingKeysToJsonObject(string originalJson, string fileName, Dictionary<string, HashSet<string>> missingKeysByFile, Dictionary<string, Dictionary<string, JObject>> sourceKeyData) {
        JObject jsonObject = JObject.Parse(originalJson);

        if (missingKeysByFile.ContainsKey(fileName)) {
            foreach (string missingKey in missingKeysByFile[fileName]) {
                if (!jsonObject.ContainsKey(missingKey)) {
                    // Find the source data for this key
                    JObject? sourceValue = FindSourceValueForKey(missingKey, fileName, sourceKeyData);
                    if (sourceValue != null) {
                        jsonObject[missingKey] = sourceValue;
                        Console.WriteLine($"Added missing key to {fileName}: {missingKey} (copied from source)");
                    } else {
                        // Fallback to empty object if source not found
                        jsonObject[missingKey] = new JObject();
                        Console.WriteLine($"Added missing key to {fileName}: {missingKey} (empty placeholder)");
                    }
                }
            }
        }

        return jsonObject;
    }

    private string AddMissingKeysToJsonString(string originalJson, string fileName, Dictionary<string, HashSet<string>> missingKeysByFile, Dictionary<string, Dictionary<string, JObject>> sourceKeyData) {
        JObject jsonObject = JObject.Parse(originalJson);

        if (missingKeysByFile.ContainsKey(fileName)) {
            foreach (string missingKey in missingKeysByFile[fileName]) {
                if (!jsonObject.ContainsKey(missingKey)) {
                    // Find the source data for this key
                    JObject? sourceValue = FindSourceValueForKey(missingKey, fileName, sourceKeyData);
                    if (sourceValue != null) {
                        jsonObject[missingKey] = sourceValue;
                        Console.WriteLine($"Added missing key to combined {fileName}: {missingKey} (copied from source)");
                    } else {
                        // Fallback to empty object if source not found
                        jsonObject[missingKey] = new JObject();
                        Console.WriteLine($"Added missing key to combined {fileName}: {missingKey} (empty placeholder)");
                    }
                }
            }
        }

        return jsonObject.ToString();
    }

    private JObject? FindSourceValueForKey(string missingKey, string fileName, Dictionary<string, Dictionary<string, JObject>> sourceKeyData) {
        // Check if we have source data for this file
        if (sourceKeyData.ContainsKey(fileName) && sourceKeyData[fileName].ContainsKey(missingKey)) {
            return sourceKeyData[fileName][missingKey];
        }
        return null;
    }

    private string[] GetTargetFileNames(string fileName) {
        // Map individual files to their combined file names based on the grouping logic
        if (fileName.StartsWith("questdescription")) {
            return [
                fileName,
                "questdescription_final.xml"
            ];
        }

        if (fileName.StartsWith("skillname")) {
            return [
                fileName,
                "skillname.xml"
            ];
        }

        if (fileName.StartsWith("korskilldescription")) {
            return [
                fileName,
                "stringskilldescription.xml"
            ];
        }

        if (fileName.StartsWith("koradditionaldescription")) {
            return [
                fileName,
                "stringadditionaldescription.xml"
            ];
        }

        // For non-grouped files, just return the original filename.
        return [fileName];
    }

    private FileInfo[] GetJsonFiles(string locale) {
        string? projectRoot = GetProjectRoot();
        if (projectRoot == null) {
            Console.WriteLine("Project root not found");
            return [];
        }

        // Combine the project root with the relative path to the "Json" folder
        string jsonPath = Path.Combine(projectRoot, "WeblateConverter", "Json", locale);

        Console.WriteLine($"Reading JSON files from {jsonPath}");

        if (!Directory.Exists(jsonPath)) {
            Console.WriteLine($"JSON directory not found: {jsonPath}");
            return [];
        }

        var directoryInfo = new DirectoryInfo(jsonPath);
        FileInfo[] files = directoryInfo.GetFiles("*.json");
        return files;
    }

    private string ConvertJsonToXml(string jsonContent, string fileName) {
        JObject jsonObject = JObject.Parse(jsonContent);

        var xmlDoc = new XmlDocument();

        // Configure XML formatting
        xmlDoc.PreserveWhitespace = false;

        var declaration = xmlDoc.CreateXmlDeclaration("1.0", "utf-8", null);
        xmlDoc.AppendChild(declaration);

        var rootElement = xmlDoc.CreateElement("ms2");
        xmlDoc.AppendChild(rootElement);

        foreach (JProperty property in OrderJsonProperties(jsonObject, fileName)) {
            string combinedKey = property.Name;
            JToken? value = property.Value;

            // Parse the combined key to extract primary key, secondary key, feature, and locale
            var (primaryKey, secondaryKey, feature, locale) = ParseCombinedKey(combinedKey, fileName);

            // Get the correct element name based on the filename
            string elementName = GetXmlElementName(fileName);
            var keyElement = xmlDoc.CreateElement(elementName);

            // Add the main key attribute (first attribute)
            string keyAttributeName = GetKeyAttributeName(fileName);
            keyElement.SetAttribute(keyAttributeName, primaryKey);

            // Add secondary key attribute if it exists
            string? secondaryKeyAttrName = GetSecondaryKeyAttributeName(fileName);
            if (secondaryKeyAttrName != null && !string.IsNullOrEmpty(secondaryKey)) {
                keyElement.SetAttribute(secondaryKeyAttrName, secondaryKey);
            }

            // Add other attributes from the JSON value first
            if (value is JObject valueObject) {
                foreach (var (attrName, attrValue) in valueObject) {
                    if (attrValue != null) {
                        keyElement.SetAttribute(attrName, attrValue.ToString());
                    }
                }
            }

            // Add feature attribute at the end only if it's not empty and not "null"
            if (!string.IsNullOrEmpty(feature) && feature != "null") {
                keyElement.SetAttribute("feature", feature);
            }

            // Add locale attribute at the very end only if it's not empty and not "null"
            if (!string.IsNullOrEmpty(locale) && locale != "null") {
                keyElement.SetAttribute("locale", locale);
            }

            rootElement.AppendChild(keyElement);
        }

        // Format the XML with proper indentation and escape apostrophes
        string formattedXml = FormatXml(xmlDoc);
        return EscapeApostrophesInXml(formattedXml);
    }

    private IEnumerable<JProperty> OrderJsonProperties(JObject jsonObject, string fileName) {
        var properties = jsonObject.Properties().ToList();
        var parsedProperties = new List<(JProperty Property, long PrimaryKey, long? SecondaryKey, bool HasLocale, int Index)>();

        for (int index = 0; index < properties.Count; index++) {
            JProperty property = properties[index];
            var (primaryKey, secondaryKey, _, locale) = ParseCombinedKey(property.Name, fileName);

            if (!long.TryParse(primaryKey, out long numericPrimaryKey)) {
                return properties;
            }

            long? numericSecondaryKey = null;
            if (!string.IsNullOrEmpty(secondaryKey)) {
                if (!long.TryParse(secondaryKey, out long parsedSecondaryKey)) {
                    return properties;
                }

                numericSecondaryKey = parsedSecondaryKey;
            }

            parsedProperties.Add((property, numericPrimaryKey, numericSecondaryKey, !string.IsNullOrEmpty(locale), index));
        }

        // Within the same id, emit locale-specific entries AFTER generic (no-locale) ones. The
        // client stores keyed tables last-write-wins, so a matching locale (e.g. NA) then
        // overwrites the generic entry — while non-matching locales are filtered out and fall
        // back to the generic. This makes localized translations take precedence in-game.
        return parsedProperties
            .OrderBy(entry => entry.PrimaryKey)
            .ThenBy(entry => entry.SecondaryKey ?? long.MinValue)
            .ThenBy(entry => entry.HasLocale ? 1 : 0)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Property);
    }

    private (string primaryKey, string secondaryKey, string feature, string locale) ParseCombinedKey(string combinedKey, string fileName) {
        // For files with secondary keys, the format is: primaryKey|secondaryKey-feature-locale
        // For files without secondary keys, the format is: primaryKey-feature-locale

        string? secondaryKeyAttr = GetSecondaryKeyAttributeName(fileName);

        if (secondaryKeyAttr != null) {
            // Handle compound key format: primaryKey|secondaryKey-feature-locale
            // First check if this is actually a compound key (contains '|')
            if (combinedKey.Contains('|')) {
                string[] mainParts = combinedKey.Split('-');
                if (mainParts.Length >= 3) {
                    string keyPart = mainParts[0];
                    string feature = mainParts[1];
                    string locale = mainParts[2];

                    // Split the key part by '|' to get primary and secondary keys
                    string[] keyParts = keyPart.Split('|');
                    if (keyParts.Length == 2) {
                        string primaryKey = keyParts[0];
                        string secondaryKey = keyParts[1];

                        // Treat "null" as empty string (legacy support)
                        if (feature == "null") feature = "";
                        if (locale == "null") locale = "";

                        return (primaryKey, secondaryKey, feature, locale);
                    }
                }
            }

            // Fallback: treat as simple key even though file expects compound key
            string[] parts = combinedKey.Split('-');
            if (parts.Length >= 3) {
                string primaryKey = parts[0];
                string feature = parts[1];
                string locale = parts[2];

                // Treat "null" as empty string (legacy support)
                if (feature == "null") feature = "";
                if (locale == "null") locale = "";

                return (primaryKey, "", feature, locale);
            }

            // Final fallback for compound key files
            return (combinedKey, "", "", "");
        } else {
            // Handle simple key format: primaryKey-feature-locale
            string[] parts = combinedKey.Split('-');

            if (parts.Length != 3) {
                // Fallback: treat the whole string as the original key
                return (combinedKey, "", "", "");
            }

            string primaryKey = parts[0];
            string feature = parts[1];
            string locale = parts[2];

            // Treat "null" as empty string (legacy support)
            if (feature == "null") feature = "";
            if (locale == "null") locale = "";

            return (primaryKey, "", feature, locale);
        }
    }

    private void SaveXmlFile(string fileName, string xmlContent, string locale) {
        string? projectRoot = GetProjectRoot();
        if (projectRoot == null) {
            throw new InvalidOperationException("Project root not found");
        }

        // Map locale for output directory (ko -> kr)
        string outputLocale = GetOutputLocale(locale);

        // Create output directory path
        string outputPath = Path.Combine(projectRoot, "Xml", "string", outputLocale);

        if (!Directory.Exists(outputPath)) {
            Directory.CreateDirectory(outputPath);
        }

        string filePath = Path.Combine(outputPath, fileName);
        File.WriteAllText(filePath, xmlContent);

        Console.WriteLine($"Saved XML file: {filePath}");
    }

    private string GetOutputLocale(string inputLocale) {
        // Map ko to kr for output directory, keep other locales as-is
        return inputLocale switch {
            "ko" => "kr",
            _ => inputLocale
        };
    }

    private string FormatXml(XmlDocument xmlDoc) {
        var settings = new XmlWriterSettings {
            Indent = true,
            IndentChars = "\t", // Use tab for indentation
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new System.Text.UTF8Encoding(false), // UTF-8 without BOM
            OmitXmlDeclaration = false
        };

        using var memoryStream = new MemoryStream();
        using var xmlWriter = XmlWriter.Create(memoryStream, settings);
        xmlDoc.Save(xmlWriter);
        xmlWriter.Flush();

        // Convert the UTF-8 bytes back to string
        return System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    private string GetXmlElementName(string fileName) {
        // Remove .json extension if present
        string baseFileName = fileName.Replace(".json", "").Replace(".xml", "");

        // Create lookup dictionary for filename to XML element name mapping
        var elementNameLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            // Files that use <achieve> element
            {
                "achievedescription", "achieve"
            }, {
                "achievespecialrewarddescription", "achieve"
            }, {
                "addressname", "address"
            }, {
                "chapterdescription_epic", "chapter"
            }, {
                "chapterdescription_event", "chapter"
            }, {
                "chapterdescription_eventcn", "chapter"
            }, {
                "chapterdescription_eventcommon", "chapter"
            }, {
                "chapterdescription_eventkr", "chapter"
            }, {
                "chapterdescription_eventna", "chapter"
            }, {
                "chapterdescription_eventjp", "chapter"
            }, {
                "chapterdescription_field", "chapter"
            }, {
                "chapterdescription_famecontents", "chapter"
            }, {
                "chapterdescription_guide", "chapter"
            }, {
                "chapterdescription_guild", "chapter"
            }, {
                "chapterdescription_item", "chapter"
            }, {
                "chapterdescription_tutorial", "chapter"
            }, {
                "chapterdescription_world", "chapter"
            }, {
                "korjobdescription", "job"
            }, {
                "loadingdescription", "tip"
            }, {
                "maidmanufacturemessage", "Message"
            }, {
                "maidpropertystring", "Desc"
            }, {
                "pvpmode", "PVPMessage"
            }, {
                "questdescription_dailymission", "quest"
            }, {
                "questdescription_epic", "quest"
            }, {
                "questdescription_eventcn", "quest"
            }, {
                "questdescription_eventcommon", "quest"
            }, {
                "questdescription_eventkr", "quest"
            }, {
                "questdescription_eventna", "quest"
            }, {
                "questdescription_famecontents", "quest"
            }, {
                "questdescription_famefield", "quest"
            }, {
                "questdescription_famemission", "quest"
            }, {
                "questdescription_field", "quest"
            }, {
                "questdescription_guide", "quest"
            }, {
                "questdescription_guild", "quest"
            }, {
                "questdescription_item", "quest"
            }, {
                "questdescription_levelguide", "quest"
            }, {
                "questdescription_mentoring", "quest"
            }, {
                "questdescription_tutorial", "quest"
            }, {
                "questdescription_wedding", "quest"
            }, {
                "questdescription_world", "quest"
            }, {
                "questdescription_final", "quest"
            }, {
                "seasonnamecn", "season"
            }, {
                "seasonnamejp", "season"
            }, {
                "seasonnamekr", "season"
            }, {
                "seasonnamena", "season"
            }, {
                "seasonnameth", "season"
            }, {
                "stringfish", "fish"
            }, {
                "systemmailcontent", "mail"
            }, {
                "systemmailcontentcn", "mail"
            }, {
                "systemmailcontentjp", "mail"
            }, {
                "systemmailcontentkr", "mail"
            }, {
                "systemmailcontentna", "mail"
            }, {
                "systemmailcontentth", "mail"
            }
        };

        // Check if we have a specific mapping for this file
        if (elementNameLookup.TryGetValue(baseFileName, out string? elementName)) {
            return elementName;
        }

        // Default to "key" for files not in the lookup
        return "key";
    }

    private string GetKeyAttributeName(string fileName) {
        // Remove .json extension if present
        string baseFileName = fileName.Replace(".json", "").Replace(".xml", "");

        // Create lookup dictionary for filename to key attribute name mapping
        var keyAttributeLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            // Files that use different key attribute names
            {
                "addressname", "mapcode"
            }, {
                "kordynamicaction", "skillID"
            }, {
                "korjobdescription", "code"
            }, {
                "maidmanufacturemessage", "MaidID"
            }, {
                "maidpropertystring", "MaidID"
            }, {
                "pvpmode", "key"
            }, {
                "questdescription_dailymission", "questID"
            }, {
                "questdescription_epic", "questID"
            }, {
                "questdescription_eventcn", "questID"
            }, {
                "questdescription_eventcommon", "questID"
            }, {
                "questdescription_eventkr", "questID"
            }, {
                "questdescription_eventna", "questID"
            }, {
                "questdescription_famecontents", "questID"
            }, {
                "questdescription_famefield", "questID"
            }, {
                "questdescription_famemission", "questID"
            }, {
                "questdescription_field", "questID"
            }, {
                "questdescription_guide", "questID"
            }, {
                "questdescription_guild", "questID"
            }, {
                "questdescription_item", "questID"
            }, {
                "questdescription_levelguide", "questID"
            }, {
                "questdescription_mentoring", "questID"
            }, {
                "questdescription_tutorial", "questID"
            }, {
                "questdescription_wedding", "questID"
            }, {
                "questdescription_world", "questID"
            }, {
                "questdescription_final", "questID"
            },

            // Most files use "id" as the key attribute (this is the default)
            // Add more specific mappings as needed when you discover them
        };

        // Check if we have a specific mapping for this file
        if (keyAttributeLookup.TryGetValue(baseFileName, out string? keyAttributeName)) {
            return keyAttributeName;
        }

        // Default to "id" for files not in the lookup
        return "id";
    }

    private string? GetSecondaryKeyAttributeName(string fileName) {
        // Remove .json extension if present
        string baseFileName = fileName.Replace(".json", "").Replace(".xml", "");

        // Create lookup dictionary for filename to secondary key attribute name mapping
        var secondaryKeyLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            // Files that need secondary keys due to duplicate primary keys
            {
                "addressname", "blockCode"
            }, {
                "seasonnamena", "type"
            }, {
                "setitemoption", "partCount"
            }, {
                "koradditionaldescription", "level"
            }, {
                "koradditionaldescription_1", "level"
            }, {
                "koradditionaldescription_10", "level"
            }, {
                "koradditionaldescription_20", "level"
            }, {
                "koradditionaldescription_30", "level"
            }, {
                "koradditionaldescription_40", "level"
            }, {
                "koradditionaldescription_50", "level"
            }, {
                "koradditionaldescription_60", "level"
            }, {
                "koradditionaldescription_70", "level"
            }, {
                "koradditionaldescription_80", "level"
            }, {
                "koradditionaldescription_90", "level"
            }, {
                "koradditionaldescription_100", "level"
            }, {
                "koradditionaldescription_110", "level"
            }, {
                "korskilldescription", "level"
            }, {
                "korskilldescription_1", "level"
            }, {
                "korskilldescription_10", "level"
            }, {
                "korskilldescription_20", "level"
            }, {
                "korskilldescription_30", "level"
            }, {
                "korskilldescription_40", "level"
            }, {
                "korskilldescription_50", "level"
            }, {
                "korskilldescription_60", "level"
            }, {
                "korskilldescription_70", "level"
            }, {
                "korskilldescription_80", "level"
            }, {
                "korskilldescription_90", "level"
            }, {
                "korskilldescription_100", "level"
            }, {
                "korskilldescription_110", "level"
            }, {
                "stringadditionaldescription", "level"
            }, {
                "stringskilldescription", "level"
            }

            // Add more mappings as needed when you discover files with duplicate primary keys
        };

        // Check if we have a specific mapping for this file
        if (secondaryKeyLookup.TryGetValue(baseFileName, out string? secondaryKeyName)) {
            return secondaryKeyName;
        }

        // Return null if no secondary key is needed
        return null;
    }

    private void ConvertSkillNameWithSplitting(string jsonContent, string locale) {
        JObject jsonObject = JObject.Parse(jsonContent);

        // Create dictionaries to store skill entries for each file
        var skillFiles = new Dictionary<string, List<(int skillId, string key, JToken value)>> {
            {
                "skillname", new List<(int, string, JToken)>()
            }, {
                "skillname_1", new List<(int, string, JToken)>()
            }, {
                "skillname_10", new List<(int, string, JToken)>()
            }, {
                "skillname_20", new List<(int, string, JToken)>()
            }, {
                "skillname_30", new List<(int, string, JToken)>()
            }, {
                "skillname_40", new List<(int, string, JToken)>()
            }, {
                "skillname_50", new List<(int, string, JToken)>()
            }, {
                "skillname_60", new List<(int, string, JToken)>()
            }, {
                "skillname_70", new List<(int, string, JToken)>()
            }, {
                "skillname_80", new List<(int, string, JToken)>()
            }, {
                "skillname_90", new List<(int, string, JToken)>()
            }, {
                "skillname_100", new List<(int, string, JToken)>()
            }, {
                "skillname_110", new List<(int, string, JToken)>()
            }
        };

        foreach ((string combinedKey, JToken? value) in jsonObject) {
            try {
                // Parse the combined key to extract the skill ID
                var (primaryKey, secondaryKey, feature, localeFromKey) = ParseCombinedKey(combinedKey, "skillname.json");

                // Convert the primary key to int for categorization
                if (!int.TryParse(primaryKey, out int skillId)) {
                    Console.WriteLine($"Warning: Could not parse skill ID '{primaryKey}' from key '{combinedKey}'. Adding to default skillname.xml.");
                    skillFiles["skillname"].Add((0, combinedKey, value ?? new JObject()));
                    continue;
                }

                // Determine which file this skill belongs to based on the ranges you specified
                string targetFile = skillId switch {
                    >= 10000000 and <= 10099999 => "skillname_1",
                    >= 19900000 and <= 19999999 => "skillname_1",
                    >= 10100000 and <= 10199999 => "skillname_10",
                    >= 10200000 and <= 10299999 => "skillname_20",
                    >= 10300000 and <= 10399999 => "skillname_30",
                    >= 10400000 and <= 10499999 => "skillname_40",
                    >= 10500000 and <= 10599999 => "skillname_50",
                    >= 10600000 and <= 10699999 => "skillname_60",
                    >= 10700000 and <= 10799999 => "skillname_70",
                    >= 10800000 and <= 10899999 => "skillname_80",
                    >= 10900000 and <= 10999999 => "skillname_90",
                    >= 11000000 and <= 11099999 => "skillname_100",
                    >= 11100000 and <= 11199999 => "skillname_110",
                    _ => "skillname"
                };

                skillFiles[targetFile].Add((skillId, combinedKey, value ?? new JObject()));

            } catch (Exception ex) {
                Console.WriteLine($"Error processing skill key '{combinedKey}': {ex.Message}");
                // Add to default file if there's an error
                skillFiles["skillname"].Add((0, combinedKey, value ?? new JObject()));
            }
        }

        // Convert each file's entries to XML and save
        foreach (var (fileName, entries) in skillFiles) {
            if (entries.Count == 0) {
                Console.WriteLine($"No entries for {fileName}.xml, skipping.");
                continue;
            }

            // Sort entries by skill ID in ascending order
            entries.Sort((a, b) => a.skillId.CompareTo(b.skillId));

            // Create a new sorted JSON object for this file
            var sortedJsonObject = new JObject();
            foreach (var (skillId, key, value) in entries) {
                sortedJsonObject[key] = value;
            }

            // Convert to XML
            string xmlContent = ConvertJsonToXml(sortedJsonObject.ToString(), "skillname.json");

            // Save the XML file
            SaveXmlFile($"{fileName}.xml", xmlContent, locale);

            Console.WriteLine($"Successfully converted {entries.Count} skills to {fileName}.xml");
        }

        Console.WriteLine($"Successfully split skillname.json into multiple XML files based on skill ID ranges");
    }

    // Identifies stale "bare" keys in a level-keyed file: keys with no secondary in the key
    // (e.g. "100000056--") that carry the secondary value inside their object AND duplicate a
    // proper compound key ("100000056|5--"). These are historical collapse artifacts; the
    // compound key is authoritative (it matches the source of truth), so the bare key is dropped.
    private HashSet<string> CollectStaleBareLevelKeys(JObject jsonObject, string fileName) {
        var stale = new HashSet<string>();
        string? secondaryAttr = GetSecondaryKeyAttributeName(fileName);
        if (secondaryAttr == null) {
            return stale;
        }

        var compoundIdentities = new HashSet<string>();
        foreach (JProperty prop in jsonObject.Properties()) {
            var (primary, secondary, _, _) = ParseCombinedKey(prop.Name, fileName);
            if (!string.IsNullOrEmpty(secondary)) {
                compoundIdentities.Add($"{primary}|{secondary}");
            }
        }

        foreach (JProperty prop in jsonObject.Properties()) {
            var (primary, secondary, _, _) = ParseCombinedKey(prop.Name, fileName);
            if (!string.IsNullOrEmpty(secondary)) {
                continue;
            }
            if (prop.Value is JObject valueObject
                && valueObject.TryGetValue(secondaryAttr, out JToken? levelToken)) {
                string level = levelToken?.ToString() ?? "";
                if (!string.IsNullOrEmpty(level) && compoundIdentities.Contains($"{primary}|{level}")) {
                    stale.Add(prop.Name);
                }
            }
        }

        return stale;
    }

    private void ConvertStringAdditionalDescriptionWithSplitting(string jsonContent, string locale) {
        JObject jsonObject = JObject.Parse(jsonContent);
        HashSet<string> staleBareKeys = CollectStaleBareLevelKeys(jsonObject, "stringadditionaldescription.json");
        if (staleBareKeys.Count > 0) {
            Console.WriteLine($"  Dropping {staleBareKeys.Count} stale bare level key(s) (superseded by compound id|level keys)");
        }

        // Create dictionaries to store skill entries for each file
        var skillFiles = new Dictionary<string, List<(int skillId, int level, string key, JToken value)>> {
            {
                "koradditionaldescription", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_1", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_10", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_20", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_30", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_40", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_50", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_60", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_70", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_80", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_90", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_100", new List<(int, int, string, JToken)>()
            }, {
                "koradditionaldescription_110", new List<(int, int, string, JToken)>()
            }
        };

        foreach ((string combinedKey, JToken? value) in jsonObject) {
            if (staleBareKeys.Contains(combinedKey)) {
                continue;
            }
            try {
                // Parse the combined key to extract the skill ID
                var (primaryKey, secondaryKey, feature, localeFromKey) = ParseCombinedKey(combinedKey, "stringadditionaldescription.json");


                // Convert the primary key to int for categorization
                if (!int.TryParse(primaryKey, out int skillId)) {
                    Console.WriteLine($"Warning: Could not parse skill ID '{primaryKey}' from key '{combinedKey}'. Adding to default koradditionaldescription.xml.");
                    skillFiles["koradditionaldescription"].Add((0, 0, combinedKey, value ?? new JObject()));
                    continue;
                }

                // Extract level from the secondary key (not from JSON value)
                int level = 0;
                if (!string.IsNullOrEmpty(secondaryKey) && int.TryParse(secondaryKey, out int parsedLevel)) {
                    level = parsedLevel;
                } else {
                    // Fallback: try to extract from JSON value if secondary key parsing failed
                    if (value is JObject valueObject && valueObject.TryGetValue("level", out JToken? levelToken)) {
                        if (!int.TryParse(levelToken.ToString(), out level)) {
                            level = 0; // Default to 0 if level can't be parsed
                        }
                    }
                }

                // Determine which file this skill belongs to based on the same ranges as skillname
                string targetFile = skillId switch {
                    >= 10000000 and <= 10099999 => "koradditionaldescription_1",
                    >= 19900000 and <= 19999999 => "koradditionaldescription_1",
                    >= 10100000 and <= 10199999 => "koradditionaldescription_10",
                    >= 10200000 and <= 10299999 => "koradditionaldescription_20",
                    >= 10300000 and <= 10399999 => "koradditionaldescription_30",
                    >= 10400000 and <= 10499999 => "koradditionaldescription_40",
                    >= 10500000 and <= 10599999 => "koradditionaldescription_50",
                    >= 10600000 and <= 10699999 => "koradditionaldescription_60",
                    >= 10700000 and <= 10799999 => "koradditionaldescription_70",
                    >= 10800000 and <= 10899999 => "koradditionaldescription_80",
                    >= 10900000 and <= 10999999 => "koradditionaldescription_90",
                    >= 11000000 and <= 11099999 => "koradditionaldescription_100",
                    >= 11100000 and <= 11199999 => "koradditionaldescription_110",
                    _ => "koradditionaldescription"
                };

                skillFiles[targetFile].Add((skillId, level, combinedKey, value ?? new JObject()));

            } catch (Exception ex) {
                Console.WriteLine($"Error processing skill key '{combinedKey}': {ex.Message}");
                // Add to default file if there's an error
                skillFiles["koradditionaldescription"].Add((0, 0, combinedKey, value ?? new JObject()));
            }
        }

        // Convert each file's entries to XML and save
        foreach (var (fileName, entries) in skillFiles) {
            if (entries.Count == 0) {
                Console.WriteLine($"No entries for {fileName}.xml, skipping.");
                continue;
            }

            // Sort entries by skill ID first, then by level (secondary key) in ascending order
            entries.Sort((a, b) => {
                int primaryComparison = a.skillId.CompareTo(b.skillId);
                if (primaryComparison != 0) {
                    return primaryComparison;
                }
                // Both skillId and level are already integers, so this should work correctly
                return a.level.CompareTo(b.level);
            });


            // Create a new sorted JSON object for this file
            var sortedJsonObject = new JObject();
            foreach (var (skillId, level, key, value) in entries) {
                sortedJsonObject[key] = value;
            }

            // Convert to XML
            string xmlContent = ConvertJsonToXml(sortedJsonObject.ToString(), "stringadditionaldescription.json");

            // Save the XML file
            SaveXmlFile($"{fileName}.xml", xmlContent, locale);

            Console.WriteLine($"Successfully converted {entries.Count} additional descriptions to {fileName}.xml");
        }

        Console.WriteLine($"Successfully split stringadditionaldescription.json into multiple XML files based on skill ID ranges");
    }

    private void ConvertStringSkillDescriptionWithSplitting(string jsonContent, string locale) {
        JObject jsonObject = JObject.Parse(jsonContent);
        HashSet<string> staleBareKeys = CollectStaleBareLevelKeys(jsonObject, "stringskilldescription.json");
        if (staleBareKeys.Count > 0) {
            Console.WriteLine($"  Dropping {staleBareKeys.Count} stale bare level key(s) (superseded by compound id|level keys)");
        }

        // Create dictionaries to store skill entries for each file
        var skillFiles = new Dictionary<string, List<(int skillId, int level, string key, JToken value)>> {
            {
                "korskilldescription", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_1", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_10", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_20", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_30", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_40", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_50", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_60", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_70", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_80", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_90", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_100", new List<(int, int, string, JToken)>()
            }, {
                "korskilldescription_110", new List<(int, int, string, JToken)>()
            }
        };

        foreach ((string combinedKey, JToken? value) in jsonObject) {
            if (staleBareKeys.Contains(combinedKey)) {
                continue;
            }
            try {
                // Parse the combined key to extract the skill ID
                var (primaryKey, secondaryKey, feature, localeFromKey) = ParseCombinedKey(combinedKey, "stringskilldescription.json");

                // Convert the primary key to int for categorization
                if (!int.TryParse(primaryKey, out int skillId)) {
                    Console.WriteLine($"Warning: Could not parse skill ID '{primaryKey}' from key '{combinedKey}'. Adding to default korskilldescription.xml.");
                    skillFiles["korskilldescription"].Add((0, 0, combinedKey, value ?? new JObject()));
                    continue;
                }

                // Extract level from the secondary key (not from JSON value)
                int level = 0;
                if (!string.IsNullOrEmpty(secondaryKey) && int.TryParse(secondaryKey, out int parsedLevel)) {
                    level = parsedLevel;
                } else {
                    // Fallback: try to extract from JSON value if secondary key parsing failed
                    if (value is JObject valueObject && valueObject.TryGetValue("level", out JToken? levelToken)) {
                        if (!int.TryParse(levelToken.ToString(), out level)) {
                            level = 0; // Default to 0 if level can't be parsed
                        }
                    }
                }

                // Determine which file this skill belongs to based on the same ranges as skillname
                string targetFile = skillId switch {
                    >= 10000000 and <= 10099999 => "korskilldescription_1",
                    >= 19900000 and <= 19999999 => "korskilldescription_1",
                    >= 10100000 and <= 10199999 => "korskilldescription_10",
                    >= 10200000 and <= 10299999 => "korskilldescription_20",
                    >= 10300000 and <= 10399999 => "korskilldescription_30",
                    >= 10400000 and <= 10499999 => "korskilldescription_40",
                    >= 10500000 and <= 10599999 => "korskilldescription_50",
                    >= 10600000 and <= 10699999 => "korskilldescription_60",
                    >= 10700000 and <= 10799999 => "korskilldescription_70",
                    >= 10800000 and <= 10899999 => "korskilldescription_80",
                    >= 10900000 and <= 10999999 => "korskilldescription_90",
                    >= 11000000 and <= 11099999 => "korskilldescription_100",
                    >= 11100000 and <= 11199999 => "korskilldescription_110",
                    _ => "korskilldescription"
                };

                skillFiles[targetFile].Add((skillId, level, combinedKey, value ?? new JObject()));

            } catch (Exception ex) {
                Console.WriteLine($"Error processing skill key '{combinedKey}': {ex.Message}");
                // Add to default file if there's an error
                skillFiles["korskilldescription"].Add((0, 0, combinedKey, value ?? new JObject()));
            }
        }

        // Convert each file's entries to XML and save
        foreach (var (fileName, entries) in skillFiles) {
            if (entries.Count == 0) {
                Console.WriteLine($"No entries for {fileName}.xml, skipping.");
                continue;
            }

            // Sort entries by skill ID first, then by level (secondary key) in ascending order
            entries.Sort((a, b) => {
                int primaryComparison = a.skillId.CompareTo(b.skillId);
                if (primaryComparison != 0) {
                    return primaryComparison;
                }
                // Both skillId and level are already integers, so this should work correctly
                return a.level.CompareTo(b.level);
            });

            // Create a new sorted JSON object for this file
            var sortedJsonObject = new JObject();
            foreach (var (skillId, level, key, value) in entries) {
                sortedJsonObject[key] = value;
            }

            // Convert to XML
            string xmlContent = ConvertJsonToXml(sortedJsonObject.ToString(), "stringskilldescription.json");

            // Save the XML file
            SaveXmlFile($"{fileName}.xml", xmlContent, locale);

            Console.WriteLine($"Successfully converted {entries.Count} skill descriptions to {fileName}.xml");
        }

        Console.WriteLine($"Successfully split stringskilldescription.json into multiple XML files based on skill ID ranges");
    }

    private string EscapeApostrophesInXml(string xmlContent) {
        if (string.IsNullOrEmpty(xmlContent)) {
            return xmlContent;
        }

        // All data lives in attribute values (every element is self-closing), and the
        // XmlWriter already escapes <, >, &, and " (as &quot;) inside those values. The
        // only raw apostrophes left in the document are therefore inside attribute values,
        // so a global replace is safe and matches MS2's use of &apos;.
        return xmlContent.Replace("'", "&apos;");
    }

    private void ConvertQuestDescriptionFinalWithSorting(string jsonContent, string locale) {
        JObject jsonObject = JObject.Parse(jsonContent);

        // Create a list to store quest entries with their parsed quest IDs for sorting
        var questEntries = new List<(int questId, string key, JToken value)>();

        foreach ((string combinedKey, JToken? value) in jsonObject) {
            try {
                // Parse the combined key to extract the quest ID
                var (primaryKey, secondaryKey, feature, localeFromKey) = ParseCombinedKey(combinedKey, "questdescription_final.json");

                // Convert the primary key to int for sorting
                if (!int.TryParse(primaryKey, out int questId)) {
                    Console.WriteLine($"Warning: Could not parse quest ID '{primaryKey}' from key '{combinedKey}'. Skipping.");
                    continue;
                }

                questEntries.Add((questId, combinedKey, value ?? new JObject()));

            } catch (Exception ex) {
                Console.WriteLine($"Error processing quest key '{combinedKey}': {ex.Message}");
            }
        }

        // Sort by quest ID in ascending order
        questEntries.Sort((a, b) => a.questId.CompareTo(b.questId));

        // Create a new sorted JSON object
        var sortedJsonObject = new JObject();
        foreach (var (questId, key, value) in questEntries) {
            sortedJsonObject[key] = value;
        }

        // Convert to XML
        string xmlContent = ConvertJsonToXml(sortedJsonObject.ToString(), "questdescription_final.json");

        // Save the XML file
        SaveXmlFile("questdescription_final.xml", xmlContent, locale);

        Console.WriteLine($"Successfully converted questdescription_final.json to questdescription_final.xml with {questEntries.Count} quests sorted by questID");
    }
}
