using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Services.Administration;
using Moq;
using Xunit;

namespace MMCA.Common.Testing.UI.Pages;

/// <summary>
/// Shared facts for a consumer's role roster page: the thin routed shell over the framework's
/// <c>RoleAdminList</c> (ADR-116). What is pinned is the consumer's own half: the roster renders
/// through the consumer's page, each row links to the consumer's editor route, and a failed load
/// shows no rows. A consumer keeps a one-line sealed subclass naming its page, two of its roles and
/// its edit-route builder (the ADR-015 pattern).
/// </summary>
/// <typeparam name="TPage">The consumer's routed roster page.</typeparam>
public abstract class RoleAdminListPageTestsBase<TPage> : BunitComponentTestBase
    where TPage : IComponent
{
    protected RoleAdminListPageTestsBase() => Services.AddSingleton(Roles.Object);

    /// <summary>Gets the mocked role-administration client the page's component calls.</summary>
    protected Mock<IRoleAdminUIService> Roles { get; } = new();

    /// <summary>Gets the first role the roster lists (for example the app's administrator role).</summary>
    protected abstract string FirstRole { get; }

    /// <summary>Gets the second role the roster lists.</summary>
    protected abstract string SecondRole { get; }

    [Fact]
    public void RendersOneRowPerRoleWithItsCompiledAndStoredCounts()
    {
        SetupDefaultRoles();
        RenderMudProviders();

        var cut = RenderUnderTest<TPage>(_ => { });

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain(FirstRole);
            cut.Markup.Should().Contain(SecondRole);
            cut.FindAll("[data-testid=compiled-count]").Should().HaveCount(2);
            cut.FindAll("[data-testid=stored-count]").Should().HaveCount(2);
        });
    }

    /// <summary>
    /// The edit link is the one parameter the consumer supplies, and it is required by the component,
    /// so a page that stopped passing it would not compile but a page that passed the wrong shape
    /// would compile and dead-end the operator.
    /// </summary>
    [Fact]
    public void EachRow_LinksToTheRoleEditorRoute()
    {
        SetupDefaultRoles();
        RenderMudProviders();

        var cut = RenderUnderTest<TPage>(_ => { });

        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid=edit-role]")
                .Select(a => a.GetAttribute("href"))
                .Should().Equal(EditRoute(FirstRole), EditRoute(SecondRole)));
    }

    /// <summary>
    /// A failed load renders zero rows, which is visually identical to a genuinely empty roster, so
    /// the shared inline error state must take over instead of the empty message.
    /// </summary>
    [Fact]
    public void WhenTheFetchFails_RendersNoRoleRows()
    {
        Roles
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<IReadOnlyList<RolePermissionsResponse>>(
                Error.Failure("Roles.LoadFailed", "boom")));
        RenderMudProviders();

        var cut = RenderUnderTest<TPage>(_ => { });

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=edit-role]").Should().BeEmpty());
    }

    /// <summary>Builds the consumer's route to one role's editor, exactly as its page passes it.</summary>
    /// <param name="role">The role name.</param>
    /// <returns>The editor route, for example <c>/roles/Admin</c>.</returns>
    protected abstract string EditRoute(string role);

    /// <summary>
    /// Sets the roster the mocked client returns. Every Result-returning member needs an explicit
    /// setup: an unstubbed <c>Task&lt;Result&lt;T&gt;&gt;</c> hands back null rather than a success,
    /// which the component would dereference.
    /// </summary>
    /// <param name="roles">The roles to return.</param>
    protected void SetupRoles(IReadOnlyList<RolePermissionsResponse> roles) =>
        Roles
            .Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(roles));

    // Set per fact rather than in the constructor: the role names are abstract members, and a
    // constructor must not call them before the derived class has initialized.
    private void SetupDefaultRoles() =>
        SetupRoles(
        [
            new RolePermissionsResponse(FirstRole, ["sample:items:read", "sample:items:manage"], []),
            new RolePermissionsResponse(SecondRole, ["sample:reports:run"], ["sample:items:read"]),
        ]);
}
