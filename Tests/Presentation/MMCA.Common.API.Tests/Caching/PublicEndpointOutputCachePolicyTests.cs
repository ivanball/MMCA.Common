using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Caching;
using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.API.Tests.Caching;

public class PublicEndpointOutputCachePolicyTests
{
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

    private static OutputCacheContext CreateContext(string method, bool withBearer = false, string? role = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        if (withBearer)
            httpContext.Request.Headers.Authorization = "Bearer some-user-token";

        if (role is not null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, role)],
                authenticationType: "TestAuth"));
        }

        return new OutputCacheContext { HttpContext = httpContext };
    }

    private static OutputCacheContext CreateContextWithRoleClaimType(string claimType, string role)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(claimType, role)],
            authenticationType: "TestAuth"));

        return new OutputCacheContext { HttpContext = httpContext };
    }

    private static OutputCacheContext CreateContextWithTenant(string? tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenantContext(tenantId));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        httpContext.Request.Method = HttpMethods.Get;

        return new OutputCacheContext { HttpContext = httpContext };
    }

    // ── Bypass roles are read the way the permission handler reads them (SEC-Common-17) ──
    [Theory]
    [InlineData("role")]
    [InlineData("roles")]
    public async Task CacheRequest_BypassRoleUnderAnUnmappedClaimType_StillBypasses(string claimType)
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration, ["Organizer"], []);
        var context = CreateContextWithRoleClaimType(claimType, "Organizer");

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse(
            "the permission handler grants this caller the elevated payload through the same claim, so a "
            + "narrower read here would store that payload under the shared public key");
        context.AllowCacheLookup.Should().BeFalse();
    }

    [Fact]
    public async Task CacheRequest_BypassRoleDifferingOnlyInCase_StillBypasses()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration, ["Organizer"], []);
        var context = CreateContextWithRoleClaimType("roles", "organizer");

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse("role comparison is case-insensitive, as everywhere else");
    }

    // ── Tenancy is part of the cache key (SEC-Common-46) ──
    [Fact]
    public async Task CacheRequest_WhenATenantIsResolved_VariesTheKeyByIt()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContextWithTenant("tenant-b");

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.CacheVaryByRules.VaryByValues.Should().ContainKey("t")
            .WhoseValue.Should().Be(
                "tenant-b",
                "the entry is shared, so one tenant must never be served another tenant's rows");
    }

    [Fact]
    public async Task CacheRequest_WhenNoTenantIsResolved_AddsNoVaryValue()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContextWithTenant(tenantId: null);

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.CacheVaryByRules.VaryByValues.Should().BeEmpty(
            "a single-tenant host keeps exactly the cache key it had");
    }

    private sealed class StubTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;

        public bool IsResolved => tenantId is not null;

        public void SetTenant(string tenantId) => throw new NotSupportedException();
    }

    // ── The point of the policy: Authorization does not bypass the cache ──
    [Fact]
    public async Task CacheRequest_GetWithAuthorizationHeader_AllowsLookupAndStorage()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration, "conference:sessions");
        var context = CreateContext(HttpMethods.Get, withBearer: true);

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.EnableOutputCaching.Should().BeTrue();
        context.AllowCacheLookup.Should().BeTrue("a Bearer token on a public endpoint must not bypass the cache");
        context.AllowCacheStorage.Should().BeTrue();
        context.AllowLocking.Should().BeTrue();
        context.ResponseExpirationTimeSpan.Should().Be(Expiration);
        context.Tags.Should().Contain("conference:sessions");
    }

    [Fact]
    public async Task CacheRequest_AnonymousGet_AllowsLookupAndStorage()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContext(HttpMethods.Get);

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeTrue();
        context.AllowCacheStorage.Should().BeTrue();
    }

    // ── Cache-key parity with the built-in default policy ──
    [Fact]
    public async Task CacheRequest_VariesByEveryQueryKey_LikeTheDefaultPolicy()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContext(HttpMethods.Get);

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.CacheVaryByRules.QueryKeys.ToString().Should().Be(
            "*",
            "search, paging, filter, and field-projection variants of a path must not share one cache entry");
    }

    // ── Bypass roles: elevated callers skip the cache entirely ──
    [Fact]
    public async Task CacheRequest_CallerInBypassRole_DisallowsLookupAndStorage()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(
            Expiration, bypassRoles: ["Organizer"], tags: ["conference:events"]);
        var context = CreateContext(HttpMethods.Get, withBearer: true, role: "Organizer");

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeFalse("bypass-role callers receive elevated payloads that must never come from or land in the shared cache");
        context.AllowCacheStorage.Should().BeFalse();
    }

    [Fact]
    public async Task CacheRequest_AuthenticatedCallerNotInBypassRole_AllowsLookupAndStorage()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(
            Expiration, bypassRoles: ["Organizer"], tags: ["conference:events"]);
        var context = CreateContext(HttpMethods.Get, withBearer: true, role: "Attendee");

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeTrue("non-bypass callers share the identity-independent cached payload");
        context.AllowCacheStorage.Should().BeTrue();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task CacheRequest_NonReadMethods_DisallowLookupAndStorage(string method)
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContext(method, withBearer: true);

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeFalse();
        context.AllowCacheStorage.Should().BeFalse();
    }

    // ── Response-side guards ──
    [Fact]
    public async Task ServeResponse_SetCookieResponse_IsNotStored()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContext(HttpMethods.Get, withBearer: true);
        await sut.CacheRequestAsync(context, CancellationToken.None);
        context.HttpContext.Response.Headers.SetCookie = "session=abc";

        await sut.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [Theory]
    [InlineData(StatusCodes.Status301MovedPermanently)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public async Task ServeResponse_Non200Response_IsNotStored(int statusCode)
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContext(HttpMethods.Get, withBearer: true);
        await sut.CacheRequestAsync(context, CancellationToken.None);
        context.HttpContext.Response.StatusCode = statusCode;

        await sut.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeFalse();
    }

    [Fact]
    public async Task ServeResponse_Plain200_StaysStorable()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration);
        var context = CreateContext(HttpMethods.Get, withBearer: true);
        await sut.CacheRequestAsync(context, CancellationToken.None);
        context.HttpContext.Response.StatusCode = StatusCodes.Status200OK;

        await sut.ServeResponseAsync(context, CancellationToken.None);

        context.AllowCacheStorage.Should().BeTrue();
    }

    // ── Constructor guards ──
    [Fact]
    public void Constructor_NonPositiveExpiration_Throws()
    {
        var act = () => new PublicEndpointOutputCachePolicy(TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
