namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// Translation-coverage fitness gate (ADR-027): every base <c>*.resx</c> under <c>Source/</c> must
    /// have, for each required culture, a sibling <c>&lt;stem&gt;.&lt;culture&gt;.resx</c> that translates
    /// every key with a non-empty value. This fails the build when a new English string ships without its
    /// translation, instead of silently degrading to the English fallback at runtime — the missing-key gate
    /// the i18n posture previously lacked (only the advisory <c>MA0076</c> formatting analyzer existed).
    /// The required-culture list is supplied by the caller (e.g. the repo's supported-culture allowlist
    /// minus the default), so the rule is repo-agnostic and vacuous for single-locale repos.
    /// </summary>
    /// <param name="requiredCultures">
    /// The non-default cultures every base <c>.resx</c> must fully translate (e.g. <c>["es"]</c>). An empty
    /// collection makes the rule a no-op (single-locale repos need no coverage gate).
    /// </param>
    public static void ResourceTranslationsAreComplete(IReadOnlyCollection<string> requiredCultures)
    {
        if (requiredCultures.Count == 0)
        {
            return;
        }

        var sourceRoot = Path.Combine(Path.GetDirectoryName(FindUpwards("Directory.Packages.props"))!, "Source");

        var baseResxFiles = Directory
            .EnumerateFiles(sourceRoot, "*.resx", SearchOption.AllDirectories)
            .Where(p => !IsCultureSpecificResx(p) && !IsUnderBuildOutput(p))
            .Order(StringComparer.Ordinal)
            .ToList();

        var violations = baseResxFiles
            .SelectMany(basePath => requiredCultures
                .SelectMany(culture => MissingTranslations(sourceRoot, basePath, culture)))
            .ToList();

        ArchitectureAssert.NoViolations(violations,
            "every base .resx under Source/ must have a complete, non-empty translation for each required culture "
            + "(ADR-027) — add the missing key to the matching .<culture>.resx sibling");
    }

    // Returns one violation per key the culture sibling fails to translate (or one for a wholly-missing
    // sibling). Extracted from the rule body so the scan stays a flat projection rather than nested loops.
    private static IEnumerable<string> MissingTranslations(string sourceRoot, string basePath, string culture)
    {
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var siblingPath = Path.Combine(Path.GetDirectoryName(basePath)!, $"{stem}.{culture}.resx");

        if (!File.Exists(siblingPath))
        {
            return [$"  - {Relativize(sourceRoot, basePath)} has no '{culture}' translation ({stem}.{culture}.resx is missing)"];
        }

        var translated = ReadResxStringEntries(siblingPath);
        return ReadResxStringEntries(basePath).Keys
            .Where(key => !translated.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            .Select(key => $"  - {Relativize(sourceRoot, siblingPath)} is missing a non-empty '{culture}' translation for key '{key}'");
    }

    // "<stem>.<culture>.resx" still has an extension once ".resx" is stripped (e.g. "ErrorResources.es");
    // a base "<stem>.resx" does not. Base resx stems are kept dot-free by convention.
    private static bool IsCultureSpecificResx(string path) =>
        Path.HasExtension(Path.GetFileNameWithoutExtension(path));

    private static bool IsUnderBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // Reads the string <data> entries (name -> value), skipping non-string resources (images/files carry a
    // type/mimetype attribute) and the resx header/metadata elements.
    private static Dictionary<string, string> ReadResxStringEntries(string path)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            var name = (string?)data.Attribute("name");
            if (name is null || data.Attribute("type") is not null || data.Attribute("mimetype") is not null)
            {
                continue;
            }

            entries[name] = data.Element("value")?.Value ?? string.Empty;
        }

        return entries;
    }

    private static string Relativize(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
