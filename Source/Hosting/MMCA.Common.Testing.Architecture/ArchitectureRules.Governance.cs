namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    // Full (namespace-qualified) names of the real framework governance contracts. Matching on the
    // full name (not the simple name) prevents a same-named local interface from satisfying the rule.
    private const string AnonymizableFullName = "MMCA.Common.Domain.Interfaces.IAnonymizable";
    private const string ConcurrencyAwareFullName = "MMCA.Common.Shared.DTOs.IConcurrencyAware";

    /// <summary>Any domain entity with a <c>[Pii]</c>-marked property must implement <c>IAnonymizable</c> (ADR-005).</summary>
    public static void EntitiesWithPiiImplementAnonymizable(IArchitectureMap map)
    {
        var violations = map.OfLayer(Layer.Domain)
            .SelectMany(a => a.GetLoadableTypes())
            .Where(HasPiiProperty)
            .Where(t => !t.GetInterfaces().Any(i => string.Equals(i.FullName, AnonymizableFullName, StringComparison.Ordinal)))
            .Select(t => $"  - {t.FullName} declares [Pii] properties and must implement {AnonymizableFullName} (ADR-005)");

        ArchitectureAssert.NoViolations(violations,
            "entities with [Pii]-marked properties must implement IAnonymizable to satisfy the GDPR/CCPA erasure path (ADR-005)");
    }

    /// <summary>Mutable update requests carry an optimistic-concurrency token (<c>IConcurrencyAware</c>).</summary>
    public static void UpdateRequestsAreConcurrencyAware(IArchitectureMap map)
    {
        var violations = map.ModuleApplication()
            .SelectMany(a => a.GetLoadableTypes())
            .Where(t => t is { IsClass: true } or { IsValueType: true }
                && t.SimpleName().EndsWith("UpdateRequest", StringComparison.Ordinal))
            .Where(t => !t.GetInterfaces().Any(i => string.Equals(i.FullName, ConcurrencyAwareFullName, StringComparison.Ordinal)))
            .Select(t => $"  - {t.FullName} must implement {ConcurrencyAwareFullName} (RowVersion) so concurrent edits surface as 409 Conflict");

        ArchitectureAssert.NoViolations(violations,
            "*UpdateRequest types must implement IConcurrencyAware so optimistic-concurrency conflicts surface as 409 rather than last-write-wins");
    }

    /// <summary>The pinned major version of a package in Directory.Packages.props is below a ceiling.</summary>
    public static void PinnedPackageMajorBelow(string packageId, int exclusiveMajorCeiling, string reason)
    {
        // Assert on the nullable major directly (no null-forgiving access after NotBeNull — that trips
        // IDE0370 only in CI; see the same pattern in EventVersioningConventionTests).
        var major = ReadPinnedPackageVersion(packageId)?.Major;

        major.Should().NotBeNull(because: $"{packageId} must remain pinned in Directory.Packages.props");
        major.Should().BeLessThan(exclusiveMajorCeiling, because: reason);
    }

    private static bool HasPiiProperty(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.GetCustomAttributes(inherit: true).Any(a => string.Equals(a.GetType().Name, "PiiAttribute", StringComparison.Ordinal)));

    private static Version? ReadPinnedPackageVersion(string packageId)
    {
        var propsPath = FindUpwards("Directory.Packages.props");
        var raw = XDocument.Load(propsPath)
            .Descendants("PackageVersion")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.Ordinal))
            ?.Attribute("Version")?.Value;

        return raw is null ? null : Version.Parse(raw);
    }

    private static string FindUpwards(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {fileName} by walking up from {AppContext.BaseDirectory}.");
    }
}
