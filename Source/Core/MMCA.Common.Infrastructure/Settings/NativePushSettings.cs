namespace MMCA.Common.Infrastructure.Settings;

/// <summary>
/// Native push delivery settings bound from the <c>NativePush</c> configuration section
/// (ADR-044). Deployments provision the hub with <see cref="Enabled"/> <see langword="false"/>
/// until platform credentials (FCM v1 service account, APNs auth key) are uploaded to it, so
/// the pipeline ships inert and is switched on by configuration alone.
/// </summary>
public sealed class NativePushSettings
{
    /// <summary>Configuration section name used for options binding.</summary>
    public static readonly string SectionName = "NativePush";

    /// <summary>Whether native push delivery is active.</summary>
    public bool Enabled { get; init; }

    /// <summary>Azure Notification Hubs connection string (a Listen+Send+Manage rule).</summary>
    public string? ConnectionString { get; init; }

    /// <summary>The notification hub name within the namespace.</summary>
    public string? HubName { get; init; }
}
