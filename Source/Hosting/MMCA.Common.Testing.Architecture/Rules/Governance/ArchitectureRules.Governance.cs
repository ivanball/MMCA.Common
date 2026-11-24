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
            .SelectMany(a => a.LoadableTypes)
            .Where(HasPiiProperty)
            .Where(t => !t.GetInterfaces().Any(i => string.Equals(i.FullName, AnonymizableFullName, StringComparison.Ordinal)))
            .Select(t => $"  - {t.FullName} declares [Pii] properties and must implement {AnonymizableFullName} (ADR-005)");

        ArchitectureAssert.NoViolations(violations,
            "entities with [Pii]-marked properties must implement IAnonymizable to satisfy the GDPR/CCPA erasure path (ADR-005)");
    }

    /// <summary>Update requests carry no optimistic-concurrency token: the token travels in the <c>If-Match</c> header.</summary>
    public static void UpdateRequestsAreNotConcurrencyAware(IArchitectureMap map)
    {
        var violations = map.ModuleApplication()
            .SelectMany(a => a.LoadableTypes)
            .Where(t => t is { IsClass: true } or { IsValueType: true }
                && t.SimpleName.EndsWith("UpdateRequest", StringComparison.Ordinal))
            .Where(t => t.GetInterfaces().Any(i => string.Equals(i.FullName, ConcurrencyAwareFullName, StringComparison.Ordinal)))
            .Select(t => $"  - {t.FullName} must not implement {ConcurrencyAwareFullName}: the concurrency token is read from the If-Match request header");

        ArchitectureAssert.NoViolations(violations,
            "*UpdateRequest types must not implement IConcurrencyAware: the concurrency token is read from the If-Match request header, and a token in the body would give the same check a second, competing source");
    }

    /// <summary>The pinned major version of a package in Directory.Packages.props is below a ceiling.</summary>
    public static void PinnedPackageMajorBelow(string packageId, int exclusiveMajorCeiling, string reason) =>
        PinnedPackageMajorBelow(FindUpwards("Directory.Packages.props"), packageId, exclusiveMajorCeiling, reason);

    /// <summary>
    /// The pinned major version of a package in the given central package file is below a ceiling.
    /// </summary>
    /// <param name="propsPath">Path of the <c>Directory.Packages.props</c> file to read.</param>
    /// <param name="packageId">The package id, matched ordinally.</param>
    /// <param name="exclusiveMajorCeiling">The first major version that fails the rule.</param>
    /// <param name="reason">Why the ceiling exists, shown on failure.</param>
    public static void PinnedPackageMajorBelow(string propsPath, string packageId, int exclusiveMajorCeiling, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propsPath);

        // Assert on the nullable major directly (no null-forgiving access after NotBeNull — that trips
        // IDE0370 only in CI; see the same pattern in EventVersioningConventionTests).
        var major = ReadPinnedPackageVersion(propsPath, packageId)?.Major;

        major.Should().NotBeNull(because: $"{packageId} must remain pinned in Directory.Packages.props");
        major.Should().BeLessThan(exclusiveMajorCeiling, because: reason);
    }

    /// <summary>
    /// No <c>PackageVersion</c> in the repo's <c>Directory.Packages.props</c> (found by walking up from the
    /// test output folder) has an id starting with <paramref name="packageIdPrefix"/>.
    /// </summary>
    /// <param name="packageIdPrefix">The banned package-id prefix, matched case-insensitively as NuGet ids are.</param>
    /// <param name="reason">Why the packages are banned, shown on failure.</param>
    public static void NoPackageVersionWithPrefix(string packageIdPrefix, string reason) =>
        NoPackageVersionWithPrefix(FindUpwards("Directory.Packages.props"), packageIdPrefix, reason);

    /// <summary>
    /// No <c>PackageVersion</c> in the given central package file has an id starting with
    /// <paramref name="packageIdPrefix"/>.
    /// </summary>
    /// <param name="propsPath">Path of the <c>Directory.Packages.props</c> file to read.</param>
    /// <param name="packageIdPrefix">The banned package-id prefix, matched case-insensitively as NuGet ids are.</param>
    /// <param name="reason">Why the packages are banned, shown on failure.</param>
    public static void NoPackageVersionWithPrefix(string propsPath, string packageIdPrefix, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageIdPrefix);

        var offending = XDocument.Load(propsPath)
            .Descendants("PackageVersion")
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .Where(id => id.StartsWith(packageIdPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        offending.Should().BeEmpty(because: reason);
    }

    private static bool HasPiiProperty(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => p.GetCustomAttributes(inherit: true).Any(a => string.Equals(a.GetType().Name, "PiiAttribute", StringComparison.Ordinal)));

    private static Version? ReadPinnedPackageVersion(string propsPath, string packageId)
    {
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
