using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace MMCA.Common.API.Authorization.MultiFactor;

/// <summary>
/// Chains <see cref="MultiFactorRoleWithholding"/> onto the <c>OnTokenValidated</c> event of every JWT
/// bearer scheme, after whatever handler the framework or the host already set there, so the validated
/// identity is changed in place before the authentication ticket is built and every later reader of the
/// request sees the same principal.
/// </summary>
/// <remarks>
/// A post-configure step, so it runs after every <c>AddJwtBearer</c> configuration delegate, including
/// the ones that assign <see cref="JwtBearerOptions.Events"/> (the framework's hub access-token events).
/// Registered once by <c>AddAuthorizationPolicies</c>, and the withholding reads the role list per
/// request, so with an empty list it returns before any work. A non-bearer scheme (a cookie session) is
/// not touched.
/// </remarks>
internal sealed class MultiFactorBearerWithholding : IPostConfigureOptions<JwtBearerOptions>
{
    /// <inheritdoc />
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Events ??= new JwtBearerEvents();
        var previous = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            if (previous is not null)
            {
                await previous(context).ConfigureAwait(false);
            }

            MultiFactorRoleWithholding.Apply(context.Principal, context.HttpContext.RequestServices);
        };
    }
}
