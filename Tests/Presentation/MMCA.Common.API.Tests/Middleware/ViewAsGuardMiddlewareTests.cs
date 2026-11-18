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
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using MMCA.Common.Shared.Auth.Requests;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

/// <summary>
/// <see cref="ViewAsGuardMiddleware"/> (T20, ADR-137): the HTTP half of read-only view-as.
/// </summary>
public sealed class ViewAsGuardMiddlewareTests
{
    // The two host modes the merged cases run under: the store host reads the grant row through
    // IViewAsService; a service with HostsGrantStore false checks the shared cache's ended marker.
    private const string StoreHost = "StoreHost";
    private const string NonStoreHost = "NonStoreHost";

    private const UserIdentifierType StaffUserId = 1;
    private const UserIdentifierType TargetUserId = 2;

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly GrantStore _grants = new();
    private readonly SharedCache _cache = new();
    private bool _reachedEndpoint;

    // Spec B assertion 1 (HTTP half): with act_sub present every non-safe method is refused 403, on the
    // store host and on a service that does not hold the grant store alike.
    [Theory]
    [InlineData(StoreHost, "POST")]
    [InlineData(StoreHost, "PUT")]
    [InlineData(StoreHost, "PATCH")]
    [InlineData(StoreHost, "DELETE")]
    [InlineData(NonStoreHost, "POST")]
    [InlineData(NonStoreHost, "PUT")]
    [InlineData(NonStoreHost, "PATCH")]
    [InlineData(NonStoreHost, "DELETE")]
    public async Task NonSafeMethod_UnderAViewAsToken_Returns403_AndNeverReachesTheEndpoint(string host, string method)
    {
        var harness = Harness(host);
        var context = CreateContext(method, ViewAsPrincipal(harness.LiveGrant()));

        await harness.Invoke(context);

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

    // gRPC is always POST: a host marks a read-only service or method, and the method rule alone relaxes.
    [Theory]
    [InlineData(StoreHost)]
    [InlineData(NonStoreHost)]
    public async Task PostToAReadOnlyEndpoint_UnderALiveGrant_PassesThrough(string host)
    {
        var harness = Harness(host);
        var context = CreateContext("POST", ViewAsPrincipal(harness.LiveGrant()), readOnlyEndpoint: true);

        await harness.Invoke(context);

        _reachedEndpoint.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    // The mark exempts POST, the one method gRPC uses, and nothing else.
    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OtherNonSafeMethod_OnAReadOnlyEndpoint_IsStillRefused403(string method)
    {
        var grant = SeedLiveGrant();
        var context = CreateContext(method, ViewAsPrincipal(grant.Id), readOnlyEndpoint: true);

        await InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        _reachedEndpoint.Should().BeFalse();
    }

    [Fact]
    public async Task PostToAReadOnlyEndpoint_WithAnEndedGrant_IsStillRefused401()
    {
        var grant = SeedLiveGrant();
        var service = CreateService();
        (await service.EndAsync(grant.Id, StaffUserId)).IsSuccess.Should().BeTrue();

        var context = CreateContext("POST", ViewAsPrincipal(grant.Id), readOnlyEndpoint: true);
        await InvokeAsync(context, service);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    [Fact]
    public async Task PostToAReadOnlyEndpoint_WithViewAsDisabled_IsStillRefused401()
    {
        var grant = SeedLiveGrant();
        var context = CreateContext("POST", ViewAsPrincipal(grant.Id), readOnlyEndpoint: true);

        await InvokeAsync(context, enabled: false);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
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

    // Spec B assertion 4: after End, the same token is refused on the next request. The store host
    // ends the grant row; a non-store host sees the ended marker the store host's end endpoint writes.
    [Theory]
    [InlineData(StoreHost)]
    [InlineData(NonStoreHost)]
    public async Task AfterEnd_TheSameTokenIsRefusedOnTheNextRequest(string host)
    {
        var harness = Harness(host);
        var grantId = harness.LiveGrant();

        var before = CreateContext("GET", ViewAsPrincipal(grantId));
        await harness.Invoke(before);
        _reachedEndpoint.Should().BeTrue("the grant is live, and no ended marker exists, before End");

        await harness.End(grantId);

        _reachedEndpoint = false;
        var after = CreateContext("GET", ViewAsPrincipal(grantId));
        await harness.Invoke(after);

        after.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    [Theory]
    [InlineData(StoreHost)]
    [InlineData(NonStoreHost)]
    public async Task UnknownOrExpiredGrant_IsRefused401(string host)
    {
        var harness = Harness(host);
        var grantId = harness.LiveGrant();
        _clock.Advance(TimeSpan.FromMinutes(16));
        var context = CreateContext("GET", ViewAsPrincipal(grantId));

        await harness.Invoke(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    // The lookup (the grant row, or the shared cache on a non-store host) fails CLOSED for view-as
    // requests, unlike SoftDeletedUserMiddleware.
    [Theory]
    [InlineData(StoreHost)]
    [InlineData(NonStoreHost)]
    public async Task GrantLookupFailure_FailsClosed(string host)
    {
        var harness = Harness(host);
        var context = CreateContext("GET", ViewAsPrincipal(Guid.NewGuid()));

        await harness.InvokeWithFailingLookup(context);

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

    // Review fix: with ViewAs disabled a still-valid view-as token must not reach a write that bypasses
    // commands (password, preferences, devices, legal acceptance), nor any read.
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("GET")]
    public async Task ViewAsDisabled_AStillValidViewAsToken_IsRefused_AndNeverReachesTheEndpoint(string method)
    {
        var grant = SeedLiveGrant();
        var context = CreateContext(method, ViewAsPrincipal(grant.Id));

        await InvokeAsync(context, enabled: false);

        context.Response.StatusCode.Should().BeOneOf(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);
        _reachedEndpoint.Should().BeFalse();
    }

    [Fact]
    public async Task ViewAsDisabled_EvenTheEndViewAsAction_IsRefused()
    {
        var grant = SeedLiveGrant();
        var context = CreateContext("POST", ViewAsPrincipal(grant.Id), endViewAsEndpoint: true);

        await InvokeAsync(context, enabled: false);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    [Fact]
    public void TheDefaultPipeline_AlwaysIncludesTheGuard() =>
        MMCA.Common.API.Startup.Pipeline.MiddlewarePipelineBuilder.CreateDefault().StepNames
            .Should().Contain(MMCA.Common.API.Startup.Pipeline.MiddlewarePipelineStepNames.ViewAsGuard);

    [Theory]
    [InlineData(StoreHost)]
    [InlineData(NonStoreHost)]
    public async Task ViewAsTokenWithoutAGrantClaim_IsRefused401(string host)
    {
        var harness = Harness(host);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, "2"),
            new Claim(AuthClaimTypes.ViewAsActor, "1"),
            ExpiryClaim(Now.AddMinutes(15)),
        ],
        "Bearer"));
        var context = CreateContext("GET", principal);

        await harness.Invoke(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    // The lookup service the host mode needs (IViewAsService, or the shared ICacheService on a non-store
    // host) is not registered: the view-as token is refused rather than waved through.
    [Theory]
    [InlineData(StoreHost)]
    [InlineData(NonStoreHost)]
    public async Task LookupServiceNotRegistered_AViewAsToken_IsRefused401(string host)
    {
        var harness = Harness(host);
        var context = CreateContext("GET", ViewAsPrincipal(harness.LiveGrant()));

        await harness.InvokeWithoutLookupService(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    // ── Multi-service host: a service that does not hold the grant store (HostsGrantStore false) ──
    // It maps no ViewAsGrants table, so it cannot read the grant row; it checks the shared cache's
    // grant-ended marker, which the store host's end endpoint writes.
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task NonStoreHost_SafeMethod_WithALiveGrant_PassesThrough_WithoutAnyGrantLookup(string method)
    {
        var viewAs = new Mock<IViewAsService>(MockBehavior.Strict);
        var context = CreateContext(method, ViewAsPrincipal(Guid.NewGuid()));

        await InvokeNonStoreAsync(context, new SharedCache(), viewAs.Object);

        _reachedEndpoint.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        viewAs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NonStoreHost_TokenWithoutAnExpiry_IsRefused401()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, "2"),
            new Claim(AuthClaimTypes.ViewAsActor, "1"),
            new Claim(AuthClaimTypes.ViewAsGrant, Guid.NewGuid().ToString()),
        ],
        "Bearer"));
        var context = CreateContext("GET", principal);

        await InvokeNonStoreAsync(context, new SharedCache());

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    [Fact]
    public async Task NonStoreHost_WithViewAsDisabled_StillRefusesEveryViewAsToken()
    {
        var context = CreateContext("GET", ViewAsPrincipal(Guid.NewGuid()));
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ViewAsSettings { Enabled = false, HostsGrantStore = false }));
        services.AddSingleton<ICacheService>(new SharedCache());
        services.AddSingleton<TimeProvider>(_clock);
        await using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;

        await new ViewAsGuardMiddleware(_ =>
        {
            _reachedEndpoint = true;
            return Task.CompletedTask;
        }).InvokeAsync(context, NullLogger<ViewAsGuardMiddleware>.Instance);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        _reachedEndpoint.Should().BeFalse();
    }

    private static Claim ExpiryClaim(DateTimeOffset expiresAt) =>
        new("exp", expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64);

    private static ClaimsPrincipal ViewAsPrincipal(Guid grantId) =>
        new(new ClaimsIdentity(
        [
            new Claim(AuthClaimTypes.Subject, TargetUserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(AuthClaimTypes.ViewAsActor, StaffUserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(AuthClaimTypes.ViewAsGrant, grantId.ToString()),
            ExpiryClaim(Now.AddMinutes(15)),
        ],
        "Bearer"));

    private static DefaultHttpContext CreateContext(
        string method,
        ClaimsPrincipal user,
        bool endViewAsEndpoint = false,
        bool readOnlyEndpoint = false)
    {
        var context = new DefaultHttpContext { User = user };
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (endViewAsEndpoint)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ViewAsEndEndpointAttribute()), "end"));
        }

        if (readOnlyEndpoint)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new ViewAsReadOnlyEndpointAttribute()), "grpc-read"));
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

    private async Task InvokeAsync(HttpContext context, IViewAsService? viewAs = null, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(viewAs ?? CreateService());
        services.AddSingleton(Options.Create(new ViewAsSettings { Enabled = enabled }));
        await using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;

        var sut = new ViewAsGuardMiddleware(_ =>
        {
            _reachedEndpoint = true;
            return Task.CompletedTask;
        });
        await sut.InvokeAsync(context, NullLogger<ViewAsGuardMiddleware>.Instance);
    }

    private async Task InvokeNonStoreAsync(HttpContext context, ICacheService? cache, IViewAsService? viewAs = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ViewAsSettings { Enabled = true, HostsGrantStore = false }));
        services.AddSingleton<TimeProvider>(_clock);
        if (cache is not null)
        {
            services.AddSingleton(cache);
        }

        if (viewAs is not null)
        {
            services.AddSingleton(viewAs);
        }

        await using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;

        var sut = new ViewAsGuardMiddleware(_ =>
        {
            _reachedEndpoint = true;
            return Task.CompletedTask;
        });
        await sut.InvokeAsync(context, NullLogger<ViewAsGuardMiddleware>.Instance);
    }

    /// <summary>
    /// The per-host-mode wiring a merged case runs against: the store host over the real grant store
    /// and one <see cref="ViewAsService"/>, or a non-store host over <see cref="_cache"/>.
    /// </summary>
    private HostHarness Harness(string host) => host switch
    {
        StoreHost => StoreHarness(CreateService()),
        NonStoreHost => new HostHarness(
            LiveGrant: Guid.NewGuid,
            Invoke: context => InvokeNonStoreAsync(context, _cache),
            End: WriteEndedMarkerAsync,
            InvokeWithFailingLookup: context => InvokeNonStoreAsync(
                context, new SharedCache { ReadFault = new InvalidOperationException("cache unavailable") }),
            InvokeWithoutLookupService: context => InvokeNonStoreAsync(context, cache: null)),
        _ => throw new ArgumentOutOfRangeException(nameof(host), host, null),
    };

    private HostHarness StoreHarness(ViewAsService service) => new(
        LiveGrant: () => SeedLiveGrant().Id,
        Invoke: context => InvokeAsync(context, service),
        End: async grantId => (await service.EndAsync(grantId, StaffUserId)).IsSuccess.Should().BeTrue(),
        InvokeWithFailingLookup: context =>
        {
            var viewAs = new Mock<IViewAsService>();
            viewAs.Setup(v => v.IsGrantActiveAsync(It.IsAny<Guid>(), It.IsAny<UserIdentifierType>(), It.IsAny<UserIdentifierType>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("database unavailable"));
            return InvokeAsync(context, viewAs.Object);
        },
        InvokeWithoutLookupService: InvokeWithoutViewAsServiceAsync);

    // The store host's end endpoint writes the marker through this helper.
    private async Task WriteEndedMarkerAsync(Guid grantId)
    {
        var storeHostServices = new ServiceCollection()
            .AddSingleton<ICacheService>(_cache)
            .AddSingleton<TimeProvider>(_clock)
            .BuildServiceProvider();
        await using (storeHostServices)
        {
            await ViewAsEndedMarker.WriteAsync(storeHostServices, ViewAsPrincipal(grantId), grantId, CancellationToken.None);
        }
    }

    private async Task InvokeWithoutViewAsServiceAsync(HttpContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ViewAsSettings { Enabled = true }));
        await using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;

        await new ViewAsGuardMiddleware(_ =>
        {
            _reachedEndpoint = true;
            return Task.CompletedTask;
        }).InvokeAsync(context, NullLogger<ViewAsGuardMiddleware>.Instance);
    }

    /// <summary>One host mode's wiring, as the delegates a merged case calls.</summary>
    private sealed record HostHarness(
        Func<Guid> LiveGrant,
        Func<HttpContext, Task> Invoke,
        Func<Guid, Task> End,
        Func<HttpContext, Task> InvokeWithFailingLookup,
        Func<HttpContext, Task> InvokeWithoutLookupService);

    /// <summary>A dictionary standing in for the distributed cache every service shares.</summary>
    private sealed class SharedCache : ICacheService
    {
        public Dictionary<string, object?> Entries { get; } = new(StringComparer.Ordinal);

        public Exception? ReadFault { get; init; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            if (ReadFault is not null)
            {
                return Task.FromException<T?>(ReadFault);
            }

            return Task.FromResult(Entries.TryGetValue(key, out var value) ? (T?)value : default);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
        {
            Entries[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Entries.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

        // The guard never reads the support access log or erases an account.
        public Task<(IReadOnlyList<ViewAsGrant> Items, int TotalCount)> ListAsync(ViewAsGrantListRequest request, DateTime now, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ForgetUserAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
