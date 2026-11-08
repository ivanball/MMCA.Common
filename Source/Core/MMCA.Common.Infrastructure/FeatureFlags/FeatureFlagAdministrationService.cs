using Microsoft.Extensions.Logging;
using Microsoft.FeatureManagement;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;

namespace MMCA.Common.Infrastructure.FeatureFlags;

/// <summary>
/// The shipped <see cref="IFeatureFlagAdministrationService"/> (ADR-031): the declared-flag check, the
/// store write, and the snapshot invalidation that makes the write visible to the next evaluation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared flags only.</b> A name outside <see cref="FeatureFlagCatalog"/> is refused with
/// <c>FeatureFlag.Undeclared</c> before anything is written, including a name configuration happens to
/// carry: an override must name a flag some code reads, or it is a typo stored as data.
/// </para>
/// <para>
/// <b>The invalidation never fails a committed write.</b> A cache fault after the row is saved is
/// logged and the call still succeeds; the entry lifetime bounds how long the old snapshot can be
/// served (the caching command decorator's posture).
/// </para>
/// </remarks>
/// <param name="catalog">The declared flags.</param>
/// <param name="store">The override store.</param>
/// <param name="snapshot">The cached snapshot every evaluation reads.</param>
/// <param name="definitions">The registered definition provider (the layered one).</param>
/// <param name="logger">Receives the invalidation warning.</param>
internal sealed partial class FeatureFlagAdministrationService(
    FeatureFlagCatalog catalog,
    IFeatureFlagOverrideStore store,
    IFeatureFlagOverrideSnapshot snapshot,
    IFeatureDefinitionProvider definitions,
    ILogger<FeatureFlagAdministrationService> logger) : IFeatureFlagAdministrationService
{
    /// <summary>
    /// The provider configuration alone answers from: the one the stored layer sits over, or the
    /// registered provider itself when something else wrapped it.
    /// </summary>
    private IFeatureDefinitionProvider ConfiguredProvider =>
        definitions is StoredOverrideFeatureDefinitionProvider layered ? layered.Configured : definitions;

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<FeatureFlagStatusResponse>>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Read from the store, not the snapshot: an operator looking at this screen must see the rows
        // as they are, not as one replica's cache last saw them.
        var overrides = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var flags = new List<FeatureFlagStatusResponse>(catalog.Flags.Count);
        foreach (var flag in catalog.Flags)
        {
            var stored = overrides.FirstOrDefault(o => string.Equals(o.Name, flag.FlagName, StringComparison.OrdinalIgnoreCase));
            flags.Add(await DescribeAsync(flag, stored).ConfigureAwait(false));
        }

        return Result.Success<IReadOnlyList<FeatureFlagStatusResponse>>(flags);
    }

    /// <inheritdoc />
    public async Task<Result<FeatureFlagStatusResponse>> SetOverrideAsync(
        string name,
        SetFeatureFlagOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!catalog.TryFind(name, out var flag))
        {
            return Result.Failure<FeatureFlagStatusResponse>(Undeclared());
        }

        var stored = await store.SetAsync(flag.FlagName, request, cancellationToken).ConfigureAwait(false);
        if (!stored.TryGetValue(out var value))
        {
            return Result.Failure<FeatureFlagStatusResponse>(stored.Errors);
        }

        await InvalidateAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success(await DescribeAsync(flag, value).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<Result> ClearOverrideAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!catalog.TryFind(name, out var flag))
        {
            return Result.Failure(Undeclared());
        }

        var cleared = await store.ClearAsync(flag.FlagName, cancellationToken).ConfigureAwait(false);
        if (cleared.IsFailure)
        {
            return cleared;
        }

        await InvalidateAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private static Error Undeclared() =>
        Error.NotFoundError(
            "FeatureFlag.Undeclared",
            "No feature flag with that name is declared by this service.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "The feature-flag override snapshot could not be invalidated after a committed write; other evaluations may serve the previous snapshot until it expires.")]
    private static partial void LogInvalidationFailed(ILogger logger, Exception exception);

    private async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await snapshot.InvalidateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInvalidationFailed(logger, ex);
        }
    }

    private async Task<FeatureFlagStatusResponse> DescribeAsync(FeatureFlagDescriptor flag, FeatureFlagOverrideResponse? stored)
    {
        var configured = FeatureFlagDefinitions.Describe(
            await ConfiguredProvider.GetFeatureDefinitionAsync(flag.FlagName).ConfigureAwait(false));

        var effective = stored is null
            ? configured.Kind
            : FeatureFlagDefinitions.Describe(FeatureFlagDefinitions.ToDefinition(stored)).Kind;

        return new FeatureFlagStatusResponse(
            flag.FlagName,
            flag.Lifetime,
            flag.Owner,
            flag.RemoveBy,
            configured.Kind,
            configured.Filters,
            stored,
            effective);
    }
}
