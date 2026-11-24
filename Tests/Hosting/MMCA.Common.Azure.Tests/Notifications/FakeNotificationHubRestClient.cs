using System.Net;
using MMCA.Common.Azure.Notifications;

namespace MMCA.Common.Azure.Tests.Notifications;

/// <summary>
/// An in-memory <see cref="INotificationHubRestClient"/> for the registrar and sender tests: it
/// holds installations by id, serves registration pages by continuation token, records every write
/// and send, and throws the not-found failure the real client throws for an unknown installation.
/// </summary>
internal sealed class FakeNotificationHubRestClient : INotificationHubRestClient
{
    public Dictionary<string, NotificationHubInstallationState> Installations { get; } = new(StringComparer.Ordinal);

    /// <summary>Registration pages keyed by the continuation token that requests them (empty for the first).</summary>
    public Dictionary<string, NotificationHubRegistrationPage> Pages { get; } = new(StringComparer.Ordinal);

    public List<NotificationHubInstallation> Upserts { get; } = [];

    public List<string> Deletes { get; } = [];

    public List<string> ListedTags { get; } = [];

    public List<(NotificationHubNotification Notification, string TagExpression)> Sends { get; } = [];

    public Exception? GetFailure { get; set; }

    public Exception? ListFailure { get; set; }

    public Exception? UpsertFailure { get; set; }

    public Func<NotificationHubNotification, string, Exception?> SendFailure { get; set; } = (_, _) => null;

    public static NotificationHubException NotFound() =>
        new("The notification hub answered 404 NotFound.", HttpStatusCode.NotFound, isTransient: false);

    public static NotificationHubException ServerBusy() =>
        new("The notification hub answered 503 ServiceUnavailable.", HttpStatusCode.ServiceUnavailable, isTransient: true);

    public void AddInstallation(string installationId, params string[] tags) =>
        Installations[installationId] = new NotificationHubInstallationState("fcm-registration-token", tags);

    public Task CreateOrUpdateInstallationAsync(NotificationHubInstallation installation, CancellationToken cancellationToken)
    {
        if (UpsertFailure is not null)
        {
            return Task.FromException(UpsertFailure);
        }

        Upserts.Add(installation);
        return Task.CompletedTask;
    }

    public Task<NotificationHubInstallationState?> GetInstallationAsync(string installationId, CancellationToken cancellationToken)
    {
        if (GetFailure is not null)
        {
            return Task.FromException<NotificationHubInstallationState?>(GetFailure);
        }

        return Installations.TryGetValue(installationId, out var installation)
            ? Task.FromResult<NotificationHubInstallationState?>(installation)
            : Task.FromException<NotificationHubInstallationState?>(NotFound());
    }

    public Task DeleteInstallationAsync(string installationId, CancellationToken cancellationToken)
    {
        Deletes.Add(installationId);
        return Task.CompletedTask;
    }

    public Task<NotificationHubRegistrationPage> GetRegistrationsByTagAsync(string tag, string? continuationToken, int top, CancellationToken cancellationToken)
    {
        ListedTags.Add(tag);
        if (ListFailure is not null)
        {
            return Task.FromException<NotificationHubRegistrationPage>(ListFailure);
        }

        return Task.FromResult(Pages[continuationToken ?? string.Empty]);
    }

    public Task SendNotificationAsync(NotificationHubNotification notification, string tagExpression, CancellationToken cancellationToken)
    {
        Sends.Add((notification, tagExpression));
        var failure = SendFailure(notification, tagExpression);
        return failure is null ? Task.CompletedTask : Task.FromException(failure);
    }
}
