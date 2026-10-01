using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using MMCA.Common.API;
using MMCA.Common.API.SessionCookies;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Web.ClientConfig;
using MMCA.Common.UI.Web.SameOriginProxy;

namespace MMCA.Common.UI.Web.Tests.SameOriginProxy;

/// <summary>One request the fake gateway received, as the gateway saw it.</summary>
internal sealed record SeenRequest(string Method, string PathAndQuery, string? Authorization, string? Cookie, string? Csrf, string? ForwardedFor);

/// <summary>
/// An in-process stand-in for the MMCA gateway: the identity endpoints the proxy and the cookie
/// refresher call (<c>auth/login</c>, <c>auth/refresh</c>, <c>auth/revoke</c>) plus a catch-all that
/// records what it saw and answers 401 for a bearer the test marked as rejected.
/// </summary>
internal sealed class FakeGateway : IAsyncDisposable
{
    public const string LoginRefreshToken = "refresh-from-login";
    public const string RotatedRefreshToken = "refresh-rotated";

    private int _refreshCalls;

    private FakeGateway()
    {
    }

    public IHost Host { get; private set; } = null!;

    public TestServer Server => Host.GetTestServer();

    public ConcurrentQueue<SeenRequest> Seen { get; } = new();

    public ConcurrentDictionary<string, bool> RejectedTokens { get; } = new(StringComparer.Ordinal);

    public int RefreshCalls => Volatile.Read(ref _refreshCalls);

    public bool FailRefresh { get; set; }

    /// <summary>When set, <c>auth/refresh</c> answers this status (a transient failure, or a refusal).</summary>
    public HttpStatusCode? RefreshFailureStatus { get; set; }

    /// <summary>The <c>Retry-After</c> value sent with <see cref="RefreshFailureStatus"/>, if any.</summary>
    public string? RefreshRetryAfter { get; set; }

    public TimeSpan RefreshDelay { get; set; } = TimeSpan.Zero;

    public string LoginAccessToken { get; } = Jwt.Create(DateTime.UtcNow.AddMinutes(10), "login-user");

    public string RotatedAccessToken { get; } = Jwt.Create(DateTime.UtcNow.AddMinutes(15), "rotated-user");

    public static async Task<FakeGateway> StartAsync()
    {
        var gateway = new FakeGateway();
        gateway.Host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(gateway.Map);
                }))
            .Build()
            .StartAndReturnAsync();
        return gateway;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }

    private void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/login", () => Results.Json(new AuthenticationResponse(LoginAccessToken, LoginRefreshToken, DateTime.UtcNow.AddMinutes(10))));

        endpoints.MapPost("/auth/refresh", async (HttpContext context) =>
        {
            Interlocked.Increment(ref _refreshCalls);
            await Task.Delay(RefreshDelay);
            if (RefreshFailureStatus is { } status)
            {
                if (RefreshRetryAfter is { } retryAfter)
                {
                    context.Response.Headers.RetryAfter = retryAfter;
                }

                return Results.StatusCode((int)status);
            }

            return FailRefresh
                ? Results.Unauthorized()
                : Results.Json(new AuthenticationResponse(RotatedAccessToken, RotatedRefreshToken, DateTime.UtcNow.AddMinutes(15)));
        });

        endpoints.MapFallback(async context =>
        {
            Record(context);
            var bearer = context.Request.Headers.Authorization.ToString();
            if (bearer.StartsWith("Bearer ", StringComparison.Ordinal) && RejectedTokens.ContainsKey(bearer["Bearer ".Length..]))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/auth/revoke")
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await context.Response.WriteAsJsonAsync(new { ok = true, path = context.Request.Path.Value });
        });
    }

    private void Record(HttpContext context)
    {
        var headers = context.Request.Headers;
        Seen.Enqueue(new SeenRequest(
            context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            headers.Authorization.Count == 0 ? null : headers.Authorization.ToString(),
            headers.Cookie.Count == 0 ? null : headers.Cookie.ToString(),
            headers.TryGetValue("X-CSRF", out var csrf) ? csrf.ToString() : null,
            headers.TryGetValue("X-Forwarded-For", out var xff) ? xff.ToString() : null));
    }
}

/// <summary>Builds the UI host under test: session cookies, optionally the proxy, client-config.</summary>
internal static class ProxyHost
{
    public const string WasmApiEndpoint = "https://gateway.example.com";

    public static Task<IHost> StartAsync(
        FakeGateway gateway,
        bool optIn = true,
        IDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? beforeOptIn = null,
        Action<IServiceCollection>? afterOptIn = null,
        Action<IApplicationBuilder>? beforeRouting = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Api:ApiEndpoint"] = "http://gateway",
            ["Api:WasmApiEndpoint"] = WasmApiEndpoint,
        };
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return new HostBuilder()
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment(Environments.Production)
                .ConfigureServices((context, services) =>
                {
                    services.AddRouting();
                    services.Configure<ApiSettings>(context.Configuration.GetSection(ApiSettings.SectionName));
                    services.AddServerAuthSessionCookie("http://gateway/");

                    // The cookie refresher's server-to-server exchange lands on the fake gateway too.
                    services.AddHttpClient("SessionCookieRefreshClient").ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());

                    beforeOptIn?.Invoke(services);
                    if (optIn)
                    {
                        services.AddCommonSameOriginApiProxy(context.Configuration);
                        services.Replace(ServiceDescriptor.Singleton(new SameOriginProxyInvoker(gateway.Server.CreateHandler())));
                    }

                    afterOptIn?.Invoke(services);
                })
                .Configure(app =>
                {
                    beforeRouting?.Invoke(app);
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        if (optIn)
                        {
                            endpoints.MapCommonSameOriginApiProxy();
                        }

                        endpoints.MapSessionCookieEndpoints();
                        endpoints.MapClientConfigEndpoint();
                    });
                }))
            .Build()
            .StartAndReturnAsync();
    }

    public static HttpRequestMessage Request(HttpMethod method, string path, string? accessToken = null, string? refreshToken = null, bool csrf = false)
    {
        var request = new HttpRequestMessage(method, path);
        var cookies = new List<string> { "theme=dark" };
        if (accessToken is not null)
        {
            cookies.Add($"{SessionCookieEndpoints.AccessTokenCookieName}={accessToken}");
        }

        if (refreshToken is not null)
        {
            cookies.Add($"{SessionCookieEndpoints.RefreshTokenCookieName}={refreshToken}");
        }

        request.Headers.Add("Cookie", string.Join("; ", cookies));
        if (csrf)
        {
            request.Headers.Add("X-CSRF", "1");
        }

        return request;
    }

    public static List<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    public static async Task<IHost> StartAndReturnAsync(this IHost host)
    {
        await host.StartAsync();
        return host;
    }
}

/// <summary>Signed test JWTs (the proxy only reads them; the fake gateway never validates them).</summary>
internal static class Jwt
{
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("proxy-tests-signing-key-long-enough-for-hs256"));

    public static string Create(DateTime expires, string subject = "user-42") =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim("sub", subject), new Claim(ClaimTypes.Role, "Member")],
            notBefore: expires.AddHours(-1),
            expires: expires,
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
}
