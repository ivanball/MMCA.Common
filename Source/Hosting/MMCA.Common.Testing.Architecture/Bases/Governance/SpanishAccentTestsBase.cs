using System.Text.RegularExpressions;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Spanish-localization fitness function: no Spanish resource string ships a common word with its
/// required accent or n-tilde missing (<c>codigo</c> for the word with the accented o, <c>sesion</c>,
/// <c>contrasena</c> for the word with the n-tilde, and so on). Those strings compile, render and pass
/// every functional test, so without this gate they are found by a Spanish-speaking user.
/// Authored once here and re-run as a thin subclass in each repo, which supplies the
/// <see cref="ResourceRoot"/> to scan and, when it has deliberate exceptions, its
/// <see cref="AllowedEntries"/>.
/// <para>
/// Every <c>*.es.resx</c> under the root is read, and only the text of each string
/// <c>&lt;data&gt;&lt;value&gt;</c> is checked (keys and comments are not). A word matches only as a
/// whole word, case-insensitively, so the correctly accented form never matches: an accented letter
/// is a different letter, and a longer word (a plural, a derived form) is a different word.
/// </para>
/// <para>
/// The list holds words whose unaccented spelling is almost always the mistake in UI text. Words that
/// are equally correct with and without the accent depending on meaning (<c>esta</c> as "this"
/// against the verb form, <c>solo</c>, which no longer takes an accent) are deliberately left out, and
/// plurals that drop the accent by rule (<c>sesiones</c>, <c>aplicaciones</c>) never match.
/// </para>
/// </summary>
public abstract class SpanishAccentTestsBase
{
    /// <summary>
    /// The unaccented spellings checked by default. Each stands for a word that requires an accent or
    /// an n-tilde in the sense UI text uses it.
    /// </summary>
    private static readonly string[] DefaultUnaccentedWords =
    [
        "sesion", "codigo", "codigos", "contrasena", "contrasenas", "numero", "numeros", "maximo",
        "minimo", "titulo", "direccion", "informacion", "posicion", "clasificacion", "electronico",
        "electronica", "pagina", "paginas", "aqui", "mas", "todavia", "aun", "estan", "publico",
        "ningun", "podra", "cerrara", "volvera", "encontro", "visito", "tenia", "aplicacion",
        "limite", "puntuacion", "perdio", "confirmo",
    ];

    /// <summary>
    /// The folder to scan recursively for <c>*.es.resx</c> files, normally the repo's <c>Source</c>
    /// folder under <c>ArchitectureMapBase.FindRepoRoot("&lt;Repo&gt;.slnx")</c>.
    /// </summary>
    protected abstract string ResourceRoot { get; }

    /// <summary>
    /// The unaccented spellings to reject. Defaults to the framework list; a subclass extends it with
    /// <c>[.. base.UnaccentedWords, "extra"]</c> for domain vocabulary of its own.
    /// </summary>
    protected virtual IReadOnlyCollection<string> UnaccentedWords => DefaultUnaccentedWords;

    /// <summary>
    /// Intentional exceptions, each written exactly as the failure lists it:
    /// <c>{path relative to the resource root, forward slashes}:{resource key}</c>. Keep it short and
    /// say why next to each entry; an exception is a decision, not a way to make the gate pass.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedEntries => [];

    /// <summary>
    /// The fewest <c>*.es.resx</c> files the scan must find. A wrong root that finds none would
    /// otherwise pass with nothing checked.
    /// </summary>
    protected virtual int MinimumResourceFileCount => 1;

    [Fact]
    public void Spanish_resources_keep_their_accents()
    {
        ResourceRoot.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(ResourceRoot).Should().BeTrue($"the resource root '{ResourceRoot}' must exist");

        var files = Directory
            .EnumerateFiles(ResourceRoot, "*.es.resx", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(ResourceRoot, path)))
            .Order(StringComparer.Ordinal)
            .ToList();

        files.Count.Should().BeGreaterThanOrEqualTo(
            MinimumResourceFileCount,
            $"the scan must find Spanish resources under '{ResourceRoot}', or it checks nothing");

        var pattern = @"\b(?:" + string.Join('|', UnaccentedWords.Select(Regex.Escape)) + @")\b";
        var unaccented = new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        var allowed = AllowedEntries.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(ResourceRoot, file).Replace('\\', '/');

            foreach (var data in XDocument.Load(file).Root?.Elements("data") ?? [])
            {
                // Non-string entries (file references, serialized objects) carry no UI text.
                if (data.Attribute("type") is not null || data.Attribute("mimetype") is not null)
                {
                    continue;
                }

                var key = (string?)data.Attribute("name") ?? string.Empty;
                var value = (string?)data.Element("value") ?? string.Empty;
                var entry = relative + ":" + key;
                if (allowed.Contains(entry))
                {
                    continue;
                }

                var words = unaccented.Matches(value)
                    .Select(static match => match.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (words.Count > 0)
                {
                    offenders.Add($"  - {entry}: {string.Join(", ", words)}");
                }
            }
        }

        ArchitectureAssert.NoViolations(
            offenders,
            "Spanish resource strings must carry their accents and n-tildes; fix the value, or add "
                + "'file:key' to AllowedEntries when the unaccented spelling is intended");
    }

    /// <summary>True when the path runs through build output or a tool-owned tree.</summary>
    private static bool IsBuildOutput(string relativePath) =>
        relativePath
            .Replace('\\', '/')
            .Split('/')
            .Any(static segment => segment is "bin" or "obj" or "node_modules" or ".git");
}
