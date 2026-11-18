using MMCA.Common.Architecture.Tests.Governance.TestGaps;
using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Ui.Accessibility;

/// <summary>
/// Self-test for <see cref="ArchitectureRules.RoutablePagesRenderExactlyOneH1"/>, the rule behind
/// <see cref="PageHeadingTestsBase"/>. The detector cases cover the shapes both reference apps ship:
/// an inline page title, a page titled by a framework component, and a page whose title sits in
/// exclusive loading and loaded branches.
/// </summary>
public sealed class PageHeadingFitnessTests
{
    private const string Title = "<MudText Typo=\"Typo.h4\" HtmlTag=\"h1\">Title</MudText>";

    private static readonly IReadOnlyCollection<string> Components = PageHeadingTestsBase.FrameworkHeadingComponents;

    [Theory]
    [InlineData("@page \"/a\"\n" + Title)]
    [InlineData("@page \"/a\"\n<h1 class=\"hero\">Title</h1>")]
    [InlineData("@page \"/a\"\n<PageHeader Title=\"@Title\" />")]
    [InlineData("@page \"/a\"\n<ListPageShell\n    Title=\"x\">\n</ListPageShell>")]
    [InlineData("@page \"/a\"\n@* HtmlTag=\"h1\" in a comment does not count *@\n" + Title)]
    [InlineData("@page \"/a\"\n<!-- <h1>old</h1> -->\n" + Title)]
    public void Detector_AcceptsExactlyOneH1(string razor) =>
        ArchitectureRules.PageHeadingViolation("Page.razor", razor, Components).Should().BeNull();

    [Theory]
    [InlineData("@page \"/a\"\n<MudText Typo=\"Typo.h4\">Title</MudText>", "0 own h1")]
    [InlineData("@page \"/a\"\n" + Title + "\n" + Title, "2 own h1")]
    [InlineData("@page \"/a\"\n<PageHeader Title=\"x\" />\n" + Title, "<PageHeader>")]
    [InlineData("@page \"/a\"\n<PageHeaderBand />", "0 own h1")]
    public void Detector_FlagsMissingOrSecondH1(string razor, string expected) =>
        ArchitectureRules.PageHeadingViolation("Page.razor", razor, Components)
            .Should().NotBeNull().And.Contain(expected);

    [Fact]
    public void Detector_HonorsAReviewedBranchedCount() =>
        ArchitectureRules.PageHeadingViolation("Page.razor", "@page \"/a\"\n" + Title + "\n" + Title, Components, expectedOwnHeadings: 2)
            .Should().BeNull();

    [Theory]
    [InlineData("@page \"/a\"\n<p />", true)]
    [InlineData("﻿@page \"/a\"\n<p />", true)]
    [InlineData("@using X\n@page \"/a\"", true)]
    [InlineData("<p>@page is text</p>", false)]
    public void IsRoutableRazorPage_ReadsTheDirective(string razor, bool expected) =>
        ArchitectureRules.IsRoutableRazorPage(razor).Should().Be(expected);

    [Fact]
    public void Rule_PassesACompliantTree_AndSkipsComponents()
    {
        using var repo = new TempRepo()
            .Write("Ui/Pages/Inline.razor", "@page \"/inline\"\n" + Title)
            .Write("Ui/Pages/List.razor", "@page \"/list\"\n<ListPageShell Title=\"x\" />")
            .Write("Ui/Pages/Detail.razor", "@page \"/detail\"\n@if (x)\n{\n" + Title + "\n}\nelse\n{\n" + Title + "\n}")
            .Write("Ui/Pages/Redirect.razor", "@page \"/redirect\"\n<MudProgressCircular />")
            .Write("Ui/Components/Card.razor", "<MudCard />");

        var act = () => ArchitectureRules.RoutablePagesRenderExactlyOneH1(
            [repo.Root],
            minimumPages: 4,
            Components,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["Ui/Pages/Detail.razor"] = 2 },
            ["Ui/Pages/Redirect.razor"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rule_FlagsStaleExemptionsAndAShortScan()
    {
        using var repo = new TempRepo()
            .Write("Ui/Pages/Redirect.razor", "@page \"/redirect\"\n<PageHeader Title=\"now titled\" />");

        var act = () => ArchitectureRules.RoutablePagesRenderExactlyOneH1(
            [repo.Root],
            minimumPages: 2,
            Components,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["Ui/Pages/Gone.razor"] = 2 },
            ["Ui/Pages/Redirect.razor"]);

        act.Should().Throw<XunitException>()
            .Which.Message.Should().Contain("Ui/Pages/Gone.razor: exempted, but no longer a routable page")
            .And.Contain("Ui/Pages/Redirect.razor: listed as rendering no heading, but it now renders <PageHeader>")
            .And.Contain("scanned 1 routable page(s), expected at least 2");
    }

    [Fact]
    public void Rule_FlagsAMissingRoot()
    {
        var act = () => ArchitectureRules.RoutablePagesRenderExactlyOneH1(
            [Path.Combine(Path.GetTempPath(), "mmca-no-such-root-" + Guid.NewGuid().ToString("N"))],
            minimumPages: 0,
            Components);

        act.Should().Throw<XunitException>().Which.Message.Should().Contain("markup root not found");
    }
}
