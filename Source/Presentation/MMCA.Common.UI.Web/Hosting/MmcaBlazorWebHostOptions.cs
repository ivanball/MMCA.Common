using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Authorization.Fallback;
using MudBlazor.Services;

namespace MMCA.Common.UI.Web.Hosting;

/// <summary>
/// Options for <c>AddMmcaBlazorWebHost</c>: the values and callbacks that differ between Blazor heads.
/// Everything else the composed call registers is shared and fixed.
/// </summary>
public sealed class MmcaBlazorWebHostOptions
{
    /// <summary>
    /// The rolling log file path passed to <c>AddCommonSerilog</c> (for example
    /// <c>logs/MMCAStoreUIWeb.txt</c>); the file sink is skipped in Production.
    /// </summary>
    public required string LogFilePath { get; init; }

    /// <summary>
    /// Adds configuration sources (Key Vault, for example). Runs FIRST, before any configuration is read,
    /// so every value it adds is seen by the service defaults and by the composed call's own reads.
    /// </summary>
    public Action<WebApplicationBuilder>? ConfigureConfigurationSources { get; init; }

    /// <summary>
    /// Runs immediately after <c>AddServiceDefaults()</c>: the place for vendor exporters that must follow
    /// it (the Azure Monitor exporter) and the shared DataProtection key ring.
    /// </summary>
    public Action<WebApplicationBuilder>? AfterServiceDefaults { get; init; }

    /// <summary>
    /// Runs on the Razor components builder immediately after the interactive server components are
    /// added: a head with a WebAssembly client passes <c>rc =&gt; rc.AddInteractiveWebAssemblyComponents()</c>.
    /// </summary>
    public Action<IRazorComponentsBuilder>? ConfigureRazorComponents { get; init; }

    /// <summary>
    /// Configures MudBlazor (the snackbar position, for example); <see langword="null"/> registers it with
    /// its defaults.
    /// </summary>
    public Action<MudServicesConfiguration>? ConfigureMudServices { get; init; }

    /// <summary>
    /// Extends the fallback authorization policy (extra exempt path prefixes for the head's own static
    /// files, for example); <see langword="null"/> keeps the framework defaults.
    /// </summary>
    public Action<FallbackAuthorizationOptions>? ConfigureFallbackAuthorization { get; init; }
}
