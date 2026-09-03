using Microsoft.Extensions.Logging;
using MMCA.Common.Shared.Auth.Permissions;

namespace MMCA.Common.Application.Auth;

/// <summary>
/// Fallback <see cref="IPermissionRegistry"/> for a host that wired the CQRS pipeline without
/// declaring any role to permission grants. It grants nothing, so an
/// <see cref="UseCases.Markers.IRequiresPermission"/> command or query is denied rather than allowed, and it
/// says so once in the log naming the call that would fix it.
/// <para>
/// Registered by <c>AddApplicationDecorators()</c> with <c>TryAdd</c>, so a host that calls
/// <c>AddAuthorizationPolicies()</c> or <c>AddPermissions(...)</c> keeps its own registry and this
/// type is never constructed. Without it the authorization decorators, which are registered
/// unconditionally, made every request through the pipeline fail to activate: a small app with no
/// Identity module answered 500 on every read, not only on the permission-gated ones.
/// </para>
/// </summary>
/// <param name="logger">Logger for the one-time misconfiguration warning.</param>
internal sealed partial class UnconfiguredPermissionRegistry(ILogger<UnconfiguredPermissionRegistry> logger)
    : IPermissionRegistry
{
    private static readonly HashSet<string> None = [];

    private int _warned;

    /// <inheritdoc />
    public IReadOnlySet<string> GetPermissions(string role)
    {
        WarnOnce();
        return None;
    }

    /// <inheritdoc />
    public bool HasPermission(IEnumerable<string> roles, string permission)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        WarnOnce();
        return false;
    }

    /// <summary>
    /// Logs the misconfiguration the first time a permission is actually checked. Deferred to the
    /// check rather than logged at startup because a host with no permission-gated request is
    /// correctly configured: it simply never needs a registry.
    /// </summary>
    private void WarnOnce()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            LogNoPermissionsConfigured(logger);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "No IPermissionRegistry is registered, so every permission check is denied. Call AddAuthorizationPolicies() (or AddPermissions(...)) before AddApplicationDecorators() to declare the role to permission grants this host expects.")]
    private static partial void LogNoPermissionsConfigured(ILogger logger);
}
