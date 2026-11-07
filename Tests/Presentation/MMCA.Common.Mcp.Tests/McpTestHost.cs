using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization;
using MMCA.Common.API.RateLimiting;
using MMCA.Common.API.Startup;
using ModelContextProtocol.Client;

namespace MMCA.Common.Mcp.Tests;

/// <summary>
/// A real ASP.NET Core host on TestServer with the framework's authorization model (fallback policy
/// ON, so an endpoint without explicit anonymous metadata would demand a token) and rate limiting,
/// opted into MCP with the two consumer calls.
/// </summary>
internal static class McpTestHost
{
    public static async Task<WebApplication> StartAsync(
        IDictionary<string, string?> configuration,
        Action<CommonMcpOptions>? configureTools = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(configuration);

        builder.Services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, configureOptions: null);
        builder.Services.AddAuthorizationPolicies();
        builder.Services.AddCommonRateLimiting(new RateLimitingSettings());
        configureServices?.Invoke(builder.Services);

        builder.AddCommonMcpServer(configureTools ?? (options => options.ToolTypes.Add(typeof(SampleTools))));

        var app = builder.Build();
        try
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();
            app.MapCommonMcp();

            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public static Task<McpClient> ConnectAsync(WebApplication app, string? permissions = null)
    {
        var httpClient = app.GetTestClient();
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, McpHostingExtensions.DefaultPath),
            TransportMode = HttpTransportMode.StreamableHttp,
        };

        if (permissions is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TestAuthHandler.HeaderName] = permissions,
            };
        }

        var transport = new HttpClientTransport(options, httpClient, loggerFactory: null, ownsHttpClient: true);
        return McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    public static Dictionary<string, string?> Enabled(params (string Key, string Value)[] extra)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal) { ["Mcp:Enabled"] = "true" };
        foreach (var (key, value) in extra)
        {
            configuration[key] = value;
        }

        return configuration;
    }
}
