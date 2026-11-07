using System.Text.Json;
using FirebaseAdmin.Messaging;
using MMCA.Common.Gcp.Notifications;

namespace MMCA.Common.Gcp.Tests.Notifications;

/// <summary>
/// A recording <see cref="IFcmMessagingClient"/> (the interface is internal, so Moq cannot proxy it):
/// keeps every send call, answers each message from <see cref="OutcomeFor"/> (keyed by its token),
/// and reads messages through their FCM wire JSON.
/// FirebaseAdmin serializes a <see cref="Message"/> with Newtonsoft to exactly the HTTP v1
/// <c>message</c> object, so the tests assert the bytes FCM receives rather than SDK properties.
/// </summary>
internal sealed class FakeFcmMessagingClient : IFcmMessagingClient
{
    /// <summary>FirebaseAdmin's own serializer posture: unset fields are omitted from the request.</summary>
    private static readonly Newtonsoft.Json.JsonSerializerSettings WireSettings = new()
    {
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
    };

    public List<IReadOnlyList<Message>> SendEachCalls { get; } = [];

    public Func<string, FcmSendOutcome> OutcomeFor { get; set; } = _ => FcmSendOutcome.Success;

    /// <summary>The message as the FCM HTTP v1 API receives it.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The JSON object.</returns>
    public static JsonElement Wire(Message message) =>
        JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(message, WireSettings)).RootElement.Clone();

    /// <summary>The token a message targets, read from its wire form.</summary>
    /// <param name="message">The message.</param>
    /// <returns>The token.</returns>
    public static string TokenOf(Message message) => Wire(message).GetProperty("token").GetString()!;

    public Task<IReadOnlyList<FcmSendOutcome>> SendEachAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken)
    {
        SendEachCalls.Add(messages);
        return Task.FromResult<IReadOnlyList<FcmSendOutcome>>([.. messages.Select(m => OutcomeFor(TokenOf(m)))]);
    }
}
