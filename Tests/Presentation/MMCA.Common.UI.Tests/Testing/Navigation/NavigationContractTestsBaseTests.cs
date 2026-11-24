using AwesomeAssertions;
using MMCA.Common.Testing.UI.Navigation;

namespace MMCA.Common.UI.Tests.Testing.Navigation;

/// <summary>
/// The shipped navigation contract gate, run against a throwaway application tree: it passes on a
/// tree whose document and pages agree, and each kind of drift (an undocumented page, a phantom row,
/// a guard that moved, a page with no guard, a missing document, a malformed row, a scan that found
/// too little) fails the fact that owns it. A gate that cannot fail here would pass vacuously in every
/// consumer.
/// </summary>
public sealed class NavigationContractTestsBaseTests : IDisposable
{
    private readonly NavigationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ACleanTree_PassesEveryFact()
    {
        var gate = new FixtureContract(_fixture);

        RunAll(gate).Should().BeEmpty("the document and the pages agree");
    }

    [Fact]
    public void DiscoverPageRoutes_ClassifiesEveryContractForm_AndSkipsBuildOutput()
    {
        var pages = new FixtureContract(_fixture).DiscoverPageRoutes();

        pages.Select(page => (page.Route, page.Access)).Should().BeEquivalentTo(
        [
            ("/app-home", (string?)NavigationContractTestsBase.AnonymousAccess),
            ("/dashboard", NavigationContractTestsBase.AuthenticatedAccess),
            ("/admin", FixtureRoleNames.Admin),
            ("/reports/{Id:int}", NavigationContractTestsBase.PermissionAccessPrefix + FixturePermissions.ReportsView),
        ]);
        pages.Single(page => page.Route == "/admin").RelativePath.Should().Be(
            "Source/App.UI/Pages/AdminPanel.razor",
            "the guard read from the code-behind is reported against the page that declares the route");
    }

    [Fact]
    public void AnUndocumentedPage_FailsTheSetEqualityFact()
    {
        _fixture.WritePage("Late", "@page \"/late\"\n@attribute [Authorize]\n");
        var gate = new FixtureContract(_fixture);

        var act = gate.EveryRoutablePage_IsInTheRouteContract_AndEveryContractRoute_Exists;

        act.Should().Throw<Exception>().WithMessage("*undocumented: /late*");
    }

    [Fact]
    public void APhantomRow_FailsTheSetEqualityFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace(
            "| `/admin` |",
            "| `/gone` | - | Anonymous |\n| `/admin` |",
            StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.EveryRoutablePage_IsInTheRouteContract_AndEveryContractRoute_Exists;

        act.Should().Throw<Exception>().WithMessage("*phantom: /gone*");
    }

    [Fact]
    public void AGuardThatMoved_FailsTheAccessFact()
    {
        _fixture.WritePage("Dashboard", "@page \"/dashboard\"\n@attribute [AllowAnonymous]\n");
        var gate = new FixtureContract(_fixture);

        var act = gate.EveryContractAccess_MatchesThePageGuard;

        act.Should().Throw<Exception>().WithMessage("*/dashboard: doc says Authenticated*declares Anonymous*");
    }

    [Fact]
    public void APageWithNoGuard_FailsTheAttributeFact()
    {
        _fixture.WritePage("Dashboard", "@page \"/dashboard\"\n<h1>Dashboard</h1>\n");
        var gate = new FixtureContract(_fixture);

        var act = gate.EveryRoutablePage_DeclaresARecognizedAccessAttribute;

        act.Should().Throw<Exception>().WithMessage("*/dashboard*declares no access attribute*");
    }

    [Fact]
    public void ARoleOutsideThePageRoles_IsNotAContractForm()
    {
        _fixture.WritePage("Dashboard", "@page \"/dashboard\"\n@attribute [Authorize(Roles = FixtureRoleNames.Member)]\n");
        var gate = new FixtureContract(_fixture, pageRoles: [FixtureRoleNames.Admin]);

        var act = gate.EveryRoutablePage_DeclaresARecognizedAccessAttribute;

        act.Should().Throw<Exception>().WithMessage("*/dashboard*not one of the contract forms*");
    }

    [Fact]
    public void ThePermissionForm_IsOffByDefault()
    {
        var gate = new FixtureContract(_fixture, permissions: false);

        RunAll(gate).Should().Contain(
            failure => failure.Contains("/reports/{Id:int}", StringComparison.Ordinal),
            "without PermissionConstantsType a [HasPagePermission] page and a 'Permission:' row are not contract forms");
        gate.DiscoverPageRoutes().Single(page => page.Route == "/reports/{Id:int}").Access.Should().BeNull();
    }

    [Fact]
    public void AnUnknownPermissionInTheDocument_FailsTheTableFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace(
            "Permission: reports:view",
            "Permission: reports:edit",
            StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*Access 'Permission: reports:edit' is not one of*");
    }

    [Fact]
    public void AMalformedRow_FailsTheTableFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace(
            "| `/dashboard` | Dashboard | Authenticated |",
            "| /dashboard | Dashboard | Authenticated |",
            StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*malformed row*/dashboard*");
    }

    [Fact]
    public void AMissingRouteContractHeading_FailsTheTableFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace("## Route Contract", "## Routes", StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*missing '## Route Contract' section*");
    }

    [Fact]
    public void ARouteContractSectionWithoutATable_FailsTheTableFact()
    {
        _fixture.WriteContract("# Fixture navigation\n\n## Route Contract\n\nThe table moved.\n\n## Something else\n\n| Route | Page | Access |\n| --- | --- | --- |\n| `/app-home` | AppHome | Anonymous |\n");
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>("a table under the next heading is not the contract").WithMessage("*section has no*Route*Page*Access*table*");
    }

    [Fact]
    public void ATableWithTheWrongHeader_FailsTheTableFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace("| Route | Page | Access |\n| --- | --- | --- |\n| `/app-home`", "| Path | Page | Access |\n| --- | --- | --- |\n| `/app-home`", StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*section has no*table*");
    }

    [Fact]
    public void AnEmptyContractTable_FailsTheTableFact()
    {
        _fixture.WriteContract("# Fixture navigation\n\n## Route Contract\n\n| Route | Page | Access |\n| --- | --- | --- |\n");
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*the Route Contract table is empty*");
    }

    [Fact]
    public void ARowWithTooFewCells_FailsTheTableFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace(
            "| `/dashboard` | Dashboard | Authenticated |",
            "| `/dashboard` | Authenticated |",
            StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*malformed row*/dashboard*expected three cells*");
    }

    [Fact]
    public void ADuplicateRow_FailsTheTableFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace(
            "| `/admin` | AdminPanel | Admin |",
            "| `/admin` | AdminPanel | Admin |\n| `/admin` | AdminPanel | Admin |",
            StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*/admin: 2 rows; keep exactly one row per route*");
    }

    [Fact]
    public void APageCellNamingAnotherPage_FailsTheTableFact()
    {
        _fixture.WriteContract(NavigationFixture.CleanContract.Replace(
            "| `/admin` | AdminPanel | Admin |",
            "| `/admin` | Dashboard | Admin |",
            StringComparison.Ordinal));
        var gate = new FixtureContract(_fixture);

        var act = gate.RouteContractTable_IsWellFormed;

        act.Should().Throw<Exception>().WithMessage("*/admin: Page 'Dashboard' does not match the page that declares it (AdminPanel*");
    }

    [Fact]
    public void AMissingDocument_Fails_RatherThanSkipping()
    {
        _fixture.DeleteContract();
        var gate = new FixtureContract(_fixture);

        var act = gate.EveryRoutablePage_IsInTheRouteContract_AndEveryContractRoute_Exists;

        act.Should().Throw<Exception>().WithMessage($"*{gate.EnvironmentVariable}*");
    }

    [Fact]
    public void TheEnvironmentVariable_OverridesTheDefaultPath()
    {
        var gate = new FixtureContract(_fixture);
        var elsewhere = Path.Combine(_fixture.Root, "downloaded", "published.md");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, NavigationFixture.CleanContract);
        _fixture.DeleteContract();

        Environment.SetEnvironmentVariable(gate.EnvironmentVariable, elsewhere);
        try
        {
            RunAll(gate).Should().BeEmpty("the document is read from the path the variable names");
        }
        finally
        {
            Environment.SetEnvironmentVariable(gate.EnvironmentVariable, null);
        }
    }

    [Fact]
    public void AScanThatFindsTooLittle_FailsTheNonVacuityFact()
    {
        var gate = new FixtureContract(_fixture, minimumRoutes: 5);

        var act = gate.RoutablePages_AreDiscovered_GateIsNotVacuous;

        act.Should().Throw<Exception>().WithMessage("*at least 5 routable pages*");
    }

    private static List<string> RunAll(NavigationContractTestsBase gate)
    {
        Action[] facts =
        [
            gate.RoutablePages_AreDiscovered_GateIsNotVacuous,
            gate.EveryRoutablePage_DeclaresARecognizedAccessAttribute,
            gate.EveryRoutablePage_IsInTheRouteContract_AndEveryContractRoute_Exists,
            gate.EveryContractAccess_MatchesThePageGuard,
            gate.RouteContractTable_IsWellFormed,
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

    /// <summary>The gate as a consumer subclasses it, pointed at the fixture tree.</summary>
    private sealed class FixtureContract(
        NavigationFixture fixture,
        bool permissions = true,
        IReadOnlyCollection<string>? pageRoles = null,
        int minimumRoutes = 4) : NavigationContractTestsBase
    {
        // Unique per instance and never set by anything else, so the default path is what is read
        // unless a test sets it.
        public string EnvironmentVariable { get; } = "MMCA_TEST_NAVIGATION_DOC_" + Guid.NewGuid().ToString("N");

        protected override string RepoRoot => fixture.RepoRoot;

        protected override string DocFileName => NavigationFixture.DocFileName;

        protected override string DocEnvironmentVariable => EnvironmentVariable;

        protected override int MinimumRoutes => minimumRoutes;

        protected override Type RoleNamesType => typeof(FixtureRoleNames);

        protected override IReadOnlyCollection<string> PageRoles => pageRoles ?? base.PageRoles;

        protected override Type? PermissionConstantsType => permissions ? typeof(FixturePermissions) : null;
    }
}
