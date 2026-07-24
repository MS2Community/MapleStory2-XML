using WeblateConverter.Converter;

var converter = new Converter();

if (args.Length > 0) {
    switch (args[0].ToLowerInvariant()) {
        case "xml-to-json" when args.Length == 2:
            Environment.ExitCode = converter.ConvertXmlToJson(args[1]) ? 0 : 1;
            return;
        case "json-to-xml" when args.Length == 2:
            Environment.ExitCode = converter.ConvertJsonToXml(args[1]) ? 0 : 1;
            return;
        case "json-to-xml-all":
            Environment.ExitCode = converter.ConvertJsonToXmlAll() ? 0 : 1;
            return;
        case "ko-with-missing":
            Environment.ExitCode = converter.ConvertKoreanWithMissingKeys() ? 0 : 1;
            return;
        case "verify" when args.Length == 2:
            Environment.ExitCode = converter.Verify(args[1]) ? 0 : 1;
            return;
        case "verify-all":
            Environment.ExitCode = converter.VerifyAll() ? 0 : 1;
            return;
        default:
            Console.WriteLine("Usage:");
            Console.WriteLine("  WeblateConverter xml-to-json <locale>");
            Console.WriteLine("  WeblateConverter json-to-xml <locale>");
            Console.WriteLine("  WeblateConverter json-to-xml-all");
            Console.WriteLine("  WeblateConverter ko-with-missing");
            Console.WriteLine("  WeblateConverter verify <locale>");
            Console.WriteLine("  WeblateConverter verify-all");
            Console.WriteLine();
            Console.WriteLine("  json-to-xml writes in place into Xml/string/<locale>; untranslated");
            Console.WriteLine("  files/entries are preserved, empty translation files are skipped.");
            Environment.ExitCode = 1;
            return;
    }
}

converter.Select();
