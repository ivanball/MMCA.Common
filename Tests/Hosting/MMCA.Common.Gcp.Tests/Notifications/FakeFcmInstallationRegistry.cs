using MMCA.Common.Gcp.Notifications;

namespace MMCA.Common.Gcp.Tests.Notifications;

/// <summary>
/// A scripted, recording <see cref="IFcmInstallationRegistry"/> (the interface is internal, so Moq
/// cannot proxy it). Every answer is set by the test, so the registrar and sender rules are pinned
/// against the registry's contract, not against an in-memory copy of the Firestore ownership rule,
/// which the emulator tier pins on its own.
/// </summary>
internal sealed class FakeFcmInstallationRegistry : IFcmInstallationRegistry
{
    public Dictionary<string, FcmInstallation> Installations { get; } = new(StringComparer.Ordinal);

    public (bool Claimed, string? PreviousPushChannel) ClaimAnswer { get; set; } = (true, null);

    public Exception? ClaimException { get; set; }

    public Exception? QueryException { get; set; }

    public Exception? DeleteException { get; set; }

    public List<FcmInstallation> Claims { get; } = [];

    public List<IReadOnlyList<string>> Queries { get; } = [];

    public List<(string InstallationId, string UserKey)> OwnedDeletes { get; } = [];

    public List<(string InstallationId, string PushChannel)> PushChannelDeletes { get; } = [];

    public Task<FcmInstallation?> GetAsync(string installationId, CancellationToken cancellationToken) =>
        Task.FromResult(Installations.GetValueOrDefault(installationId));

    public Task<(bool Claimed, string? PreviousPushChannel)> TryClaimAsync(FcmInstallation installation, CancellationToken cancellationToken)
    {
        Claims.Add(installation);
        return ClaimException is null ? Task.FromResult(ClaimAnswer) : Task.FromException<(bool, string?)>(ClaimException);
    }

    public Task<IReadOnlyList<FcmInstallation>> QueryByUsersAsync(IReadOnlyList<string> userKeys, CancellationToken cancellationToken)
    {
        Queries.Add([.. userKeys]);
        if (QueryException is not null)
        {
            return Task.FromException<IReadOnlyList<FcmInstallation>>(QueryException);
        }

        return Task.FromResult<IReadOnlyList<FcmInstallation>>(
            [.. Installations.Values.Where(i => userKeys.Contains(i.UserId, StringComparer.Ordinal))]);
    }

    public Task<bool> DeleteOwnedAsync(string installationId, string userKey, CancellationToken cancellationToken)
    {
        OwnedDeletes.Add((installationId, userKey));
        return DeleteException is null ? Task.FromResult(true) : Task.FromException<bool>(DeleteException);
    }

    public Task<bool> DeleteIfPushChannelAsync(string installationId, string pushChannel, CancellationToken cancellationToken)
    {
        PushChannelDeletes.Add((installationId, pushChannel));
        return Task.FromResult(true);
    }
}
