using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace MMCA.Common.Azure.Notifications;

/// <summary>
/// The Azure Notification Hubs REST operations native push uses (ADR-044): installation upsert, read
/// and delete, registrations by tag, and native sends. Every failure surfaces as a
/// <see cref="NotificationHubException"/> (not-found flagged by
/// <see cref="NotificationHubException.IsEntityNotFound"/>).
/// </summary>
internal interface INotificationHubRestClient
{
    /// <summary>Creates or fully overwrites an installation (<c>PUT installations/{id}</c>).</summary>
    Task CreateOrUpdateInstallationAsync(NotificationHubInstallation installation, CancellationToken cancellationToken);

    /// <summary>Reads an installation (<c>GET installations/{id}</c>); a 404 throws a not-found failure.</summary>
    Task<NotificationHubInstallationState?> GetInstallationAsync(string installationId, CancellationToken cancellationToken);

    /// <summary>Deletes an installation (<c>DELETE installations/{id}</c>).</summary>
    Task DeleteInstallationAsync(string installationId, CancellationToken cancellationToken);

    /// <summary>Reads one page of the registrations carrying <paramref name="tag"/> (<c>GET tags/{tag}/registrations</c>).</summary>
    Task<NotificationHubRegistrationPage> GetRegistrationsByTagAsync(string tag, string? continuationToken, int top, CancellationToken cancellationToken);

    /// <summary>Sends a native notification to the installations matching <paramref name="tagExpression"/> (<c>POST messages</c>).</summary>
    Task SendNotificationAsync(NotificationHubNotification notification, string tagExpression, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="INotificationHubRestClient"/> over a factory <see cref="HttpClient"/>, authorized with
/// a Shared Access Signature per request. The request shapes (URL, query, headers, bodies), the
/// accepted status codes, the failure classification and the retry are those of the retired
/// Microsoft.Azure.NotificationHubs 4.2.0 SDK, captured from it and pinned by
/// <c>NotificationHubRestClientRequestShapeTests</c>. Registrations by tag are read once, without a
/// retry, as the SDK did; the other operations run through <see cref="NotificationHubRetryPolicy"/>.
/// </summary>
internal sealed class NotificationHubRestClient : INotificationHubRestClient
{
    /// <summary>The per-attempt timeout set on the client.</summary>
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The <c>User-Agent</c> the hub has always seen from this framework.</summary>
    internal static readonly string UserAgent =
        $"NHub/{NotificationHubConnection.ApiVersion} (api-origin=DotNetSdk;os={Environment.OSVersion.Platform};os-version={Environment.OSVersion.Version})";

    private const string JsonMediaType = "application/json";

    private static readonly HttpStatusCode[] OkOnly = [HttpStatusCode.OK];
    private static readonly HttpStatusCode[] NoContentOnly = [HttpStatusCode.NoContent];
    private static readonly HttpStatusCode[] OkOrCreated = [HttpStatusCode.OK, HttpStatusCode.Created];

    private readonly HttpClient _httpClient;
    private readonly NotificationHubConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly NotificationHubRetryPolicy _retryPolicy;

    /// <summary>
    /// Takes a fresh <see cref="HttpClient"/> (a typed factory client, never one already used) and sets
    /// its timeout and default <c>User-Agent</c>, as the SDK set them on its own client.
    /// </summary>
    internal NotificationHubRestClient(HttpClient httpClient, NotificationHubConnection connection, TimeProvider timeProvider, NotificationHubRetryPolicy retryPolicy)
    {
        _httpClient = httpClient;
        _connection = connection;
        _timeProvider = timeProvider;
        _retryPolicy = retryPolicy;

        _httpClient.Timeout = OperationTimeout;
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }
    }

    /// <inheritdoc />
    public async Task CreateOrUpdateInstallationAsync(NotificationHubInstallation installation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (string.IsNullOrWhiteSpace(installation.InstallationId))
        {
            throw new InvalidOperationException("installation.InstallationId must be specified");
        }

        var requestUri = _connection.CreateRequestUriBuilder();
        requestUri.Path += $"installations/{installation.InstallationId}";
        var body = NotificationHubWireFormat.WriteInstallation(installation);

        await _retryPolicy.RunAsync(
            async ct =>
            {
                using var request = CreateRequest(HttpMethod.Put, requestUri.Uri, out var trackingId);
                request.Content = new StringContent(body, Encoding.UTF8, JsonMediaType);
                using var response = await SendAsync(request, trackingId, OkOnly, ct).ConfigureAwait(false);
                return true;
            },
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<NotificationHubInstallationState?> GetInstallationAsync(string installationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);

        var requestUri = _connection.CreateRequestUriBuilder();
        requestUri.Path += $"installations/{installationId}";

        return _retryPolicy.RunAsync(
            async ct =>
            {
                using var request = CreateRequest(HttpMethod.Get, requestUri.Uri, out var trackingId);
                using var response = await SendAsync(request, trackingId, OkOnly, ct).ConfigureAwait(false);
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return NotificationHubWireFormat.ReadInstallation(content);
            },
            _timeProvider,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteInstallationAsync(string installationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);

        var requestUri = _connection.CreateRequestUriBuilder();
        requestUri.Path += $"installations/{installationId}";

        await _retryPolicy.RunAsync(
            async ct =>
            {
                using var request = CreateRequest(HttpMethod.Delete, requestUri.Uri, out var trackingId);
                using var response = await SendAsync(request, trackingId, NoContentOnly, ct).ConfigureAwait(false);
                return true;
            },
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NotificationHubRegistrationPage> GetRegistrationsByTagAsync(string tag, string? continuationToken, int top, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var requestUri = _connection.CreateRequestUriBuilder();
        requestUri.Path += $"tags/{tag}/registrations";
        if (top > 0)
        {
            NotificationHubConnection.AddToQuery(requestUri, string.Create(CultureInfo.InvariantCulture, $"&$top={top}"));
        }

        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            // Appended as the hub returned it: the token is opaque and the hub reads it back raw.
            NotificationHubConnection.AddToQuery(requestUri, $"&continuationtoken={continuationToken}");
        }

        using var request = CreateRequest(HttpMethod.Get, requestUri.Uri, out var trackingId);
        using var response = await SendAsync(request, trackingId, OkOnly, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var registrationTags = await NotificationHubWireFormat.ReadRegistrationTagsAsync(stream).ConfigureAwait(false);
            var nextToken = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.First()
                : null;
            return new NotificationHubRegistrationPage(registrationTags, nextToken);
        }
    }

    /// <inheritdoc />
    public async Task SendNotificationAsync(NotificationHubNotification notification, string tagExpression, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var requestUri = _connection.CreateRequestUriBuilder();
        requestUri.Path += "messages";

        await _retryPolicy.RunAsync(
            async ct =>
            {
                using var request = CreateRequest(HttpMethod.Post, requestUri.Uri, out var trackingId);
                if (!string.IsNullOrWhiteSpace(tagExpression))
                {
                    request.Headers.Add("ServiceBusNotification-Tags", tagExpression);
                }

                request.Headers.Add("ServiceBusNotification-Format", notification.Format);
                request.Content = new StringContent(notification.Body, Encoding.UTF8, JsonMediaType);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);

                using var response = await SendAsync(request, trackingId, OkOrCreated, ct).ConfigureAwait(false);
                return true;
            },
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri requestUri, out string trackingId)
    {
        trackingId = Guid.NewGuid().ToString();

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("TrackingId", trackingId);
        request.Headers.Add("Authorization", _connection.CreateSharedAccessSignature(requestUri, _timeProvider.GetUtcNow()));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string trackingId, HttpStatusCode[] successStatuses, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw NotificationHubWireFormat.TranslateTransportFailure(ex, trackingId);
        }

        if (Array.IndexOf(successStatuses, response.StatusCode) >= 0)
        {
            return response;
        }

        using (response)
        {
            throw await NotificationHubWireFormat.TranslateFailureAsync(response, trackingId).ConfigureAwait(false);
        }
    }
}
