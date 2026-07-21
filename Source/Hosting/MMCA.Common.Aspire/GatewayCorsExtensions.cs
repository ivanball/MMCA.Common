using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MMCA.Common.Aspire;

/// <summary>
/// Registration extension for the shared gateway CORS policy. A reverse-proxy gateway must pass
/// arbitrary client headers through to the services it fronts, so — unlike
/// <c>MMCA.Common.API.AddCommonCors</c>'s allow-listed headers/methods for a service host — the
/// production gateway policy allows any header/method while restricting <b>origins</b> to
/// <c>Cors:AllowedOrigins</c> so credentials (cookies / Authorization headers) can flow safely.
/// Development allows any origin. Registered as the <b>default</b> policy: pair with a bare
/// <c>app.UseCors()</c>.
/// </summary>
public static class GatewayCorsExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the default gateway CORS policy: allow-any in Development; in other environments,
        /// origins from <c>Cors:AllowedOrigins</c> with any header/method and credentials allowed.
        /// </summary>
        public IServiceCollection AddCommonGatewayCors(
            IConfiguration configuration,
            IHostEnvironment environment)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);
            ArgumentNullException.ThrowIfNull(environment);

            services.AddCors(options =>
            {
                if (environment.IsDevelopment())
                {
#pragma warning disable S5122 // Allow-any-origin is scoped to Development only; production restricts origins to Cors:AllowedOrigins below
                    options.AddDefaultPolicy(p => p
                        .AllowAnyOrigin()
                        .AllowAnyHeader()
                        .AllowAnyMethod());
#pragma warning restore S5122
                }
                else
                {
                    var origins = configuration
                        .GetSection("Cors:AllowedOrigins")
                        .Get<string[]>() ?? [];
                    options.AddDefaultPolicy(p => p
                        .WithOrigins(origins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials());
                }
            });

            return services;
        }
    }
}
