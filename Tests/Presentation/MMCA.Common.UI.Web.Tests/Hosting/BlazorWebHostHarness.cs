using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MMCA.Common.Aspire.Integrations;
using MMCA.Common.UI.Web.Hosting;

namespace MMCA.Common.UI.Web.Tests.Hosting;

/// <summary>
/// Shared set-up for the composed Blazor web host tests: an in-process <see cref="WebApplicationBuilder"/>
/// on a TestServer, seeded with the two API endpoints every Blazor head configures, and the
/// helpers that read where in the composition a callback ran.
/// </summary>
internal static class BlazorWebHostHarness
{
    /// <summary>The server-side gateway address (what <c>Api:ApiEndpoint</c> holds in a real head).</summary>
    public const string ApiEndpoint = "http://gateway";

    /// <summary>The browser-facing gateway address served to the WASM client.</summary>
    public const string WasmApiEndpoint = "https://gateway.example.com";

    private static readonly Lock ManifestLock = new();

    /// <summary>
    /// An absolute path under the temp folder, so the non-Production file sink never writes into the
    /// test's working directory.
    /// </summary>
    public static string LogFilePath { get; } =
        Path.Combine(Path.GetTempPath(), "mmca-ui-web-tests", "blazor-web-host.txt");

    /// <summary>
    /// Creates a builder on a TestServer with <c>Api:ApiEndpoint</c> and <c>Api:WasmApiEndpoint</c>
    /// set (unless <paramref name="withApiEndpoint"/> is false) plus any extra settings.
    /// </summary>
    public static WebApplicationBuilder CreateBuilder(
        string environmentName = "Production",
        bool withApiEndpoint = true,
        IDictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.WebHost.UseTestServer();

        // A real head registers its UI modules and IHomePageContent AFTER the composed call (rule 15),
        // so the container these tests build is deliberately incomplete. Development turns on
        // ValidateOnBuild, which would then fail on what the app has not added yet rather than on
        // anything the composed call owns.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = false;
            options.ValidateScopes = false;
        });

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (withApiEndpoint)
        {
            values["Api:ApiEndpoint"] = ApiEndpoint;
            values["Api:WasmApiEndpoint"] = WasmApiEndpoint;
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(values);

        // DESIGN-QUESTION: rule 20.20 calls MapStaticAssets() with no manifest path, which throws when
        // {ApplicationName}.staticwebassets.endpoints.json is absent (every non-Web-SDK test host). The
        // harness writes an empty manifest; the alternative is a manifest-path option on the pipeline.
        EnsureStaticAssetsManifest(builder.Environment.ApplicationName);
        return builder;
    }

    /// <summary>
    /// The options every test starts from: only the required log file path, unique per call so two
    /// Development hosts running in parallel never share one rolling file.
    /// </summary>
    public static MmcaBlazorWebHostOptions DefaultOptions() => new()
    {
        LogFilePath = Path.Combine(Path.GetTempPath(), "mmca-ui-web-tests", $"blazor-web-host-{Guid.NewGuid():N}.txt"),
    };

    /// <summary>
    /// Composes the host and the pipeline, lets the test map its own probes after the pipeline (where
    /// a real head maps <c>MapRazorComponents</c>), and starts the TestServer.
    /// </summary>
    public static async Task<WebApplication> StartAsync(
        WebApplicationBuilder builder,
        MmcaBlazorWebHostOptions? hostOptions = null,
        MmcaBlazorWebPipelineOptions? pipelineOptions = null,
        Action<WebApplication>? afterPipeline = null)
    {
        builder.AddMmcaBlazorWebHost(hostOptions ?? DefaultOptions());
        var app = builder.Build();
        app.UseMmcaBlazorWebPipeline(pipelineOptions);
        afterPipeline?.Invoke(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>
    /// True once <c>AddServiceDefaults()</c> has run: it registers this marker as its last step, and
    /// it is what the vendor exporters (Azure Monitor) check for.
    /// </summary>
    public static bool HasServiceDefaultsMarker(IServiceCollection services) =>
        services.Any(descriptor =>
            descriptor.ServiceType == typeof(HostIntegrationMarker)
            && descriptor.ImplementationInstance is HostIntegrationMarker marker
            && string.Equals(marker.Name, HostIntegrationNames.ServiceDefaultsTelemetry, StringComparison.Ordinal));

    /// <summary>
    /// <c>MapStaticAssets()</c> with no argument reads <c>{ApplicationName}.staticwebassets.endpoints.json</c>
    /// from the base directory and throws when it is missing. A test project is not a Web SDK project,
    /// so nothing generates one; an empty manifest maps no asset and lets the pipeline build.
    /// </summary>
    private static void EnsureStaticAssetsManifest(string applicationName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{applicationName}.staticwebassets.endpoints.json");
        lock (ManifestLock)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, """{"Version":1,"ManifestType":"Build","Endpoints":[]}""");
            }
        }
    }
}
