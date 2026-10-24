namespace MMCA.Common.Aspire.Integrations;

/// <summary>
/// An empty options type whose only job is to carry <see cref="HostIntegrationTripwire"/> through
/// <c>ValidateOnStart</c>, so the check runs with the other startup validators, before any hosted
/// service starts.
/// </summary>
internal sealed class HostIntegrationTripwireOptions;
