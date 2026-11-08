namespace MMCA.Common.Application.Auth.MachineClients;

/// <summary>
/// Configuration for machine clients (OAuth client credentials, ADR-140). Bound from the
/// <c>MachineClients</c> section by <c>AddMachineClients(configuration)</c> and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> While <see cref="Enabled"/> is false the token endpoint and every
/// administration endpoint answer 404, and the <c>MachineClients</c> table is in no model, so a host
/// that never opts in sees no route and no migration. A host that does not call
/// <c>AddMachineClients</c> at all behaves the same way.
/// </para>
/// <para>
/// <b>Turning it on needs consumer code:</b> an <see cref="IServiceAccountProvisioner"/> over the app's
/// own <c>User</c> aggregate. An enabled host without one fails at startup naming the registration.
/// </para>
/// </remarks>
public sealed class MachineClientSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MachineClients";

    /// <summary>The shortest machine-token lifetime accepted, in minutes.</summary>
    public const int MinAccessTokenMinutes = 1;

    /// <summary>
    /// The longest machine-token lifetime accepted, in minutes. Revocation works by writing the
    /// soft-deleted marker for the service account, which lives 20 minutes; a token must lapse within
    /// that window minus the bearer handler's 5-minute clock skew, or a revoked client's token would
    /// outlive the marker.
    /// </summary>
    public const int MaxAccessTokenMinutes = 15;

    /// <summary>Gets a value indicating whether machine clients are available on this host. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the lifetime of a machine access token in minutes, from <see cref="MinAccessTokenMinutes"/>
    /// to <see cref="MaxAccessTokenMinutes"/>. Defaults to 15. Nothing renews it: a program asks again.
    /// </summary>
    public int AccessTokenMinutes { get; init; } = MaxAccessTokenMinutes;

    /// <summary>
    /// Gets the logical data source that owns the <c>MachineClients</c> table: the Identity database.
    /// Defaults to the engine's default source, which is right for a single-database host.
    /// </summary>
    public string DataSourceName { get; init; } = "Default";
}
