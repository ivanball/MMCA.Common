using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace MMCA.Common.Architecture.Tests;

/// <summary>
/// Navigation contract drift gate (rubric §25): the "Routes shipped by the framework" table in
/// <c>NavigationFlow.md</c> must stay in lockstep with the routable pages the
/// <c>MMCA.Common.UI</c> assembly actually ships. Reflection discovers every component carrying a
/// <see cref="RouteAttribute"/>; the embedded document (see the csproj) supplies the promised
/// route list and each route's auth posture. A page added, removed, or re-routed without the doc
/// moving in the same change fails the build, and so does an auth-posture lie in either direction:
/// a route the doc calls Authenticated must carry <see cref="AuthorizeAttribute"/>, and a route the
/// doc calls Anonymous (or Any) must not. A minimum-route floor keeps the reflection non-vacuous.
/// </summary>
public sealed partial class NavigationContractTests
{
    private const int MinimumRoutes = 8;
    private const string DocResource = "NavigationFlow.md";

    [Fact]
    public void RoutablePages_AreDiscovered_GateIsNotVacuous() =>
        DiscoverRoutedPages().Should().HaveCountGreaterThanOrEqualTo(
            MinimumRoutes,
            because: $"MMCA.Common.UI ships at least {MinimumRoutes} routable pages; discovering fewer means the reflection anchor drifted and the navigation drift gate would pass vacuously");

    [Fact]
    public void EveryRoutablePage_IsDocumented_AndEveryDocumentedRoute_Exists()
    {
        var reality = DiscoverRoutedPages();
        var documented = DiscoverDocumentedRoutes();

        var undocumented = reality.Keys.Except(documented.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var phantom = documented.Keys.Except(reality.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        undocumented.Should().BeEmpty(
            because: "every routable page in MMCA.Common.UI must appear in NavigationFlow.md's routes table; add the new route (with its auth posture) in the same change (rubric §25)");
        phantom.Should().BeEmpty(
            because: "NavigationFlow.md promises routes that no longer exist; remove or re-route the stale rows in the same change (rubric §25)");
    }

    [Fact]
    public void EveryDocumentedAuthPosture_MatchesTheRouteAttributeReality()
    {
        var reality = DiscoverRoutedPages();
        var documented = DiscoverDocumentedRoutes();

        var violations = new List<string>();
        foreach (var (route, authCell) in documented)
        {
            if (!reality.TryGetValue(route, out var requiresAuth))
            {
                continue; // the set-equality fact reports phantoms; do not double-report here
            }

            var docSaysAuthenticated = authCell.StartsWith("Authenticated", StringComparison.Ordinal);
            var docSaysOpen = authCell.StartsWith("Anonymous", StringComparison.Ordinal)
                || authCell.StartsWith("Any", StringComparison.Ordinal);

            if (!docSaysAuthenticated && !docSaysOpen)
            {
                violations.Add($"{route}: unrecognized auth posture '{authCell}' in NavigationFlow.md; use 'Anonymous', 'Any', or 'Authenticated...' so the gate can verify it");
            }
            else if (docSaysAuthenticated && !requiresAuth)
            {
                violations.Add($"{route}: NavigationFlow.md promises an authenticated route, but the page carries no [Authorize] attribute, so AuthorizeRouteView will not guard it");
            }
            else if (docSaysOpen && requiresAuth)
            {
                violations.Add($"{route}: NavigationFlow.md documents an open route, but the page carries [Authorize]; update the table's auth posture in the same change");
            }
        }

        violations.Should().BeEmpty(
            because: "the documented auth posture is the §25 route-guard contract; the RouteAttribute/AuthorizeAttribute reality must match it exactly");
    }

    private static Dictionary<string, bool> DiscoverRoutedPages()
    {
        var pages = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var type in typeof(UI.UISharedAssemblyReference).Assembly.GetTypes())
        {
            var routes = type.GetCustomAttributes<RouteAttribute>(inherit: false).ToList();
            if (routes.Count == 0)
            {
                continue;
            }

            var requiresAuth = type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any();
            foreach (var route in routes)
            {
                pages[route.Template] = requiresAuth;
            }
        }

        return pages;
    }

    private static Dictionary<string, string> DiscoverDocumentedRoutes()
    {
        using var stream = typeof(NavigationContractTests).Assembly.GetManifestResourceStream(DocResource)
            ?? throw new InvalidOperationException($"{DocResource} must be embedded as a resource for the navigation drift gate to run");
        using var reader = new StreamReader(stream);
        var doc = reader.ReadToEnd();

        var documented = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match row in RouteRowRegex.Matches(doc))
        {
            documented[row.Groups["route"].Value] = row.Groups["auth"].Value.Trim();
        }

        return documented;
    }

    [GeneratedRegex(@"^\|\s*`(?<route>/[^`]*)`\s*\|[^|]*\|\s*(?<auth>[^|]+)\|", RegexOptions.Multiline | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RouteRowRegex { get; }
}
