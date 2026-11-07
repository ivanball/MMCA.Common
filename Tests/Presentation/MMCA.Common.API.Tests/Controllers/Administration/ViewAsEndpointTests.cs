using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Controllers.Administration;
using MMCA.Common.API.Idempotency;
using MMCA.Common.API.Middleware;
using MMCA.Common.API.Tests.Controllers.Auth;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.Administration;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using Moq;

namespace MMCA.Common.API.Tests.Controllers.Administration;

/// <summary>
/// The grant action on <see cref="UsersAdminControllerBase{TUserDto}"/> and the end action on
/// <see cref="AuthControllerBase"/> (T20, ADR-137).
/// </summary>
public sealed class ViewAsEndpointTests
{
    private const UserIdentifierType StaffUserId = 1;
    private const UserIdentifierType TargetUserId = 2;

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly MethodInfo StartAction =
        typeof(UsersAdminControllerBase<TestUserDto>).GetMethod(nameof(UsersAdminControllerBase<>.StartViewAsAsync))!;

    private readonly Mock<IViewAsService> _viewAs = new(MockBehavior.Strict);

    // Spec B assertion 5: with ViewAs:Enabled false the grant endpoint returns 404.
    [Fact]
    public async Task StartViewAsAsync_WhenViewAsIsDisabled_Returns404_AndIssuesNothing()
    {
        var sut = CreateUsersController(StaffPrincipal());

        var result = await sut.StartViewAsAsync(TargetUserId, new StartViewAsRequest("Ticket 4411: schedule missing"), Settings(enabled: false), _viewAs.Object);

        result.Result.Should().BeOfType<NotFoundResult>();
        _viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EndViewAsAsync_WhenViewAsIsDisabled_Returns404()
    {
        var sut = CreateAuthController(ViewAsPrincipal(Guid.NewGuid()));

        var result = await sut.EndViewAsAsync(Settings(enabled: false), _viewAs.Object, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StartViewAsAsync_WhenEnabled_ReturnsTheGrantForTheCallerAndTheRoutedAccount()
    {
        var response = new ViewAsGrantResponse("view-as-token", DateTime.UtcNow.AddMinutes(15), Guid.NewGuid(), TargetUserId);
        _viewAs.Setup(v => v.StartAsync(StaffUserId, TargetUserId, "Ticket 4411: schedule missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(response));
        var sut = CreateUsersController(StaffPrincipal());

        var result = await sut.StartViewAsAsync(TargetUserId, new StartViewAsRequest("Ticket 4411: schedule missing"), Settings(enabled: true), _viewAs.Object);

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(response);
    }

    [Fact]
    public async Task StartViewAsAsync_ARefusal_IsProblemDetails()
    {
        _viewAs.Setup(v => v.StartAsync(StaffUserId, TargetUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<ViewAsGrantResponse>(Error.Forbidden("ViewAs.TargetIsStaff", "No.")));
        var sut = CreateUsersController(StaffPrincipal());

        var result = await sut.StartViewAsAsync(TargetUserId, new StartViewAsRequest("Ticket 4411: schedule missing"), Settings(enabled: true), _viewAs.Object);

        result.Result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task EndViewAsAsync_EndsTheGrantNamedByTheToken()
    {
        var grantId = Guid.NewGuid();
        _viewAs.Setup(v => v.EndAsync(grantId, StaffUserId, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
        var sut = CreateAuthController(ViewAsPrincipal(grantId));

        var result = await sut.EndViewAsAsync(Settings(enabled: true), _viewAs.Object, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        _viewAs.Verify(v => v.EndAsync(grantId, StaffUserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EndViewAsAsync_ForAnOrdinaryToken_IsABadRequest()
    {
        var sut = CreateAuthController(StaffPrincipal());

        var result = await sut.EndViewAsAsync(Settings(enabled: true), _viewAs.Object, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        _viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public void StartViewAsAsync_IsRoutedAsPostUserIdViewAs_AndRequiresUsersViewAsOnTopOfUsersManage()
    {
        StartAction.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("{userId}/view-as");
        StartAction.GetCustomAttribute<HasPermissionAttribute>()!.Permission.Should().Be("users:view-as");
        StartAction.DeclaringType!.GetCustomAttribute<HasPermissionAttribute>()!.Permission.Should().Be(AdministrationPermissions.ManageUsers);
        StartAction.GetCustomAttribute<NonIdempotentAttribute>().Should().NotBeNull();
    }

    // Multi-service host: the end endpoint writes the shared grant-ended marker the other services read,
    // and it must outlive the token: its expiry plus the JWT bearer clock skew.
    [Fact]
    public async Task EndViewAsAsync_WritesTheGrantEndedMarker_WithATtlPastTheTokenExpiry()
    {
        var grantId = Guid.NewGuid();
        _viewAs.Setup(v => v.EndAsync(grantId, StaffUserId, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
        var cache = RecordingCache(out var writes);
        await using var services = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Now))
            .BuildServiceProvider();
        var sut = CreateAuthController(ViewAsPrincipal(grantId, Now.AddMinutes(12)), services);

        var result = await sut.EndViewAsAsync(Settings(enabled: true), _viewAs.Object, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var (key, ttl) = writes.Should().ContainSingle().Subject;
        key.Should().Be(ViewAsEndedMarker.KeyFor(grantId));
        ttl.Should().Be(TimeSpan.FromMinutes(12) + TokenValidationParameters.DefaultClockSkew);
        Now.Add(ttl!.Value).Should().BeAfter(Now.AddMinutes(12), "the marker must outlive the token");
    }

    [Fact]
    public async Task EndViewAsAsync_MarkerTtl_UsesTheHostsConfiguredClockSkew()
    {
        var grantId = Guid.NewGuid();
        _viewAs.Setup(v => v.EndAsync(grantId, StaffUserId, It.IsAny<CancellationToken>())).ReturnsAsync(Result.Success());
        var cache = RecordingCache(out var writes);
        var serviceCollection = new ServiceCollection()
            .AddSingleton(cache)
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        serviceCollection.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            o => o.TokenValidationParameters = new TokenValidationParameters { ClockSkew = TimeSpan.FromMinutes(2) });
        await using var services = serviceCollection.BuildServiceProvider();
        var sut = CreateAuthController(ViewAsPrincipal(grantId, Now.AddMinutes(15)), services);

        await sut.EndViewAsAsync(Settings(enabled: true), _viewAs.Object, CancellationToken.None);

        writes.Should().ContainSingle().Which.Ttl.Should().Be(TimeSpan.FromMinutes(17));
    }

    [Fact]
    public async Task EndViewAsAsync_WhenTheGrantIsNotFound_WritesNoMarker()
    {
        var grantId = Guid.NewGuid();
        _viewAs.Setup(v => v.EndAsync(grantId, StaffUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure(Error.NotFoundError("ViewAs.GrantNotFound", "No such view-as grant.")));
        var cache = RecordingCache(out var writes);
        await using var services = new ServiceCollection().AddSingleton(cache).BuildServiceProvider();
        var sut = CreateAuthController(ViewAsPrincipal(grantId, Now.AddMinutes(15)), services);

        await sut.EndViewAsAsync(Settings(enabled: true), _viewAs.Object, CancellationToken.None);

        writes.Should().BeEmpty();
    }

    // A service that does not hold the grant store serves neither the grant nor the end endpoint.
    [Fact]
    public async Task StartViewAsAsync_OnAHostWithoutTheGrantStore_Returns404_AndIssuesNothing()
    {
        var sut = CreateUsersController(StaffPrincipal());

        var result = await sut.StartViewAsAsync(TargetUserId, new StartViewAsRequest("Ticket 4411: schedule missing"), NonStoreSettings(), _viewAs.Object);

        result.Result.Should().BeOfType<NotFoundResult>();
        _viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EndViewAsAsync_OnAHostWithoutTheGrantStore_Returns404()
    {
        var sut = CreateAuthController(ViewAsPrincipal(Guid.NewGuid()));

        var result = await sut.EndViewAsAsync(NonStoreSettings(), _viewAs.Object, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        _viewAs.VerifyNoOtherCalls();
    }

    private static IOptions<ViewAsSettings> Settings(bool enabled) => Options.Create(new ViewAsSettings { Enabled = enabled });

    private static IOptions<ViewAsSettings> NonStoreSettings() =>
        Options.Create(new ViewAsSettings { Enabled = true, HostsGrantStore = false });

    private static ICacheService RecordingCache(out List<(string Key, TimeSpan? Ttl)> writes)
    {
        var recorded = new List<(string Key, TimeSpan? Ttl)>();
        var cache = new Mock<ICacheService>();
        cache.Setup(c => c.SetAsync(It.IsAny<string>(), true, It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, bool, TimeSpan?, CancellationToken>((key, _, ttl, _) => recorded.Add((key, ttl)))
            .Returns(Task.CompletedTask);
        writes = recorded;
        return cache.Object;
    }

    private static ClaimsPrincipal StaffPrincipal() =>
        new(new ClaimsIdentity([new Claim(AuthClaimTypes.Subject, "1")], "Bearer"));

    private static ClaimsPrincipal ViewAsPrincipal(Guid grantId, DateTimeOffset? expiresAt = null) =>
        new(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, "2"),
            new Claim(AuthClaimTypes.ViewAsActor, "1"),
            new Claim(AuthClaimTypes.ViewAsGrant, grantId.ToString()),
            new Claim("exp", (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
        ],
        "Bearer"));

    private static TestUsersAdminController CreateUsersController(ClaimsPrincipal user) =>
        new(Mock.Of<IUserAdministrationService<TestUserDto>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } },
        };

    private static TestAuthController CreateAuthController(ClaimsPrincipal user, IServiceProvider? services = null)
    {
        var httpContext = new DefaultHttpContext { User = user };
        if (services is not null)
        {
            httpContext.RequestServices = services;
        }

        return new(Mock.Of<IAuthenticationService>(), Mock.Of<ICurrentUserService>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
