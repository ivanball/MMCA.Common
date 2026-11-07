using System.Globalization;
using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Auth.ViewAs;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

/// <summary>
/// <see cref="ViewAsGuardMiddleware"/> (T20, ADR-137): the HTTP half of read-only view-as.
/// </summary>
public sealed class ViewAsGuardMiddlewareTests
{
    private const UserIdentifierType StaffUserId = 1;
    private const UserIdentifierType TargetUserId = 2;

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly GrantStore _grants = new();
    private bool _reachedEndpoint;

    // Spec B assertion 1 (HTTP half): with act_sub present every non-safe method is refused 403.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task NonSafeMethod_UnderAViewAsToken_Returns403_AndNeverReachesTheEndpoint(string method)
    {
        var grant = SeedLiveGrant();
        var context = CreateContext(method, ViewAsPrincipal(grant.Id));

        await InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _reachedEndpoint.Should().BeFalse();
    }

    // Spec B assertion 1 (the one exception): POST to the end-view-as action passes.
    [Fact]
    public async Task PostToTheEndViewAsAction_UnderAViewAsToken_PassesThrough()
    {
        var grant = SeedLiveGrant();
        var context = CreateContext("POST", ViewAsPrincipal(grant.Id), endViewAsEndpoint: true);

        await InvokeAsync(context);

        _reachedEndpoint.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SafeMethod_UnderALiveGrant_PassesThrough(string method)
    {
        var grant = SeedLiveGrant();
        var context = CreateContext(method, ViewAsPrincipal(grant.Id));

        await InvokeAsync(context);

        _reachedEndpoint.Should().BeTrue();
    }

    // Spec B assertion 4: after End, the same token is refused on the next request.
    [Fact]
    public async Task AfterEnd_TheSameTokenIsRefusedOnTheNextRequest()
    {
        var grant = SeedLiveGrant();
        var service = CreateService();

        var before = CreateContext("GET", ViewAsPrincipal(grant.Id));
        await InvokeAsync(before, service);
        _reachedEndpoint.Should().BeTrue("the grant is live before End");

        (await service.EndAsync(grant.Id, StaffUserId)).IsSuccess.Should().BeTrue();

        _reachedEndpoint = false;
        var after = CreateContext("GET", ViewAsPrincipal(grant.Id));
        await InvokeAsync(after, service);

        after.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownOrExpiredGrant_IsRefused401()
    {
        var grant = SeedLiveGrant();
        _clock.Advance(TimeSpan.FromMinutes(16));
        var context = CreateContext("GET", ViewAsPrincipal(grant.Id));

        await InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    // The lookup fails CLOSED for view-as requests (unlike SoftDeletedUserMiddleware).
    [Fact]
    public async Task GrantLookupFailure_FailsClosed()
    {
        var viewAs = new Mock<IViewAsService>();
        viewAs.Setup(v => v.IsGrantActiveAsync(It.IsAny<Guid>(), It.IsAny<UserIdentifierType>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var context = CreateContext("GET", ViewAsPrincipal(Guid.NewGuid()));

        await InvokeAsync(context, viewAs.Object);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    // Only view-as requests pay the lookup.
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task OrdinaryToken_PassesWithoutAnyGrantLookup(string method)
    {
        var viewAs = new Mock<IViewAsService>(MockBehavior.Strict);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(AuthClaimTypes.Subject, "5")], "Bearer"));
        var context = CreateContext(method, principal);

        await InvokeAsync(context, viewAs.Object);

        _reachedEndpoint.Should().BeTrue();
        viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ViewAsTokenWithoutAGrantClaim_IsRefused401()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AuthClaimTypes.Subject, "2"), new Claim(AuthClaimTypes.ViewAsActor, "1")], "Bearer"));
        var context = CreateContext("GET", principal);

        await InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static ClaimsPrincipal ViewAsPrincipal(Guid grantId) =>
        new(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, TargetUserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(AuthClaimTypes.ViewAsActor, StaffUserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(AuthClaimTypes.ViewAsGrant, grantId.ToString()),
        ],
        "Bearer"));

    private static DefaultHttpContext CreateContext(string method, ClaimsPrincipal user, bool endViewAsEndpoint = false)
    {
        var context = new DefaultHttpContext { User = user };
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (endViewAsEndpoint)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ViewAsEndEndpointAttribute()), "end"));
        }

        return context;
    }

    private ViewAsGrant SeedLiveGrant()
    {
        var grant = ViewAsGrant.Create(StaffUserId, TargetUserId, "Ticket 4411: schedule missing", Now.UtcDateTime, TimeSpan.FromMinutes(15)).Value!;
        _grants.Items.Add(grant);
        return grant;
    }

    private ViewAsService CreateService() => new(
        _grants,
        Mock.Of<ITokenService>(),
        new PermissionRegistryBuilder().Build(),
        Options.Create(new ViewAsSettings { Enabled = true }),
        _clock);

    private async Task InvokeAsync(HttpContext context, IViewAsService? viewAs = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(viewAs ?? CreateService());
        await using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;

        var sut = new ViewAsGuardMiddleware(_ =>
        {
            _reachedEndpoint = true;
            return Task.CompletedTask;
        });
        await sut.InvokeAsync(context, NullLogger<ViewAsGuardMiddleware>.Instance);
    }

    private sealed class GrantStore : IViewAsGrantStore
    {
        public List<ViewAsGrant> Items { get; } = [];

        public Task AddAsync(ViewAsGrant grant, CancellationToken cancellationToken = default)
        {
            Items.Add(grant);
            return Task.CompletedTask;
        }

        public Task<ViewAsGrant?> FindAsync(Guid grantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.Find(g => g.Id == grantId));

        public Task<bool> EndAsync(Guid grantId, UserIdentifierType staffUserId, DateTime endedAt, CancellationToken cancellationToken = default)
        {
            var grant = Items.Find(g => g.Id == grantId && g.StaffUserId == staffUserId);
            grant?.End(endedAt);
            return Task.FromResult(grant is not null);
        }

        public Task<IReadOnlyList<ViewAsGrant>> GetRecentForTargetAsync(UserIdentifierType targetUserId, int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ViewAsGrant>>([.. Items.Where(g => g.TargetUserId == targetUserId)]);

        public Task<int> DeleteIssuedBeforeAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.RemoveAll(g => g.GrantedAt < cutoff));
    }
}
