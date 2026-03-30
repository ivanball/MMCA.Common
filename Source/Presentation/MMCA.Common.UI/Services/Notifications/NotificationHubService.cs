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
/// </summary>
public sealed partial class NotificationHubService : IAsyncDisposable
{
    private readonly ITokenStorageService _tokenStorageService;
    private readonly string _hubUrl;
    private readonly ILogger<NotificationHubService> _logger;
    private HubConnection? _hubConnection;

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

        _hubConnection.On<string, string, Dictionary<string, string>?>("ReceiveNotification", async (title, body, _) =>
        {
            if (NotificationCallback is not null)
            {
                await NotificationCallback.Invoke(title, body).ConfigureAwait(false);
            }
        });

        try
        {
            await _hubConnection.StartAsync().ConfigureAwait(false);
            LogConnected(_hubUrl);
        }
        catch (Exception ex)
        {
            LogConnectionFailed(ex, _hubUrl);
        }
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
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to notification hub at {HubUrl}")]
    private partial void LogConnected(string hubUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to notification hub at {HubUrl}")]
    private partial void LogConnectionFailed(Exception exception, string hubUrl);
}
