using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace MMCA.Common.API.Authorization;

/// <summary>
/// Lazily materializes an <see cref="AuthorizationPolicy"/> for any policy name prefixed with
/// <see cref="PermissionPolicy.Prefix"/>, attaching a <see cref="PermissionRequirement"/> for the
/// permission encoded in the name. This removes the need to pre-register a named policy per
/// permission. Every other policy name falls through to the default provider, so the named role
/// policies in <see cref="AuthorizationPolicies"/> keep working unchanged.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallbackPolicyProvider;

    /// <summary>Initializes the provider, delegating non-permission policies to the default provider.</summary>
    /// <param name="options">The authorization options.</param>
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) =>
        _fallbackPolicyProvider = new DefaultAuthorizationPolicyProvider(options);

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
        _fallbackPolicyProvider.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
        _fallbackPolicyProvider.GetFallbackPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        if (!policyName.StartsWith(PermissionPolicy.Prefix, StringComparison.Ordinal))
        {
            return _fallbackPolicyProvider.GetPolicyAsync(policyName);
        }

        var permission = policyName[PermissionPolicy.Prefix.Length..];
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}
