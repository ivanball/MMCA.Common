namespace MMCA.Common.Application.Auth.Passkeys;

/// <summary>
/// Configuration for passkey sign-in (WebAuthn, ADR-139). Bound from the <c>Passkeys</c> section by
/// <c>AddPasskeys(configuration)</c> and validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off by default.</b> While <see cref="Enabled"/> is false every passkey endpoint answers 404 and
/// the <c>PasskeyCredentials</c> table is in no model, so a host that never opts in sees no route and
/// no migration. A host that does not call <c>AddPasskeys</c> at all behaves the same way.
/// </para>
/// <para>
/// <b>Passkeys are bound to the relying party id.</b> A credential registered under one
/// <see cref="RelyingPartyId"/> is never offered for another, so changing the domain orphans every
/// passkey; settle the production domain before turning the feature on.
/// </para>
/// </remarks>
public sealed class PasskeySettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Passkeys";

    /// <summary>Gets a value indicating whether passkeys are available on this host. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the WebAuthn relying party id: the registrable domain the passkeys belong to (for example
    /// <c>example.com</c>, which also covers <c>app.example.com</c>). Required when enabled.
    /// </summary>
    public string? RelyingPartyId { get; init; }

    /// <summary>Gets the name the authenticator shows for the relying party. Required when enabled.</summary>
    public string? RelyingPartyName { get; init; }

    /// <summary>
    /// Gets the exact origins (scheme, host and port) the browser ceremonies may run on, for example
    /// <c>https://app.example.com</c>. At least one is required when enabled.
    /// </summary>
    public IReadOnlyList<string> Origins { get; init; } = [];

    /// <summary>
    /// Gets the logical data source that owns the <c>PasskeyCredentials</c> table: the Identity
    /// database. Defaults to the engine's default source, which is right for a single-database host.
    /// </summary>
    public string DataSourceName { get; init; } = "Default";
}
