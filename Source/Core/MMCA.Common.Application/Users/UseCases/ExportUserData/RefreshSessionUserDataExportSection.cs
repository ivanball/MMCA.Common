using MMCA.Common.Application.Auth;
using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.Auth;

namespace MMCA.Common.Application.Users.UseCases.ExportUserData;

/// <summary>
/// The framework's own export section: the user's sign-in sessions (<see cref="RefreshSession"/>
/// rows), so the access and portability export covers the session metadata the account holds, the
/// same rows erasure anonymizes and the signed-in-devices list shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every row, revoked and expired included</b> (<see cref="IRefreshSessionStore.GetAllByUserAsync"/>),
/// oldest first: a revoked row still carries the IP address, user agent and device fields captured
/// at issue time, so leaving it out would export less than the store holds.
/// </para>
/// <para>
/// <b>Never a secret.</b> The token hash and the successor's token hash are credentials material and
/// are not projected; <see cref="UserSessionExportDTO"/> is the whole surface.
/// </para>
/// <para>
/// Registered by <c>AddInfrastructure</c> beside the refresh-session store, so every host that
/// issues sessions exports them with no app code. It runs only where an app's export handler runs.
/// </para>
/// </remarks>
/// <param name="sessionStore">The refresh-session store.</param>
public sealed class RefreshSessionUserDataExportSection(IRefreshSessionStore sessionStore) : IUserDataExportSection
{
    /// <summary>The name this section is published under in the export document.</summary>
    public const string Name = "SignInSessions";

    /// <inheritdoc />
    public string SectionName => Name;

    /// <inheritdoc />
    public async Task<UserDataExportSectionResult> ExportAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var sessions = await sessionStore.GetAllByUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return UserDataExportSectionResult.Complete(Name, sessions.Select(UserSessionExportDTO.From).ToList());
    }
}

/// <summary>
/// One sign-in session as the data subject receives it: when it was issued, when it expires or was
/// revoked and why, and the client and device details recorded for it. Carries no token hash. The
/// client and device members are <c>[Pii]</c>, as on <see cref="RefreshSession"/>, so a log redactor
/// masks them.
/// </summary>
/// <param name="CreatedAt">The UTC instant the session was issued.</param>
/// <param name="ExpiresAt">The UTC instant the session stops being usable.</param>
/// <param name="RevokedAt">The UTC instant the session was revoked, or null while it is live.</param>
/// <param name="RevocationReason">Why the session was revoked, or null.</param>
/// <param name="IpAddress">The client IP recorded at issue time, or null.</param>
/// <param name="UserAgent">The client user agent recorded at issue time, or null.</param>
/// <param name="DeviceId">The per-install identifier the native client reported, or null.</param>
/// <param name="DeviceName">The owner-assigned device name, or null.</param>
/// <param name="DeviceModel">The device hardware model, or null.</param>
/// <param name="DeviceManufacturer">The device manufacturer, or null.</param>
/// <param name="DevicePlatform">The device operating system, or null.</param>
/// <param name="DeviceFormFactor">The device form factor, or null.</param>
/// <param name="DeviceType">Whether the device is physical or virtual, or null.</param>
public sealed record UserSessionExportDTO(
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    string? RevocationReason,
    [property: Pii] string? IpAddress,
    [property: Pii] string? UserAgent,
    [property: Pii] string? DeviceId,
    [property: Pii] string? DeviceName,
    [property: Pii] string? DeviceModel,
    [property: Pii] string? DeviceManufacturer,
    [property: Pii] string? DevicePlatform,
    [property: Pii] string? DeviceFormFactor,
    [property: Pii] string? DeviceType)
{
    /// <summary>Projects one stored session, leaving every hash behind.</summary>
    /// <param name="session">The stored session.</param>
    /// <returns>The exported view of the session.</returns>
    public static UserSessionExportDTO From(RefreshSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new UserSessionExportDTO(
            session.CreatedAt,
            session.ExpiresAt,
            session.RevokedAt,
            session.ReasonRevoked,
            session.IpAddress,
            session.UserAgent,
            session.DeviceId,
            session.DeviceName,
            session.DeviceModel,
            session.DeviceManufacturer,
            session.DevicePlatform,
            session.DeviceFormFactor,
            session.DeviceType);
    }
}
