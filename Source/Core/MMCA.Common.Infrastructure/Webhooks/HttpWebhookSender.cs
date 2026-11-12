using System.Globalization;
using System.Net;
using System.Text;
using MMCA.Common.Application.Webhooks.Delivery;
using MMCA.Common.Application.Webhooks.Subscriptions;

namespace MMCA.Common.Infrastructure.Webhooks;

/// <summary>
/// Default <see cref="IWebhookSender"/>: one Standard Webhooks POST through the <c>MMCA.Webhooks</c>
/// client, signed once per readable secret (current first), mapped to an outcome from the status code
/// or the exception alone. The response body is never read.
/// </summary>
/// <param name="httpClientFactory">Creates the <c>MMCA.Webhooks</c> client.</param>
/// <param name="secretProtector">Unprotects the signing secrets.</param>
/// <param name="timeProvider">The clock for <c>webhook-timestamp</c>.</param>
internal sealed class HttpWebhookSender(
    IHttpClientFactory httpClientFactory,
    IWebhookSecretProtector secretProtector,
    TimeProvider timeProvider) : IWebhookSender
{
    /// <summary>The named client deliveries go through.</summary>
    internal const string ClientName = "MMCA.Webhooks";

    private const string UserAgent = "MMCA-Webhooks/1";
    private const string JsonMediaType = "application/json";

    /// <inheritdoc />
    public async Task<WebhookSendOutcome> SendAsync(WebhookDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var keys = ReadKeys(request.ProtectedSecrets);
        if (keys.Count == 0)
        {
            // An unsigned delivery is never sent.
            return new WebhookSendOutcome(WebhookSendStatus.SecretUnreadable, null, "The current webhook signing secret cannot be read.");
        }

        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var signatures = string.Join(' ', keys.Select(key => WebhookSignature.Compute(request.WebhookId, timestamp, request.Body, key)));

        using var message = new HttpRequestMessage(HttpMethod.Post, request.Target)
        {
            Content = new StringContent(request.Body, Encoding.UTF8, JsonMediaType),
        };
        message.Headers.TryAddWithoutValidation(WebhookSignature.IdHeader, request.WebhookId);
        message.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, signatures);
        message.Headers.UserAgent.ParseAdd(UserAgent);

        try
        {
            using var client = httpClientFactory.CreateClient(ClientName);
            using var response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return new WebhookSendOutcome(Classify(response.StatusCode), (int)response.StatusCode, null);
        }
        catch (WebhookTargetRefusedException refused)
        {
            return new WebhookSendOutcome(WebhookSendStatus.TargetRefused, null, Describe(refused));
        }
        catch (HttpRequestException failed) when (failed.InnerException is WebhookTargetRefusedException refused)
        {
            return new WebhookSendOutcome(WebhookSendStatus.TargetRefused, null, Describe(refused));
        }
        catch (HttpRequestException failed)
        {
            return new WebhookSendOutcome(WebhookSendStatus.TransportFailed, null, Describe(failed));
        }
        catch (OperationCanceledException timedOut) when (!cancellationToken.IsCancellationRequested)
        {
            // The client's own Timeout elapsed; the caller's token (host shutdown) is still live.
            return new WebhookSendOutcome(WebhookSendStatus.TransportFailed, null, Describe(timedOut));
        }
    }

    /// <summary>2xx is delivered, 410 is gone, anything else (redirects included) is a rejection.</summary>
    private static WebhookSendStatus Classify(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.Gone)
        {
            return WebhookSendStatus.Gone;
        }

        return (int)statusCode is >= 200 and <= 299 ? WebhookSendStatus.Delivered : WebhookSendStatus.Rejected;
    }

    private static string Describe(Exception exception) => exception.GetType().Name + ": " + exception.Message;

    /// <summary>
    /// The HMAC keys to sign with: the current secret first, then any readable previous one. An
    /// unreadable current secret yields none; an unreadable previous secret is dropped.
    /// </summary>
    private List<byte[]> ReadKeys(IReadOnlyList<string> protectedSecrets)
    {
        List<byte[]> keys = [];
        for (var i = 0; i < protectedSecrets.Count; i++)
        {
            var plain = secretProtector.Unprotect(protectedSecrets[i]);
            var key = plain.TryGetValue(out var secret) ? WebhookSignature.DecodeSecret(secret) : null;
            if (key is not null && key.TryGetValue(out var bytes))
            {
                keys.Add(bytes);
            }
            else if (i == 0)
            {
                return [];
            }
        }

        return keys;
    }
}
