namespace MMCA.Common.Testing.UI.Navigation;

/// <summary>
/// One routable page as <see cref="NavigationContractTestsBase.DiscoverPageRoutes"/> reads it from
/// source: one entry per <c>@page</c> directive.
/// </summary>
/// <param name="Route">The route template, verbatim from the <c>@page</c> directive.</param>
/// <param name="RelativePath">The declaring <c>.razor</c> file, relative to the repo root, forward slashes.</param>
/// <param name="PageName">The declaring <c>.razor</c> file name without its extension.</param>
/// <param name="Access">
/// The page's access word (<see cref="NavigationContractTestsBase.AnonymousAccess"/>,
/// <see cref="NavigationContractTestsBase.AuthenticatedAccess"/>, a page role, or
/// <see cref="NavigationContractTestsBase.PermissionAccessPrefix"/> plus a permission value), or
/// <see langword="null"/> when the guard is missing or not one of the contract forms.
/// </param>
/// <param name="Violation">Why <paramref name="Access"/> is <see langword="null"/>; otherwise <see langword="null"/>.</param>
public sealed record NavigationPage(string Route, string RelativePath, string PageName, string? Access, string? Violation);
