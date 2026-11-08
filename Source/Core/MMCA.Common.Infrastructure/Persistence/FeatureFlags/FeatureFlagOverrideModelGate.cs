namespace MMCA.Common.Infrastructure.Persistence.FeatureFlags;

/// <summary>
/// The marker <c>AddReloadableFeatureFlags</c> registers to say that the <c>FeatureFlagOverrides</c>
/// table belongs in this host's model (the <c>PermissionGrantModelGate</c> precedent): the DI call IS
/// the opt-in, so a host that never makes it keeps a byte-identical model and sees no migration.
/// </summary>
/// <remarks>
/// It carries no state and is never injected anywhere. <c>ApplicationDbContext.OnConfiguring</c>
/// resolves it from the root provider with <c>GetService</c>, so its absence reads as "this host did
/// not opt in" rather than failing every context construction.
/// </remarks>
internal sealed class FeatureFlagOverrideModelGate;
