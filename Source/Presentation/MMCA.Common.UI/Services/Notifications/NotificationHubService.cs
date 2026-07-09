using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MMCA.Common.UI.Common.Settings;
using MMCA.Common.UI.Services.Auth;

namespace MMCA.Common.UI.Services.Notifications;

/// <summary>
/// Manages the client-side SignalR connection to the notification hub.
/// Establishes a connection after authentication and invokes a callback when
/// notifications are received for display via MudBlazor snackbar.
/// If the initial connection fails, retries with exponential backoff up to
/// <see cref="MaxRetries"/> times so that transient startup-order issues
/// (token not yet available, API still starting) are handled gracefully.
/// <para>
/// The same connection also carries ephemeral live channel events: components join a channel via
/// <see cref="JoinChannelAsync"/> and subscribe handlers via <see cref="OnChannelEvent"/> (multicast,
/// so an invisible listener and a page can observe the same channel concurrently). Joined channels
/// are tracked and re-joined automatically after an automatic reconnect, because SignalR group
/// membership does not survive a new connection.
/// </para>
/// </summary>
public sealed partial class NotificationHubService : IAsyncDisposable
{
    private const int MaxRetries = 3;
    private const string ReceiveNotificationMethodName = "ReceiveNotification";
    private const string ReceiveChannelEventMethodName = "ReceiveChannelEvent";
    private const string JoinChannelMethodName = "JoinChannel";
    private const string LeaveChannelMethodName = "LeaveChannel";
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);

    private readonly ITokenStorageService _tokenStorageService;
    private readonly string _hubUrl;
    private readonly ILogger<NotificationHubService> _logger;
    private readonly Lock _channelSync = new();
    private readonly HashSet<string> _joinedChannels = [];
    private readonly Dictionary<string, List<ChannelSubscription>> _channelSubscriptions = [];
    private HubConnection? _hubConnection;
    private bool _disposed;

    /// <summary>
    /// Callback invoked when a push notification is received.
    /// Parameters: (title, body).
    /// </summary>
    public Func<string, string, Task>? NotificationCallback { get; set; }

    public NotificationHubService(
        ITokenStorageService tokenStorageService,
        IOptions<ApiSettings> apiSettings,
        ILogger<NotificationHubService> logger)
    {
        _tokenStorageService = tokenStorageService;
        _logger = logger;
        string endpoint = apiSettings?.Value.ApiEndpoint ?? throw new ArgumentNullException(nameof(apiSettings));
        _hubUrl = endpoint.TrimEnd('/') + "/hubs/notifications";
    }

    /// <summary>Gets a value indicating whether the hub connection is active.</summary>
    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

    /// <summary>
    /// Starts the SignalR connection if not already connected. Called after user login.
    /// Retries with exponential backoff if the initial attempt fails.
    /// </summary>
    public async Task StartAsync()
    {
        if (_hubConnection is not null)
        {
            return;
        }

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(_hubUrl, options => options.AccessTokenProvider = _tokenStorageService.GetAccessTokenAsync)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<string, string, Dictionary<string, string>?>(ReceiveNotificationMethodName, async (title, body, _) =>
        {
            if (NotificationCallback is not null)
            {
                await NotificationCallback.Invoke(title, body).ConfigureAwait(false);
            }
        });

        _hubConnection.On<string, string, string>(ReceiveChannelEventMethodName, DispatchChannelEventAsync);

        // Group membership lives on the server connection; a new connection after an automatic
        // reconnect starts with no groups, so every tracked channel must be re-joined.
        _hubConnection.Reconnected += _ => RejoinChannelsAsync();

        var delay = InitialRetryDelay;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _hubConnection.StartAsync().ConfigureAwait(false);
                LogConnected(_hubUrl);

                // Apply any channel joins requested before the connection came up.
                await RejoinChannelsAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                if (attempt == MaxRetries)
                {
                    LogConnectionFailed(ex, _hubUrl);
                    return;
                }

                LogRetrying(attempt + 1, MaxRetries, delay, _hubUrl);
                await Task.Delay(delay).ConfigureAwait(false);
                delay *= 2;
            }
        }
    }

    /// <summary>
    /// Joins a live channel so <see cref="OnChannelEvent"/> handlers receive its events. Starts the
    /// hub connection if needed, tracks the membership, and re-joins automatically after reconnects.
    /// Join failures are logged, never thrown: live updates are best-effort by design.
    /// Safe to call more than once for the same channel.
    /// </summary>
    /// <param name="channelKey">The channel key (e.g. <c>event:1</c>).</param>
    public async Task JoinChannelAsync(string channelKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);

        lock (_channelSync)
        {
            _joinedChannels.Add(channelKey);
        }

        await StartAsync().ConfigureAwait(false);

        HubConnection? connection = _hubConnection;
        if (connection?.State == HubConnectionState.Connected)
        {
            try
            {
                await connection.InvokeAsync(JoinChannelMethodName, channelKey).ConfigureAwait(false);
                LogChannelJoined(channelKey);
            }
            catch (Exception ex)
            {
                LogChannelJoinFailed(ex, channelKey);
            }
        }
    }

    /// <summary>
    /// Leaves a live channel and stops re-joining it after reconnects. Leave failures are logged,
    /// never thrown. Subscriptions registered via <see cref="OnChannelEvent"/> are not removed;
    /// dispose those individually.
    /// </summary>
    /// <param name="channelKey">The channel key (e.g. <c>event:1</c>).</param>
    public async Task LeaveChannelAsync(string channelKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);

        lock (_channelSync)
        {
            _joinedChannels.Remove(channelKey);
        }

        HubConnection? connection = _hubConnection;
        if (connection?.State == HubConnectionState.Connected)
        {
            try
            {
                await connection.InvokeAsync(LeaveChannelMethodName, channelKey).ConfigureAwait(false);
                LogChannelLeft(channelKey);
            }
            catch (Exception ex)
            {
                LogChannelLeaveFailed(ex, channelKey);
            }
        }
    }

    /// <summary>
    /// Subscribes a handler to events received on a channel. Multiple handlers may observe the same
    /// channel concurrently. Handler parameters: (eventName, payloadJson). Dispose the returned
    /// subscription to unsubscribe. Subscribing does not join the channel on the server; call
    /// <see cref="JoinChannelAsync"/> as well.
    /// </summary>
    /// <param name="channelKey">The channel key (e.g. <c>event:1</c>).</param>
    /// <param name="handler">Invoked for every event received on the channel.</param>
    /// <returns>A subscription that unsubscribes the handler when disposed.</returns>
    public IDisposable OnChannelEvent(string channelKey, Func<string, string, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelKey);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new ChannelSubscription(this, channelKey, handler);
        lock (_channelSync)
        {
            if (!_channelSubscriptions.TryGetValue(channelKey, out List<ChannelSubscription>? subscriptions))
            {
                subscriptions = [];
                _channelSubscriptions[channelKey] = subscriptions;
            }

            subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <summary>
    /// Stops the SignalR connection. Called on logout or disposal.
    /// </summary>
    public async Task StopAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync().ConfigureAwait(false);
            _hubConnection = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private async Task DispatchChannelEventAsync(string channelKey, string eventName, string payloadJson)
    {
        ChannelSubscription[] subscriptions;
        lock (_channelSync)
        {
            if (!_channelSubscriptions.TryGetValue(channelKey, out List<ChannelSubscription>? registered) || registered.Count == 0)
            {
                return;
            }

            subscriptions = [.. registered];
        }

        // Handlers are isolated: one failing subscriber must not starve the others.
        foreach (ChannelSubscription subscription in subscriptions)
        {
            try
            {
                await subscription.Handler.Invoke(eventName, payloadJson).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogChannelHandlerFailed(ex, channelKey, eventName);
            }
        }
    }

    private async Task RejoinChannelsAsync()
    {
        string[] channels;
        lock (_channelSync)
        {
            channels = [.. _joinedChannels];
        }

        HubConnection? connection = _hubConnection;
        if (channels.Length == 0 || connection?.State != HubConnectionState.Connected)
        {
            return;
        }

        foreach (string channelKey in channels)
        {
            try
            {
                await connection.InvokeAsync(JoinChannelMethodName, channelKey).ConfigureAwait(false);
                LogChannelJoined(channelKey);
            }
            catch (Exception ex)
            {
                LogChannelJoinFailed(ex, channelKey);
            }
        }
    }

    private void RemoveSubscription(ChannelSubscription subscription)
    {
        lock (_channelSync)
        {
            if (_channelSubscriptions.TryGetValue(subscription.ChannelKey, out List<ChannelSubscription>? subscriptions))
            {
                subscriptions.Remove(subscription);
                if (subscriptions.Count == 0)
                {
                    _channelSubscriptions.Remove(subscription.ChannelKey);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to notification hub at {HubUrl}")]
    private partial void LogConnected(string hubUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to notification hub at {HubUrl}")]
    private partial void LogConnectionFailed(Exception exception, string hubUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification hub connection attempt {Attempt}/{MaxAttempts} failed, retrying in {Delay} — {HubUrl}")]
    private partial void LogRetrying(int attempt, int maxAttempts, TimeSpan delay, string hubUrl);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Joined live channel {ChannelKey}")]
    private partial void LogChannelJoined(string channelKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to join live channel {ChannelKey}")]
    private partial void LogChannelJoinFailed(Exception exception, string channelKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Left live channel {ChannelKey}")]
    private partial void LogChannelLeft(string channelKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to leave live channel {ChannelKey}")]
    private partial void LogChannelLeaveFailed(Exception exception, string channelKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Channel event handler failed for {ChannelKey}/{EventName}")]
    private partial void LogChannelHandlerFailed(Exception exception, string channelKey, string eventName);

    private sealed class ChannelSubscription(NotificationHubService owner, string channelKey, Func<string, string, Task> handler) : IDisposable
    {
        public string ChannelKey { get; } = channelKey;

        public Func<string, string, Task> Handler { get; } = handler;

        public void Dispose() => owner.RemoveSubscription(this);
    }
}
