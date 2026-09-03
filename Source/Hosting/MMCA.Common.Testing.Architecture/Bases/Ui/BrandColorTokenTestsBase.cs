namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Brand-token drift fitness function (rubric §20): landing-page scoped CSS must source the primary
/// brand color from the shared <c>--mmca-primary</c> custom property (defined once in MMCA.Common's
/// <c>app.css</c> from <c>BrandColors.Primary</c>) rather than re-hardcoding the hex, so per-host copies
/// cannot silently drift from each other or from the framework token. Authored once here and re-run as a
/// thin subclass in each repo: the subclass embeds its landing-page stylesheets as manifest resources
/// (see its csproj) and lists their logical names in <see cref="EmbeddedCssLogicalNames"/>; a
/// re-introduced literal then fails the build. MMCA.Common's own <c>BrandColorTokenTests</c> guards the
/// C#-to-CSS token definition; this base guards the consumers of it.
/// </summary>
public abstract class BrandColorTokenTestsBase
{
    private const string BrandPrimaryHex = "#1565C0";
    private const string BrandToken = "var(--mmca-primary)";

    /// <summary>
    /// The logical manifest-resource names of the landing-page scoped stylesheets embedded in the
    /// subclass's test assembly (e.g. <c>["StoreHome.Server.razor.css", "StoreHome.Client.razor.css"]</c>).
    /// </summary>
    protected abstract IReadOnlyList<string> EmbeddedCssLogicalNames { get; }

    [Fact]
    public void LandingPageCss_SourcesBrandColorFromToken_NotHardcodedHex()
    {
        EmbeddedCssLogicalNames.Should().NotBeEmpty(
            "at least one landing-page stylesheet must be embedded and listed, so the brand-token drift guard is actually verified");

        var violations = new List<string>();
        foreach (var logicalName in EmbeddedCssLogicalNames)
        {
            var css = ReadEmbeddedCss(GetType().Assembly, logicalName);

            if (string.IsNullOrWhiteSpace(css))
            {
                violations.Add($"{logicalName} must be a non-empty stylesheet");
                continue;
            }

            if (!css.Contains(BrandToken, StringComparison.Ordinal))
            {
                violations.Add($"{logicalName} must source the primary brand color from {BrandToken} (single source of truth)");
            }

            if (css.Contains(BrandPrimaryHex, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{logicalName} must not re-hardcode the primary brand hex {BrandPrimaryHex}; use {BrandToken}");
            }
        }

        violations.Should().BeEmpty(
            because: "landing-page CSS must consume the shared brand token so host copies cannot drift from the framework palette (rubric §20)");
    }

    private static string ReadEmbeddedCss(Assembly assembly, string logicalName)
    {
        using var stream = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"{logicalName} must be embedded as a resource for the brand-token drift guard to run");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
