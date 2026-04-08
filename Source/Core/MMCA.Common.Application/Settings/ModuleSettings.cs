namespace MMCA.Common.Application.Settings;

/// <summary>
/// Per-module configuration settings.
/// </summary>
public sealed class ModuleSettings
{
    /// <summary>Whether this module should be registered and activated at startup. Defaults to <see langword="true"/>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Names of declared <c>IModule.Dependencies</c> that are satisfied externally —
    /// typically by an extracted microservice talking to this host over gRPC or a message
    /// broker. Used by the module loader to skip the strict <c>IModule.RequiresDependencies</c>
    /// check for those names: a dependency listed here is treated as satisfied even when its
    /// module is disabled in-process.
    /// <para>
    /// The host is responsible for actually wiring the cross-process replacement (e.g.,
    /// <c>services.AddCatalogProductVariantClient()</c> after <c>ModuleLoader.DiscoverAndRegister</c>
    /// returns). The disabled module's <c>RegisterDisabledStubs</c> still runs first so the
    /// contract type is in DI; the host then <c>Replace</c>s it with the real adapter.
    /// </para>
    /// <para>
    /// Example monolith config after Catalog has been extracted as a separate service:
    /// <code>
    /// "Modules": {
    ///   "Catalog": { "Enabled": false },
    ///   "Identity": { "Enabled": true },
    ///   "Sales": {
    ///     "Enabled": true,
    ///     "RemoteDependencies": [ "Catalog" ]
    ///   }
    /// }
    /// </code>
    /// </para>
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only - required for IConfiguration binding
    public List<string> RemoteDependencies { get; set; } = [];
#pragma warning restore CA2227
}
