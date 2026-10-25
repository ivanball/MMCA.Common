using System.Collections.Concurrent;
using FirebaseAdmin.Messaging;
using MMCA.Common.Gcp.Notifications;

namespace MMCA.Common.Gcp.Emulator.Tests;

/// <summary>
/// FCM has no emulator, so this tier registers a recording <see cref="IFcmMessagingClient"/> before
/// <c>AddFirebaseCloudMessagingPush</c> (which then uses it as is): every send succeeds unless
/// <see cref="DeadTokens"/> names its token, and the topic calls are recorded. Tokens are read from
/// the message's wire JSON (Newtonsoft, FirebaseAdmin's own serializer).
/// </summary>
internal sealed class RecordingFcmMessagingClient : IFcmMessagingClient
{
    private static readonly Newtonsoft.Json.JsonSerializerSettings WireSettings = new()
    {
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
    };

    public ConcurrentQueue<string> SentTokens { get; } = new();

    public HashSet<string> DeadTokens { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Subscribed { get; } = new();

    public ConcurrentQueue<string> Unsubscribed { get; } = new();

    public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        List<FcmSendOutcome> outcomes = [];
        foreach (var message in messages)
        {
            var token = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(message, WireSettings))
                .Value<string>("token")!;
            SentTokens.Enqueue(token);
            outcomes.Add(DeadTokens.Contains(token)
                ? new FcmSendOutcome(false, MessagingErrorCode.Unregistered, new InvalidOperationException("Requested entity was not found."))
                : FcmSendOutcome.Success);
        }

        return Task.FromResult<IReadOnlyList<FcmSendOutcome>>(outcomes);
    }

    public Task SendAsync(Message message, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> SubscribeToTopicAsync(string token, string topic, CancellationToken cancellationToken)
    {
        Subscribed.Enqueue(token);
        return Task.FromResult<string?>(null);
    }

    public Task<string?> UnsubscribeFromTopicAsync(string token, string topic, CancellationToken cancellationToken)
    {
        Unsubscribed.Enqueue(token);
        return Task.FromResult<string?>(null);
    }
}
