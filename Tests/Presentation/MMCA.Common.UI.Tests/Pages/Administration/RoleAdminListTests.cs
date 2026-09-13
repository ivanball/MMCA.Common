using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Pages.Administration;
using MMCA.Common.UI.Services.Administration;
using Moq;

namespace MMCA.Common.UI.Tests.Pages.Administration;

/// <summary>
/// bUnit tests for the shared <see cref="RoleAdminList"/> component (the roster half of ADR-116's
/// role administration): the rows and their two counts, the empty and failed states, the edit link
/// the host supplies, and the app-localizer override.
/// </summary>
public sealed class RoleAdminListTests : BunitTestBase
{
    private readonly Mock<IRoleAdminUIService> _roles = new();

    public RoleAdminListTests()
    {
        Services.AddSingleton(_roles.Object);

        SetupRoles(
            new RolePermissionsResponse("Admin", ["orders:write", "reports:read"], ["exports:run"]),
            new RolePermissionsResponse("Customer", ["orders:read"], []));
    }

    [Fact]
    public async Task RendersOneRowPerRoleWithBothCounts()
    {
        var cut = RenderList();

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.Markup.Should().Contain("Admin");
        cut.Markup.Should().Contain("Customer");

        cut.FindAll("[data-testid=\"compiled-count\"]").Select(cell => cell.TextContent.Trim())
            .Should().Equal("2", "1");
        cut.FindAll("[data-testid=\"stored-count\"]").Select(cell => cell.TextContent.Trim())
            .Should().Equal("1", "0");
    }

    /// <summary>
    /// The edit action is a real link built by the host, because the framework component knows
    /// nothing about the app's routes.
    /// </summary>
    [Fact]
    public async Task TheEditActionLinksToTheHostSuppliedRoute()
    {
        var cut = RenderList();

        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=\"edit-role\"]").Should().HaveCount(2));
        cut.FindAll("[data-testid=\"edit-role\"]")[0].GetAttribute("href").Should().Be("/admin/roles/Admin");
        cut.FindAll("[data-testid=\"edit-role\"]")[0].GetAttribute("aria-label")
            .Should().Be("Edit the stored permissions of Admin");
    }

    [Fact]
    public async Task WithNoRoles_RendersTheEmptyState()
    {
        SetupRoles();

        var cut = RenderList();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("No roles found."));
    }

    /// <summary>
    /// A failed load renders zero rows, which is visually identical to a genuinely empty list, so
    /// the inline alert and its retry have to take over instead of the empty state.
    /// </summary>
    [Fact]
    public async Task WhenTheLoadFails_RendersTheInlineFailureAndARetry()
    {
        _roles.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<RolePermissionsResponse>>(
                Error.Failure("Admin.Roles.LoadFailed", "The roles could not be loaded.")));

        var cut = RenderList();

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("The roles could not be loaded."));
        cut.Markup.Should().Contain("Try again");
        cut.Markup.Should().NotContain("No roles found.");
    }

    [Fact]
    public async Task TheRetryButton_ReloadsThroughTheService()
    {
        _roles.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<RolePermissionsResponse>>(
                Error.Failure("Admin.Roles.LoadFailed", "The roles could not be loaded.")));
        var cut = RenderList();
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Try again"));

        SetupRoles(new RolePermissionsResponse("Admin", [], []));
        await cut.Find("button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.FindAll("tbody tr").Should().ContainSingle());
    }

    /// <summary>
    /// An app keeps its own vocabulary by passing a localizer, without a parameter per word; a key
    /// that localizer does not carry still falls back to the component's own resources.
    /// </summary>
    [Fact]
    public async Task AnAppLocalizerOverridesTheComponentsOwnWording()
    {
        var cut = RenderList(p => p.Add(x => x.Localizer, new StubLocalizer("Title", "Security groups")));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("Security groups"));
        cut.Markup.Should().Contain("Role", "an unmapped key falls back to the component's own resources");
    }

    private void SetupRoles(params RolePermissionsResponse[] roles) =>
        _roles.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success<IReadOnlyList<RolePermissionsResponse>>(roles));

    private IRenderedComponent<RoleAdminList> RenderList(
        Action<ComponentParameterCollectionBuilder<RoleAdminList>>? extra = null) =>
        RenderUnderTest<RoleAdminList>(p =>
        {
            p.Add(x => x.EditHref, role => $"/admin/roles/{role}");
            extra?.Invoke(p);
        });

    /// <summary>
    /// A localizer carrying exactly one key, so a test can prove both halves of the override: the
    /// key it holds wins, and every other key falls through to the component's resources.
    /// </summary>
    /// <param name="key">The one key this localizer answers.</param>
    /// <param name="value">Its value.</param>
    private sealed class StubLocalizer(string key, string value) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            new(name, string.Equals(name, key, StringComparison.Ordinal) ? value : name,
                resourceNotFound: !string.Equals(name, key, StringComparison.Ordinal));

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [this[key]];
    }
}
