using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// <c>GET</c> on <see cref="ViewAsGrantsAdminControllerBase"/>, the support access log (ADR-137): who
/// may read it, the 404 on a host that does not hold the grant store, and the page it answers.
/// </summary>
public sealed class ViewAsGrantsAdminEndpointTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly MethodInfo ListAction =
        typeof(ViewAsGrantsAdminControllerBase).GetMethod(nameof(ViewAsGrantsAdminControllerBase.ListAsync))!;

    private readonly Mock<IViewAsGrantStore> _grants = new(MockBehavior.Strict);
    private readonly FakeTimeProvider _clock = new(Now);

    // Spec A assertion 2 (403 half): the action carries users:manage and nothing weaker, and the
    // permission policy refuses a signed-in caller who does not hold it.
    [Fact]
    public async Task ListAsync_RequiresUsersManage_ACallerWithoutItIsRefused()
    {
        ListAction.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Should().BeEmpty();
        var required = typeof(ViewAsGrantsAdminControllerBase).GetCustomAttribute<HasPermissionAttribute>()!;
        required.Permission.Should().Be(AdministrationPermissions.ManageUsers);
        ListAction.GetCustomAttributes<HasPermissionAttribute>(inherit: true)
            .Select(a => a.Permission)
            .Should().OnlyContain(p => p == AdministrationPermissions.ManageUsers);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationPolicies();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var withoutIt = await authorization.AuthorizeAsync(Caller("sessions:read"), required.Policy!);
        var withIt = await authorization.AuthorizeAsync(Caller(AdministrationPermissions.ManageUsers), required.Policy!);

        withoutIt.Succeeded.Should().BeFalse("a signed-in caller without users:manage is forbidden (403)");
        withIt.Succeeded.Should().BeTrue();
    }

    // Spec A assertion 2 (404 half): a service without the grant store, and a host with view-as off,
    // answer 404 without reading the store, matching the grant and end actions.
    [Fact]
    public async Task ListAsync_OnAHostWithoutTheGrantStore_Returns404_AndReadsNothing()
    {
        var result = await CreateController().ListAsync(
            new ViewAsGrantListRequest(),
            Options.Create(new ViewAsSettings { Enabled = true, HostsGrantStore = false }),
            _grants.Object,
            _clock);

        result.Result.Should().BeOfType<NotFoundResult>();
        _grants.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ListAsync_WhenViewAsIsDisabled_Returns404()
    {
        var result = await CreateController().ListAsync(
            new ViewAsGrantListRequest(),
            Options.Create(new ViewAsSettings()),
            _grants.Object,
            _clock);

        result.Result.Should().BeOfType<NotFoundResult>();
        _grants.VerifyNoOtherCalls();
    }

    // Spec A assertion 1 (API half): the page comes back in the store's order with the derived
    // status, both display snapshots, the reason and the total.
    [Fact]
    public async Task ListAsync_ReturnsTheStoresPage_WithTheStatusDerivedAtTheReadInstant()
    {
        var live = Grant(Now.UtcDateTime.AddMinutes(-5));
        var ended = Grant(Now.UtcDateTime.AddHours(-1));
        ended.End(Now.UtcDateTime.AddMinutes(-50));
        var expired = Grant(Now.UtcDateTime.AddHours(-2));
        var request = new ViewAsGrantListRequest { Staff = "ana", PageNumber = 2, PageSize = 3 };
        _grants.Setup(g => g.ListAsync(request, Now.UtcDateTime, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<ViewAsGrant>)[live, ended, expired], 6));

        var result = await CreateController().ListAsync(
            request,
            Options.Create(new ViewAsSettings { Enabled = true }),
            _grants.Object,
            _clock);

        var page = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<PagedCollectionResult<ViewAsGrantSummaryResponse>>().Subject;
        page.Items.Select(i => i.Id).Should().Equal(live.Id, ended.Id, expired.Id);
        page.Items.Select(i => i.Status).Should().Equal(ViewAsGrantStatus.Live, ViewAsGrantStatus.Ended, ViewAsGrantStatus.Expired);
        var first = page.Items.First();
        first.StaffDisplay.Should().Be("ana@staff.test");
        first.TargetDisplay.Should().Be("bob@example.com");
        first.Reason.Should().Be("Ticket 4411: schedule missing");
        page.PaginationMetadata.TotalItemCount.Should().Be(6);
        page.PaginationMetadata.CurrentPage.Should().Be(2);
        page.PaginationMetadata.PageSize.Should().Be(3);
    }

    [Fact]
    public void ListAsync_IsAGetOnTheSubclassRoute_ResolvingItsServicesPerAction()
    {
        ListAction.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().BeNull("the subclass owns the route");
        typeof(ViewAsGrantsAdminControllerBase).GetCustomAttribute<RouteAttribute>().Should().BeNull();
        ListAction.GetParameters()
            .Where(p => p.ParameterType != typeof(ViewAsGrantListRequest) && p.ParameterType != typeof(CancellationToken))
            .Should().OnlyContain(p => p.GetCustomAttribute<FromServicesAttribute>() != null);
        ListAction.GetParameters().Single(p => p.ParameterType == typeof(ViewAsGrantListRequest))
            .GetCustomAttribute<FromQueryAttribute>().Should().NotBeNull();
    }

    private static ClaimsPrincipal Caller(string permission) =>
        new(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, "1"),
            new Claim(AuthClaimTypes.Permission, permission),
        ],
        "Bearer"));

    private static ViewAsGrant Grant(DateTime grantedAt) =>
        ViewAsGrant.Create(1, 10, "Ticket 4411: schedule missing", grantedAt, TimeSpan.FromMinutes(15), "ana@staff.test", "bob@example.com").Value!;

    private static TestViewAsGrantsAdminController CreateController() =>
        new() { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
}

[Route("Admin/ViewAsGrants")]
internal sealed class TestViewAsGrantsAdminController : ViewAsGrantsAdminControllerBase;
