namespace MMCA.Common.Infrastructure.Configuration;

/// <summary>
/// An empty options type whose only job is to carry <see cref="CloudAdapterTripwire"/> through
/// <c>ValidateOnStart</c>, so the check runs with the other startup validators, before any hosted
/// service starts.
/// </summary>
internal sealed class CloudAdapterTripwireOptions;
