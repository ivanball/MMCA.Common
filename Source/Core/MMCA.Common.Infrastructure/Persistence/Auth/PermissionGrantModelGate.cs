namespace MMCA.Common.Infrastructure.Persistence.Auth;

/// <summary>
/// The marker <c>AddStoredPermissionGrants(configuration)</c> registers to say that the
/// <c>PermissionGrants</c> table belongs in this host's model.
/// <para>
/// The refresh sessions answer the same question from <c>RefreshSessions:Enabled</c>, because that
/// table's workflow runs whether or not this host owns the rows. Stored grants have no such split:
/// the DI call IS the opt-in, and a second configuration flag would be a second way to say the same
/// thing. A marker type keeps the gate an explicit registration rather than a guess made by
/// resolving a service that has to exist for other reasons.
/// </para>
/// </summary>
/// <remarks>
/// It carries no state and is never injected anywhere. <c>ApplicationDbContext.OnConfiguring</c>
/// resolves it from the root provider with <c>GetService</c>, so its absence reads as "this host did
/// not opt in" rather than failing every context construction.
/// </remarks>
internal sealed class PermissionGrantModelGate;
