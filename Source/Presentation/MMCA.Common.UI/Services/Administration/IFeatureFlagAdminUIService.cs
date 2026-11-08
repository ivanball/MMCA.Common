using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// The UI's view of the feature-flag administration endpoints <c>FeatureFlagsControllerBase</c> serves
/// (ADR-031). Every member names the flag SOURCE it calls: the API route prefix of the service that
/// owns the flag, so one page can list and edit the flags of every service in a multi-service host.
/// Registered by <c>AddFeatureFlagAdministrationUI</c>.
/// </summary>
/// <remarks>
/// Every member answers with a <see cref="Result"/> carrying the API's own errors and their
/// <see cref="ErrorType"/> intact, so the page branches on the outcome instead of catching.
/// </remarks>
public interface IFeatureFlagAdminUIService
{
    /// <summary>Reads the flags one source declares, from <c>GET {source}</c>.</summary>
    /// <param name="source">The API route prefix of the owning service, e.g. <c>Admin/FeatureFlags</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flags, or the API's own refusal.</returns>
    Task<Result<IReadOnlyList<FeatureFlagStatusResponse>>> GetAllAsync(
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>Sets one flag's override, through <c>PUT {source}/{name}</c>.</summary>
    /// <param name="source">The API route prefix of the owning service.</param>
    /// <param name="name">The flag name.</param>
    /// <param name="request">The override to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The flag's state afterwards, or the API's own refusal.</returns>
    Task<Result<FeatureFlagStatusResponse>> SetOverrideAsync(
        string source,
        string name,
        SetFeatureFlagOverrideRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Clears one flag's override, through <c>DELETE {source}/{name}</c>.</summary>
    /// <param name="source">The API route prefix of the owning service.</param>
    /// <param name="name">The flag name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, or the API's own refusal.</returns>
    Task<Result> ClearOverrideAsync(string source, string name, CancellationToken cancellationToken = default);
}
