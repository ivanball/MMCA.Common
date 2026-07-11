namespace MMCA.Common.UI.Services.Capabilities.Fallbacks;

/// <summary>
/// Token provider that never produces a token (ADR-044). The default everywhere, including on
/// native heads until the app registers a credentialed provider (Firebase / APNs) - with it in
/// place the registration pipeline is wired but inert, which is exactly the state a build
/// without push credentials should be in.
/// </summary>
public sealed class NullPushDeviceTokenProvider : IPushDeviceTokenProvider
{
    /// <inheritdoc />
    public Task<PushDeviceToken?> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<PushDeviceToken?>(null);
}
