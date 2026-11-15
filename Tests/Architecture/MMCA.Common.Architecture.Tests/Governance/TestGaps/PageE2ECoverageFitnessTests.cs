using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Governance.TestGaps;

/// <summary>
/// Self-test for the page E2E-coverage rule (<see cref="PageE2ECoverageTestsBase"/>), over throwaway
/// trees. It pins the route-template matching (a constrained parameter matches one concrete segment
/// and an interpolation hole, and never a shorter path), that an untested page is reported with its
/// route and file, that the allowlist exempts a route, and that too few routes fails as vacuous.
/// </summary>
public sealed class PageE2ECoverageFitnessTests
{
    private const string OrderPage = "@page \"/orders/{id:int}\"\n<h1>Order</h1>";

    [Theory]
    [InlineData("await Page.GotoAsync(\"/orders/42\");")]
    [InlineData("await Page.GotoAsync($\"/orders/{order.Id}\");")]
    [InlineData("await Page.GotoAsync($\"{BaseUrl}/orders/7?tab=items\");")]
    public void ParameterizedRoute_IsCoveredByAConcreteOrInterpolatedLiteral(string e2eLine)
    {
        using var repo = new TempRepo()
            .Write("Source/Ui/Pages/OrderDetail.razor", OrderPage)
            .Write("Tests/E2E/Orders/OrderTests.cs", e2eLine);

        var act = () => ArchitectureRules.PagesHaveE2ETests(repo.Root, "Source", ["Tests/E2E"], []);

        act.Should().NotThrow("the literal reaches the order route with a concrete or interpolated id");
    }

    [Fact]
    public void ShorterPath_DoesNotCoverAParameterizedRoute()
    {
        using var repo = new TempRepo()
            .Write("Source/Ui/Pages/OrderDetail.razor", OrderPage)
            .Write("Tests/E2E/Orders/OrderListTests.cs", "await Page.GotoAsync(\"/orders\");");

        var act = () => ArchitectureRules.PagesHaveE2ETests(repo.Root, "Source", ["Tests/E2E"], []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("/orders/{id:int}", "the list path never opens an order");
    }

    [Fact]
    public void UntestedPage_IsReportedWithItsRouteAndFile()
    {
        using var repo = new TempRepo()
            .Write("Source/Ui/Pages/OrderDetail.razor", OrderPage)
            .Write("Tests/E2E/Home/HomeTests.cs", "await Page.GotoAsync(\"/\");");

        var act = () => ArchitectureRules.PagesHaveE2ETests(repo.Root, "Source", ["Tests/E2E"], []);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("/orders/{id:int}").And.Contain("OrderDetail.razor");
    }

    [Fact]
    public void AllowlistedRoute_IsNotFlagged()
    {
        using var repo = new TempRepo()
            .Write("Source/Ui/Pages/OrderDetail.razor", OrderPage)
            .Write("Tests/E2E/Home/HomeTests.cs", "await Page.GotoAsync(\"/\");");

        var act = () => ArchitectureRules.PagesHaveE2ETests(repo.Root, "Source", ["Tests/E2E"], ["/orders/{id:int}"]);

        act.Should().NotThrow("an allowlisted route is exempt by its template");
    }

    [Fact]
    public void RouteInventory_CountsEveryPageDirective()
    {
        using var repo = new TempRepo()
            .Write("Source/Ui/Pages/OrderDetail.razor", OrderPage)
            .Write("Source/Ui/Pages/Home.razor", "@page \"/\"\n@page \"/home\"\n<h1>Home</h1>");

        ArchitectureRules.PageRouteCount(repo.Root, "Source").Should().Be(3, "a page can declare several routes and each is a separate entry");
    }

    [Fact]
    public void BaseWithTooFewRoutes_FailsAsVacuous()
    {
        using var repo = new TempRepo().Write("Source/Ui/Pages/Home.razor", "@page \"/\"\n<h1>Home</h1>");
        var sut = new VacuousPageCoverage(repo.Root);

        var act = sut.RouteInventory_ShouldNotBe_Empty;

        act.Should().Throw<XunitException>("one route against a minimum of 50 means the scan missed the pages");
    }

    private sealed class VacuousPageCoverage(string root) : PageE2ECoverageTestsBase
    {
        protected override string RepoRoot => root;

        protected override int MinimumRoutes => 50;
    }
}
