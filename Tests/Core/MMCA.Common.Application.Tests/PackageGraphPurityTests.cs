using System.Xml.Linq;
using AwesomeAssertions;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Package-graph purity gate for MMCA.Common.Application.
/// <para>
/// The IL-based architecture rules can only see what the code <b>uses</b>; they are blind to what the
/// package <b>drags in</b>. A single PackageReference is enough to put ASP.NET MVC or EF Core into
/// the dependency graph of every consumer of this host-agnostic package, and nothing else in the
/// build would notice. This test reads the csproj itself and fails on that.
/// </para>
/// </summary>
public sealed class PackageGraphPurityTests
{
    /// <summary>
    /// Package-id prefixes that must never appear as a direct reference of the Application package.
    /// Web hosting belongs to MMCA.Common.API, persistence to MMCA.Common.Infrastructure.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.AspNetCore.",
        "MiniProfiler.AspNetCore",
        "Microsoft.EntityFrameworkCore",
    ];

    /// <summary>
    /// The complete set of direct package references the Application package is allowed to have.
    /// Adding an entry here is a deliberate decision about what every consumer of the framework
    /// inherits, so each one carries its reason:
    /// <list type="bullet">
    /// <item><c>FluentValidation.DependencyInjectionExtensions</c>: the validators the pipeline resolves.</item>
    /// <item><c>Microsoft.Extensions.Configuration.Json</c>: ModuleLoader reads each module's
    /// modules.{name}.json. Host-agnostic; it used to arrive transitively through the MiniProfiler
    /// ASP.NET Core graph, which is precisely the kind of accident this test exists to prevent.</item>
    /// <item><c>Microsoft.FeatureManagement</c>: the abstractions-only feature-flag package the feature
    /// gate decorators read (the <c>.AspNetCore</c> variant belongs to MMCA.Common.API).</item>
    /// <item><c>MiniProfiler.Shared</c>: profiling primitives only. Deliberately NOT
    /// <c>MiniProfiler.AspNetCore.Mvc</c>, which the profiling decorators never touched and which
    /// pulled MVC into every consumer of this package.</item>
    /// <item><c>Riok.Mapperly</c>: source generator, PrivateAssets=all, no runtime dependency.</item>
    /// <item><c>MinVer</c>: build-time versioning, PrivateAssets=all.</item>
    /// <item><c>Scrutor</c>: the assembly scanning and decoration this layer's registration is built on.</item>
    /// <item><c>System.Linq.Dynamic.Core</c>: KEPT DELIBERATELY. It is the parser behind the whole
    /// entity query pipeline (QueryFieldService plus every strategy under Services/Filtering), which
    /// is Application-layer logic, not infrastructure. Hiding it behind an Application-owned
    /// interface implemented in Infrastructure would move the pipeline itself across a layer boundary
    /// for no purity gain: the package is host-agnostic, brings no ASP.NET or EF Core dependency, and
    /// targets netstandard. Allowlisted rather than relocated.</item>
    /// </list>
    /// </summary>
    private static readonly string[] AllowedPackageReferences =
    [
        "FluentValidation.DependencyInjectionExtensions",
        "Microsoft.Extensions.Configuration.Json",
        "Microsoft.FeatureManagement",
        "MinVer",
        "MiniProfiler.Shared",
        "Riok.Mapperly",
        "Scrutor",
        "System.Linq.Dynamic.Core",
    ];

    [Fact]
    public void ApplicationPackage_HasNoWebOrPersistenceDependency()
    {
        var references = ReadDirectPackageReferences();

        var offenders = references
            .Where(id => ForbiddenPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offenders.Should().BeEmpty(
            "MMCA.Common.Application is host-agnostic: ASP.NET Core / MVC and EF Core belong to MMCA.Common.API and MMCA.Common.Infrastructure");
    }

    [Fact]
    public void ApplicationPackage_DirectReferences_AreAllAccountedFor()
    {
        var references = ReadDirectPackageReferences();

        references.Should().BeSubsetOf(
            AllowedPackageReferences,
            "every direct dependency of the Application package is inherited by every consumer of the framework, so a new one is a deliberate decision recorded in AllowedPackageReferences");
    }

    [Fact]
    public void ApplicationPackage_StillReferencesEveryAllowedPackage()
    {
        var references = ReadDirectPackageReferences();

        // Catches the other direction: a package silently dropped from the csproj leaves a stale
        // allowlist entry that would then admit its return unnoticed.
        AllowedPackageReferences.Should().BeSubsetOf(references);
    }

    private static IReadOnlyList<string> ReadDirectPackageReferences()
    {
        var csprojPath = Path.Combine(
            FindRepositoryRoot(),
            "Source",
            "Core",
            "MMCA.Common.Application",
            "MMCA.Common.Application.csproj");

        File.Exists(csprojPath).Should().BeTrue($"the Application csproj must be readable at '{csprojPath}'");

        return [.. XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MMCA.Common.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (no MMCA.Common.slnx above '{AppContext.BaseDirectory}').");
    }
}
