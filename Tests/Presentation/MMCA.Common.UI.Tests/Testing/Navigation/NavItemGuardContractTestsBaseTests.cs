using AwesomeAssertions;
using MMCA.Common.Testing.UI.Navigation;
using MMCA.Common.UI.Common;

namespace MMCA.Common.UI.Tests.Testing.Navigation;

/// <summary>
/// The shipped menu-to-page guard gate, run against a throwaway application tree: it passes when
/// every nav item requires at least what its page enforces, and fails on an item looser than its
/// page (anonymous to a signed-in page, missing the page's role, missing its permission), on a dead
/// link, on an entry the source reader cannot resolve, and when there is nothing to read. It also
/// proves both module paths: source text and, for an assembly next to the tests, reflection.
/// </summary>
[Collection(nameof(ReflectedFixtureUIModule))]
public sealed class NavItemGuardContractTestsBaseTests : IDisposable
{
    private readonly NavigationFixture _fixture = new();

    public void Dispose()
    {
        ReflectedFixtureUIModule.Items = [];
        _fixture.Dispose();
    }

    [Fact]
    public void ACleanTree_PassesEveryFact() =>
        RunAll(new FixtureGuard(_fixture)).Should().BeEmpty("every item requires what its page enforces");

    [Fact]
    public void AnItemMissingThePageRole_Fails()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace(
            ", RequiredRole: FixtureRoleNames.Admin",
            ", Section: NavSection.Admin",
            StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>().WithMessage("*Admin -> /admin*requires one of the roles [Admin]*");
    }

    [Fact]
    public void AnAnonymousItemToASignedInPage_Fails()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace(
            ", Section: NavSection.User",
            string.Empty,
            StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>().WithMessage("*Dashboard -> /dashboard*shown to anonymous visitors*");
    }

    [Fact]
    public void AnItemMissingThePagePermission_Fails()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace(
            "RequiredPermission: \"reports:view\", ",
            string.Empty,
            StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>().WithMessage("*Report -> /reports/1*requires the permission 'reports:view'*");
    }

    [Fact]
    public void AnItemRequiringAnotherRole_Fails()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace(
            "RequiredRole: FixtureRoleNames.Admin",
            "RequiredRole: FixtureRoleNames.Member",
            StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>().WithMessage("*Admin -> /admin*requires one of the roles [Admin] but the item requires role 'Member'*");
    }

    [Fact]
    public void AnItemRequiringAnotherPermission_Fails()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace(
            "RequiredPermission: \"reports:view\"",
            "RequiredPermission: \"reports:edit\"",
            StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>().WithMessage("*Report -> /reports/1*requires the permission 'reports:view' but the item requires permission 'reports:edit'*");
    }

    [Fact]
    public void AnItemToAPageWhoseGuardCannotBeRead_Fails()
    {
        _fixture.WritePage("Dashboard", "@page \"/dashboard\"\n<h1>Dashboard</h1>\n");
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>().WithMessage("*Dashboard -> /dashboard*the page guard could not be read (declares no access attribute*");
    }

    [Fact]
    public void ADeadLink_Fails()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace("\"/dashboard\"", "\"/nowhere\"", StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_OpensARoutablePage;

        act.Should().Throw<Exception>().WithMessage("*/nowhere: no routable page*");
    }

    [Fact]
    public void ARouteConstraint_IsHonored()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace("\"/reports/1\"", "\"/reports/latest\"", StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_OpensARoutablePage;

        act.Should().Throw<Exception>(
            "'latest' is not an int, so the int-constrained reports route is not the page the item opens").WithMessage("*/reports/latest*");
    }

    [Fact]
    public void AnEntryTheSourceReaderCannotResolve_FailsTheNonVacuityFact()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace(
            "RequiredRole: FixtureRoleNames.Admin",
            "RequiredRole: SomewhereElse.Admin",
            StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.NavItems_AreReadFromEveryUIModule_GateIsNotVacuous;

        act.Should().Throw<Exception>().WithMessage("*cannot read nav item*SomewhereElse.Admin*");
    }

    [Fact]
    public void NoModule_FailsTheNonVacuityFact()
    {
        File.Delete(Path.Combine(_fixture.RepoRoot, "Source", "App.UI", "AppUIModule.cs"));
        var gate = new FixtureGuard(_fixture);

        var act = gate.NavItems_AreReadFromEveryUIModule_GateIsNotVacuous;

        act.Should().Throw<Exception>().WithMessage("*no IUIModule implementation was found*");
    }

    [Fact]
    public void AModuleWhoseAssemblyIsPresent_IsReadByReflection()
    {
        _fixture.AddReflectedModule();
        ReflectedFixtureUIModule.Items =
        [
            new NavItem("Reflected admin", "/admin", string.Empty, typeof(NavItemGuardContractTestsBaseTests)),
        ];
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>(
            "the type's own NavItems are compared, not the clean text beside it").WithMessage("*Reflected admin -> /admin*");
    }

    [Fact]
    public void FrameworkPages_AreComparedWithTheirRegisteredGuards()
    {
        _fixture.WriteModule(NavigationFixture.CleanModule.Replace(
            "new(\"Home\", \"/app-home\", Icons.Home, typeof(Strings)),",
            "new(\"Home\", \"/app-home\", Icons.Home, typeof(Strings)),\n        new(\"Sessions\", \"/profile/sessions\", Icons.Lock, typeof(Strings)),",
            StringComparison.Ordinal));
        var gate = new FixtureGuard(_fixture);

        var act = gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens;

        act.Should().Throw<Exception>(
            "the framework's /profile/sessions page carries [Authorize], so an anonymous item to it is looser than its page")
            .WithMessage("*Sessions -> /profile/sessions*shown to anonymous visitors*");
    }

    private static List<string> RunAll(NavItemGuardContractTestsBase gate)
    {
        Action[] facts =
        [
            gate.NavItems_AreReadFromEveryUIModule_GateIsNotVacuous,
            gate.EveryNavItem_OpensARoutablePage,
            gate.EveryNavItem_IsNoLooserThanTheGuardOfThePageItOpens,
        ];

        var failures = new List<string>();
        foreach (var fact in facts)
        {
            try
            {
                fact();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures.Add(ex.Message);
            }
        }

        return failures;
    }

    /// <summary>The navigation contract the guard gate reads its pages from.</summary>
    private sealed class FixtureContract(NavigationFixture fixture) : NavigationContractTestsBase
    {
        protected override string RepoRoot => fixture.RepoRoot;

        protected override string DocFileName => NavigationFixture.DocFileName;

        protected override string DocEnvironmentVariable => "MMCA_TEST_NAVIGATION_DOC_UNSET";

        protected override int MinimumRoutes => 4;

        protected override Type RoleNamesType => typeof(FixtureRoleNames);

        protected override Type? PermissionConstantsType => typeof(FixturePermissions);
    }

    /// <summary>The guard gate as a consumer subclasses it, pointed at the fixture tree.</summary>
    private sealed class FixtureGuard(NavigationFixture fixture) : NavItemGuardContractTestsBase
    {
        protected override string RepoRoot => fixture.RepoRoot;

        protected override NavigationContractTestsBase NavigationContract { get; } = new FixtureContract(fixture);

        protected override Type RoleNamesType => typeof(FixtureRoleNames);
    }
}
