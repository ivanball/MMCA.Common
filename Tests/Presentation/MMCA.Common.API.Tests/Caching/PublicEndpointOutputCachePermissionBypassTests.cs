using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Caching;
using MMCA.Common.Application.Auth.Permissions;
using MMCA.Common.Shared.Auth;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.API.Tests.Caching;

/// <summary>
/// Pins security review 2026-11-21 item A3: a <see cref="PublicEndpointOutputCachePolicy"/> built with
/// bypass permissions neither serves from nor stores into the shared cache for any caller who holds one
/// of them, evaluated the way the permission authorization handler evaluates it (a permission claim, a
/// role the registry grants it to, or a stored grant), even when that caller's role is not in the bypass
/// role list. Anonymous callers, and callers without the permission, still share the cached entry, and
/// the existing constructors keep their behavior.
/// </summary>
public sealed class PublicEndpointOutputCachePermissionBypassTests
{
    private const string ElevatedRead = "sessions:read-unpublished";
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task CacheRequest_CallerOutsideTheRoleListWhoseRoleGrantsThePermission_SkipsLookupAndStorage()
    {
        IOutputCachePolicy sut = PermissionPolicy();
        var context = CreateContext(Authenticated(new Claim(ClaimTypes.Role, "Organizer")), CompiledRegistry());

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeFalse("the elevated caller must never be served the shared public entry");
        context.AllowCacheStorage.Should().BeFalse("the elevated payload must never be stored under the shared key");
    }

    [Fact]
    public async Task CacheRequest_CallerHoldingThePermissionAsAClaim_SkipsLookupAndStorage()
    {
        IOutputCachePolicy sut = PermissionPolicy();
        var context = CreateContext(
            Authenticated(new Claim(ClaimTypes.Role, "Member"), new Claim(AuthClaimTypes.Permission, ElevatedRead)),
            CompiledRegistry());

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeFalse();
        context.AllowCacheStorage.Should().BeFalse();
    }

    [Fact]
    public async Task CacheRequest_CallerHoldingThePermissionThroughAStoredGrant_SkipsLookupAndStorage()
    {
        IOutputCachePolicy sut = PermissionPolicy();
        var grants = new Mock<IPermissionGrantCache>();
        grants.Setup(g => g.GetPermissions(It.IsAny<string>())).Returns(new HashSet<string>(StringComparer.Ordinal));
        grants
            .Setup(g => g.GetPermissions("Volunteer"))
            .Returns(new HashSet<string>(StringComparer.Ordinal) { ElevatedRead });
        var registry = new LayeredPermissionRegistry(CompiledRegistry(), grants.Object);
        var context = CreateContext(Authenticated(new Claim(ClaimTypes.Role, "Volunteer")), registry);

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeFalse("a stored grant is honored the way the permission handler honors it");
        context.AllowCacheStorage.Should().BeFalse();
    }

    [Fact]
    public async Task CacheRequest_AnonymousCaller_StillUsesTheSharedCache()
    {
        IOutputCachePolicy sut = PermissionPolicy();
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity()), CompiledRegistry());

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeTrue("anonymous callers are exactly who the shared entry is for");
        context.AllowCacheStorage.Should().BeTrue();
    }

    [Fact]
    public async Task CacheRequest_AuthenticatedCallerWithoutThePermission_StillUsesTheSharedCache()
    {
        IOutputCachePolicy sut = PermissionPolicy();
        var context = CreateContext(Authenticated(new Claim(ClaimTypes.Role, "Member")), CompiledRegistry());

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeTrue();
        context.AllowCacheStorage.Should().BeTrue();
    }

    [Fact]
    public async Task CacheRequest_BypassRoleListStillApplies_AlongsideThePermissions()
    {
        IOutputCachePolicy sut = PermissionPolicy();
        var context = CreateContext(Authenticated(new Claim(ClaimTypes.Role, "Manager")), CompiledRegistry());

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeFalse("the role list keeps working next to the permission list");
        context.AllowCacheStorage.Should().BeFalse();
    }

    [Fact]
    public async Task CacheRequest_TheRoleOnlyConstructor_KeepsCachingForAPermissionHolderOutsideTheRoleList()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration, ["Manager"], ["conference:sessions"]);
        var context = CreateContext(Authenticated(new Claim(ClaimTypes.Role, "Organizer")), CompiledRegistry());

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeTrue("an existing constructor names no permission, so its behavior is unchanged");
        context.AllowCacheStorage.Should().BeTrue();
    }

    [Fact]
    public async Task CacheRequest_TheTagsOnlyConstructor_KeepsCachingForAPermissionHolder()
    {
        IOutputCachePolicy sut = new PublicEndpointOutputCachePolicy(Expiration, "conference:sessions");
        var context = CreateContext(
            Authenticated(new Claim(ClaimTypes.Role, "Organizer"), new Claim(AuthClaimTypes.Permission, ElevatedRead)),
            CompiledRegistry());

        await sut.CacheRequestAsync(context, CancellationToken.None);

        context.AllowCacheLookup.Should().BeTrue();
        context.AllowCacheStorage.Should().BeTrue();
        context.Tags.Should().Contain("conference:sessions");
    }

    // -- Helpers --
    private static PublicEndpointOutputCachePolicy PermissionPolicy() =>
        new(Expiration, ["Manager"], [ElevatedRead], ["conference:sessions"]);

    // Organizer is granted the elevated read by the compiled registry; Member and Volunteer are not.
    private static PermissionRegistry CompiledRegistry() => new(new Dictionary<string, IReadOnlySet<string>>
    {
        ["Organizer"] = new HashSet<string>(StringComparer.Ordinal) { ElevatedRead },
        ["Member"] = new HashSet<string>(StringComparer.Ordinal),
        ["Volunteer"] = new HashSet<string>(StringComparer.Ordinal),
        ["Manager"] = new HashSet<string>(StringComparer.Ordinal),
    });

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "TestAuth"));

    private static OutputCacheContext CreateContext(ClaimsPrincipal user, IPermissionRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user,
        };
        httpContext.Request.Method = HttpMethods.Get;
        httpContext.Request.Headers.Authorization = "Bearer some-user-token";

        return new OutputCacheContext { HttpContext = httpContext };
    }
}
