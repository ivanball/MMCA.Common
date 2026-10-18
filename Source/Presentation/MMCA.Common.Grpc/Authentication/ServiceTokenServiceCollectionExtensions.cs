using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MMCA.Common.Grpc.Authentication;

/// <summary>
/// Registration for service-to-service tokens: <c>AddServiceTokenClient</c> on the calling side and
/// <c>AddServiceTokenAuthentication</c> on the receiving side. Both bind
/// <see cref="ServiceTokenSettings"/> from <see cref="ServiceTokenSettings.SectionName"/> and
/// validate it on start.
/// </summary>
/// <remarks>
/// The two halves are independent opt-ins. Registering the provider sends nothing by itself: each
/// typed client that should carry the token opts in with
/// <see cref="ServiceTokenHttpClientBuilderExtensions"/>' <c>WithServiceToken()</c>, and every other
/// client keeps forwarding only the user's token.
/// </remarks>
public static class ServiceTokenServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="IServiceTokenProvider"/> as a singleton (one token cache per process)
        /// for the typed gRPC clients that opted in with <c>WithServiceToken()</c>, which attach a
        /// service token to a call that has no user token to forward. Also adds
        /// <see cref="TimeProvider.System"/> unless the host already registered a clock.
        /// </summary>
        /// <param name="configuration">The application configuration holding the <c>Grpc:ServiceToken</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddServiceTokenClient(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddServiceTokenSettings(configuration);
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IServiceTokenProvider, ServiceTokenProvider>();
            return services;
        }

        /// <summary>
        /// Adds the <see cref="ServiceTokenDefaults.AuthenticationScheme"/> JwtBearer scheme and the
        /// <see cref="ServiceTokenDefaults.InternalCallerPolicy"/> authorization policy. The scheme
        /// validates issuer, audience, lifetime and signature against the shared key, accepts HS256
        /// only, and refuses a token without the <see cref="ServiceTokenDefaults.ServiceClaimType"/>
        /// claim. The policy accepts an authenticated caller from either the user scheme
        /// (<see cref="JwtBearerDefaults.AuthenticationScheme"/>) or the service scheme.
        /// <para>
        /// The service scheme is never made the default, so a service token satisfies only an
        /// endpoint that names <see cref="ServiceTokenDefaults.InternalCallerPolicy"/> (or the scheme
        /// itself); a plain <c>[Authorize]</c> endpoint still authenticates the user scheme alone.
        /// Register the user scheme as well (<c>AddCommonAuthentication</c> or
        /// <c>AddForwardedJwtBearer</c>): ASP.NET Core falls back to a lone registered scheme as the
        /// default, so a host where the service scheme would end up the default fails at startup.
        /// </para>
        /// </summary>
        /// <param name="configuration">The application configuration holding the <c>Grpc:ServiceToken</c> section.</param>
        /// <returns>The service collection for chaining.</returns>
        public IServiceCollection AddServiceTokenAuthentication(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            services.AddServiceTokenSettings(configuration);

            // AddAuthentication() with no argument leaves the default scheme to whoever set it.
            services.AddAuthentication().AddJwtBearer(ServiceTokenDefaults.AuthenticationScheme);
            services.AddOptions<JwtBearerOptions>(ServiceTokenDefaults.AuthenticationScheme)
                .Configure<IOptions<ServiceTokenSettings>>((options, settings) => ConfigureServiceTokenBearer(options, settings.Value));

            // Fail the host rather than document the trap: a service token that became the default
            // scheme would satisfy every plain [Authorize] endpoint.
            // A marker type carries the check so it runs once at startup and reading
            // AuthenticationOptions stays free of side effects.
            services.AddOptions<DefaultSchemeGuard>()
                .Validate<IOptions<AuthenticationOptions>>(
                    (_, authentication) => ServiceTokenIsNotTheDefaultScheme(authentication.Value),
                    $"The '{ServiceTokenDefaults.AuthenticationScheme}' scheme must not be the default authentication scheme, explicitly or as the only registered scheme. Register the user bearer scheme as the default (AddCommonAuthentication or AddForwardedJwtBearer).")
                .ValidateOnStart();

            services.AddAuthorizationBuilder()
                .AddPolicy(ServiceTokenDefaults.InternalCallerPolicy, policy => policy
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, ServiceTokenDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser());

            return services;
        }

        /// <summary>
        /// Binds and validates <see cref="ServiceTokenSettings"/> once, however many of the two
        /// registrations a host calls, so a bad section reports each problem once.
        /// </summary>
        private void AddServiceTokenSettings(IConfiguration configuration)
        {
            if (services.Any(d => !d.IsKeyedService && d.ImplementationInstance is DataAnnotationValidateOptions<ServiceTokenSettings>))
            {
                return;
            }

            services.AddOptions<ServiceTokenSettings>()
                .Bind(configuration.GetSection(ServiceTokenSettings.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();
        }
    }

    private static bool ServiceTokenIsNotTheDefaultScheme(AuthenticationOptions options)
    {
        string?[] defaults =
        [
            options.DefaultScheme,
            options.DefaultAuthenticateScheme,
            options.DefaultChallengeScheme,
            options.DefaultForbidScheme,
        ];
        if (defaults.Contains(ServiceTokenDefaults.AuthenticationScheme, StringComparer.Ordinal))
        {
            return false;
        }

        // ASP.NET Core promotes a lone registered scheme to the default when none is set.
        var schemes = options.Schemes.Select(s => s.Name).ToList();
        return !(options.DefaultScheme is null
            && options.DefaultAuthenticateScheme is null
            && schemes is [ServiceTokenDefaults.AuthenticationScheme]);
    }

    private static void ConfigureServiceTokenBearer(JwtBearerOptions options, ServiceTokenSettings settings)
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(settings.SigningKey)),

            // Pinned so an algorithm-confusion swap cannot pass a token signed some other way.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };

        options.Events = new JwtBearerEvents
        {
            // The svc claim is what makes a token a service token; one that validates without it
            // was minted for something else and is refused here.
            OnTokenValidated = context =>
            {
                var serviceName = context.Principal?.FindFirst(ServiceTokenDefaults.ServiceClaimType)?.Value;
                if (string.IsNullOrWhiteSpace(serviceName))
                {
                    context.Fail($"A service token must carry the '{ServiceTokenDefaults.ServiceClaimType}' claim.");
                }

                return Task.CompletedTask;
            },
        };
    }

    /// <summary>Marker options whose start-time validation runs the default-scheme guard.</summary>
    private sealed class DefaultSchemeGuard;
}
