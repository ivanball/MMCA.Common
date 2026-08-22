using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MMCA.Common.API.Startup;

namespace MMCA.Common.API.Tests.OpenApi;

/// <summary>
/// Boots a started in-memory <see cref="WebApplication"/> over the real MVC + <c>AddCommonApiVersioning</c>
/// + <c>AddCommonOpenApi</c> + <c>MapCommonOpenApi</c> pipeline, so <c>/openapi/v1.json</c> is served by the
/// same code path a service host runs. Controller discovery is restricted to an explicit probe list: the
/// default feature providers would otherwise sweep in every controller in the test assembly (and in
/// MMCA.Common.API itself), which would make each caller's document depend on unrelated test files.
/// </summary>
internal static class OpenApiProbeHost
{
    /// <summary>
    /// Creates and starts a host whose only controllers are <paramref name="controllerTypes"/>.
    /// The caller owns the returned application and must dispose it (<c>await using</c>).
    /// </summary>
    /// <param name="controllerTypes">The probe controllers this host should describe.</param>
    public static async Task<WebApplication> CreateAsync(params Type[] controllerTypes)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddControllers();
        builder.Services.AddCommonApiVersioning();
        builder.Services.AddCommonOpenApi();

        // Restricting discovery has to happen AFTER the framework registrations: the API-versioning
        // builder runs AddMvcCore internally, which puts MVC's default ControllerFeatureProvider back
        // whenever it is absent, so a strip done earlier is silently undone.
        builder.Services.AddControllers().ConfigureApplicationPartManager(manager =>
        {
            for (int i = manager.FeatureProviders.Count - 1; i >= 0; i--)
            {
                if (manager.FeatureProviders[i] is IApplicationFeatureProvider<ControllerFeature>)
                {
                    manager.FeatureProviders.RemoveAt(i);
                }
            }

            manager.FeatureProviders.Add(new ProbeControllerFeatureProvider(controllerTypes));
        });

        WebApplication app = builder.Build();
        app.MapControllers();
        app.MapCommonOpenApi();
        await app.StartAsync();

        return app;
    }

    private sealed class ProbeControllerFeatureProvider(Type[] controllerTypes)
        : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (Type controllerType in controllerTypes)
            {
                feature.Controllers.Add(controllerType.GetTypeInfo());
            }
        }
    }
}
