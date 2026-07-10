using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using MMCA.Common.API.Caching;

namespace MMCA.Common.API.Tests.Caching;

public class PublicEndpointOutputCachePolicyTests
{
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

    private static OutputCacheContext CreateContext(string method, bool withBearer = false)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        if (withBearer)
            httpContext.Request.Headers.Authorization = "Bearer some-user-token";

        return new OutputCacheContext { HttpContext = httpContext };
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
