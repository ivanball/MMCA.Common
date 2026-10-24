using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMCA.Common.Aspire.Integrations;

/// <summary>
/// Fails host startup when the configuration asks for a vendor integration whose adapter call was
/// never made. The core packages are cloud-neutral, so a deployment that still sets, say,
/// <c>KeyVault:Uri</c> but no longer calls <c>AddAzureKeyVaultConfiguration()</c> would otherwise
/// start cleanly with every vault secret missing, or ship no telemetry at all, and look healthy while
/// doing it. Each check names the call that is missing.
/// </summary>
/// <param name="configuration">The host configuration.</param>
/// <param name="markers">The registration receipts adapter calls left behind.</param>
internal sealed class HostIntegrationTripwire(
    IConfiguration configuration,
    IEnumerable<HostIntegrationMarker> markers) : IValidateOptions<HostIntegrationTripwireOptions>
{
    /// <summary>The configuration key whose presence requires the Azure Monitor exporter.</summary>
    internal const string ApplicationInsightsKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>The configuration key whose presence requires the Key Vault configuration source.</summary>
    internal const string KeyVaultUriKey = "KeyVault:Uri";

    /// <summary>The configuration key whose presence requires the Azure DataProtection key ring.</summary>
    internal const string DataProtectionBlobStorageUriKey = "DataProtection:BlobStorageUri";

    private readonly HashSet<string> _registered = [.. markers.Select(marker => marker.Name)];

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HostIntegrationTripwireOptions options)
    {
        List<string> failures = [];

        Check(
            ApplicationInsightsKey,
            HostIntegrationNames.AzureMonitorExporter,
            "no Azure Monitor exporter is registered, so no log, metric or trace reaches Application Insights. Reference MMCA.Common.Azure and call builder.AddAzureMonitorExporter() after AddServiceDefaults()",
            failures);
        Check(
            KeyVaultUriKey,
            HostIntegrationNames.AzureKeyVaultConfiguration,
            "no Key Vault configuration source was added, so every secret the vault holds is missing from configuration. Reference MMCA.Common.Azure and call builder.AddAzureKeyVaultConfiguration() early in Program.cs",
            failures);
        Check(
            DataProtectionBlobStorageUriKey,
            HostIntegrationNames.AzureDataProtection,
            "the Azure DataProtection key ring was not registered, so each replica keeps its own in-memory keys and cookies minted by one cannot be read by another. Reference MMCA.Common.Azure and call builder.AddAzureDataProtection()",
            failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void Check(string key, string integration, string consequence, List<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(configuration[key]) && !_registered.Contains(integration))
        {
            failures.Add($"{key} is set, but {consequence}.");
        }
    }
}
