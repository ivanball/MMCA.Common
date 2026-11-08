using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Services.Administration;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// bUnit tests for the shared <see cref="FeatureFlagAdmin"/> component: one table across every flag
/// source (so a multi-service host shows its flags together), and the set and clear actions each
/// reaching the source that owns the flag.
/// </summary>
public sealed class FeatureFlagAdminTests : BunitTestBase
{
    private const string Conference = "Admin/Conference/FeatureFlags";
    private const string Engagement = "Admin/Engagement/FeatureFlags";

    private readonly Mock<IFeatureFlagAdminUIService> _flags = new();

    public FeatureFlagAdminTests()
    {
        Services.AddSingleton(_flags.Object);
        Services.AddSingleton(Options.Create(new FeatureFlagAdminUIOptions { Sources = [Conference, Engagement] }));

        Setup(Conference, Flag("Conference.SessionScoring", FeatureFlagValueKind.On, null));
        Setup(Engagement, Flag(
            "Engagement.Points",
            FeatureFlagValueKind.On,
            new FeatureFlagOverrideResponse("Engagement.Points", FeatureFlagOverrideMode.Off, null, [], [], "Leaderboard bug", null, null)));
    }

    [Fact]
    public async Task ListsTheFlagsOfEverySourceInOneTable()
    {
        var cut = RenderUnderTest<FeatureFlagAdmin>(_ => { });

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.FindAll("[data-testid=\"flag-name\"]").Select(cell => cell.TextContent.Trim())
            .Should().Equal("Conference.SessionScoring", "Engagement.Points");
        cut.FindAll("[data-testid=\"flag-source\"]").Select(cell => cell.TextContent.Trim())
            .Should().Equal(Conference, Engagement);
        cut.FindAll("[data-testid=\"flag-override\"]")[1].TextContent.Should().Contain("Leaderboard bug",
            "an active override shows its reason, since an override is never time-boxed");
    }

    [Fact]
    public async Task SettingAnOverride_SendsItsReasonToTheSourceThatOwnsTheFlag()
    {
        _flags.Setup(x => x.SetOverrideAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SetFeatureFlagOverrideRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(Flag("Conference.SessionScoring", FeatureFlagValueKind.On, null)));
        var cut = RenderUnderTest<FeatureFlagAdmin>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"set-override\"]").Should().HaveCount(2));

        await cut.FindAll("[data-testid=\"set-override\"]")[0].ClickAsync(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => cut.FindAll("textarea").Should().ContainSingle());
        await cut.Find("textarea").InputAsync(new ChangeEventArgs { Value = "Scoring outage" });
        await cut.Find("[data-testid=\"save-override\"]").ClickAsync(new MouseEventArgs());

        _flags.Verify(x => x.SetOverrideAsync(
            Conference,
            "Conference.SessionScoring",
            It.Is<SetFeatureFlagOverrideRequest>(r => r.Mode == FeatureFlagOverrideMode.Off && r.Reason == "Scoring outage"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearingAnOverride_ReachesTheSourceThatOwnsTheFlag()
    {
        _flags.Setup(x => x.ClearOverrideAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
        var cut = RenderUnderTest<FeatureFlagAdmin>(_ => { });
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"clear-override\"]").Should().ContainSingle(
            "only a flag with an override can be cleared"));

        await cut.Find("[data-testid=\"clear-override\"]").ClickAsync(new MouseEventArgs());

        _flags.Verify(x => x.ClearOverrideAsync(Engagement, "Engagement.Points", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static FeatureFlagStatusResponse Flag(string name, FeatureFlagValueKind configured, FeatureFlagOverrideResponse? stored) =>
        new(name, FeatureFlagLifetime.Permanent, "team", null, configured, [], stored,
            stored is null ? configured : FeatureFlagValueKind.Off);

    private void Setup(string source, params FeatureFlagStatusResponse[] flags) =>
        _flags.Setup(x => x.GetAllAsync(source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<FeatureFlagStatusResponse>>(flags));
}
