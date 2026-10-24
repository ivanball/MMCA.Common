namespace MMCA.Common.Aspire.Integrations;

/// <summary>
/// A registration receipt: an adapter package's host-level registration (a configuration source, an
/// exporter, a key-ring sink) adds one of these to the service collection so the core packages can
/// tell, at startup, that the call was made. The core never names an adapter type, so a marker
/// carrying a well-known <see cref="Name"/> (see <see cref="HostIntegrationNames"/>) is how it checks.
/// </summary>
/// <param name="name">The integration name, one of the <see cref="HostIntegrationNames"/> constants.</param>
public sealed class HostIntegrationMarker(string name)
{
    /// <summary>Gets the integration name.</summary>
    public string Name { get; } = name;
}
