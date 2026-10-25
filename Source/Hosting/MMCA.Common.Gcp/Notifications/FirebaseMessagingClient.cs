using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace MMCA.Common.Gcp.Notifications;

/// <summary>
/// <see cref="IFcmMessagingClient"/> over FirebaseAdmin. A <see cref="FirebaseApp"/> is process-global
/// state keyed by name, so this client creates its OWN uniquely named app (never
/// <see cref="FirebaseApp.DefaultInstance"/>) and deletes it when the container disposes the client:
/// a host that creates its own default app is unaffected, two registrations never collide on a name,
/// and nothing outlives the service provider. The app is created on first use with Application
/// Default Credentials, not at registration, so a host builds its container without Google
/// credentials and only the first push needs them.
/// </summary>
/// <param name="projectId">The Firebase (Google Cloud) project the messages are sent from.</param>
internal sealed class FirebaseMessagingClient(string projectId) : IFcmMessagingClient, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FirebaseApp? _app;
    private FirebaseMessaging? _messaging;

    /// <summary>Gets the name of the Firebase app this client owns, unique per instance.</summary>
    internal string AppName { get; } = $"MMCA.Common.Gcp.Fcm.{Guid.NewGuid():N}";

    /// <inheritdoc />
    public async Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        var messaging = await GetMessagingAsync(cancellationToken).ConfigureAwait(false);
        var batch = await messaging.SendEachAsync(messages, cancellationToken).ConfigureAwait(false);
        return [.. batch.Responses.Select(response => response.IsSuccess
            ? FcmSendOutcome.Success
            : new FcmSendOutcome(false, response.Exception?.MessagingErrorCode, response.Exception))];
    }

    /// <inheritdoc />
    public async Task SendAsync(Message message, CancellationToken cancellationToken)
    {
        var messaging = await GetMessagingAsync(cancellationToken).ConfigureAwait(false);
        await messaging.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> SubscribeToTopicAsync(string token, string topic, CancellationToken cancellationToken)
    {
        var messaging = await GetMessagingAsync(cancellationToken).ConfigureAwait(false);
        return FirstError(await messaging.SubscribeToTopicAsync([token], topic, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<string?> UnsubscribeFromTopicAsync(string token, string topic, CancellationToken cancellationToken)
    {
        var messaging = await GetMessagingAsync(cancellationToken).ConfigureAwait(false);
        return FirstError(await messaging.UnsubscribeFromTopicAsync([token], topic, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _app?.Delete();
        _app = null;
        _messaging = null;
        _gate.Dispose();
    }

    private static string? FirstError(TopicManagementResponse response)
    {
        if (response.FailureCount == 0)
        {
            return null;
        }

        return response.Errors.Count > 0 ? response.Errors[0].Reason : "unknown-error";
    }

    private async Task<FirebaseMessaging> GetMessagingAsync(CancellationToken cancellationToken)
    {
        if (_messaging is { } existing)
        {
            return existing;
        }

        // Serialized: a FirebaseApp name can be created once, so two first callers must not race.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_messaging is null)
            {
                var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false);
                _app = FirebaseApp.Create(new AppOptions { Credential = credential, ProjectId = projectId }, AppName);
                _messaging = FirebaseMessaging.GetMessaging(_app);
            }

            return _messaging;
        }
        finally
        {
            _gate.Release();
        }
    }
}
