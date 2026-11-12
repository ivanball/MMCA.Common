using System.Runtime.CompilerServices;

// Key Vault configuration, the Azure Monitor exporter and the DataProtection key ring moved to
// MMCA.Common.Azure.Startup (same namespace). Forwarding them keeps a host compiled against an older
// MMCA.Common.Azure binary resolving all three without a rebuild.
[assembly: TypeForwardedTo(typeof(MMCA.Common.Azure.AzureKeyVaultConfigurationExtensions))]
[assembly: TypeForwardedTo(typeof(MMCA.Common.Azure.AzureMonitorExporterExtensions))]
[assembly: TypeForwardedTo(typeof(MMCA.Common.Azure.AzureDataProtectionExtensions))]
