namespace MMCA.Common.Aspire.Integrations;

/// <summary>
/// The well-known <see cref="HostIntegrationMarker"/> names. The core registers
/// <see cref="ServiceDefaultsTelemetry"/> itself; the others are registered by the adapter call that
/// owns them and checked at startup against the configuration that needs them.
/// </summary>
public static class HostIntegrationNames
{
    /// <summary>
    /// Registered by <c>ConfigureOpenTelemetry()</c> (and so by <c>AddServiceDefaults()</c>), which
    /// installs the trace filter processors a vendor exporter must be registered after.
    /// </summary>
    public const string ServiceDefaultsTelemetry = "ServiceDefaultsTelemetry";

    /// <summary>Registered by <c>AddAzureMonitorExporter()</c> (MMCA.Common.Azure).</summary>
    public const string AzureMonitorExporter = "AzureMonitorExporter";

    /// <summary>Registered by <c>AddAzureKeyVaultConfiguration()</c> (MMCA.Common.Azure).</summary>
    public const string AzureKeyVaultConfiguration = "AzureKeyVaultConfiguration";

    /// <summary>Registered by <c>AddAzureDataProtection()</c> (MMCA.Common.Azure).</summary>
    public const string AzureDataProtection = "AzureDataProtection";
}
