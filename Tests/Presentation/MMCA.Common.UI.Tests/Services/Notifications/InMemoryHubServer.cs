using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// A minimal in-memory SignalR "server" for <see cref="NotificationHubServiceTests"/>: it plays the JSON
/// hub handshake over a pair of pipes so a real <see cref="HubConnection"/> reaches
/// <see cref="HubConnectionState.Connected"/> with no network, and lets the test drop every live
/// connection, with or without an error, to drive the client's close path. It can also refuse new
/// connections (a network that is still down), counts connection attempts, and answers hub
/// invocations with an empty completion while recording each <c>JoinChannel</c> it receives.
/// </summary>
internal sealed class InMemoryHubServer : IConnectionFactory
{
    private const byte RecordSeparator = 0x1e;
    private readonly Lock _sync = new();
    private readonly List<IDuplexPipe> _live = [];
    private readonly List<string> _joinedChannels = [];
    private int _connectAttempts;
    private volatile bool _refuseConnections;

    /// <summary>Gets or sets a value indicating whether new connections fail as if the network were down.</summary>
    public bool RefuseConnections
    {
        get => _refuseConnections;
        set => _refuseConnections = value;
    }

    /// <summary>Gets the number of connection attempts, refused ones included.</summary>
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    /// <summary>Gets how many <c>JoinChannel</c> invocations named <paramref name="channelKey"/>.</summary>
    /// <param name="channelKey">The channel key.</param>
    /// <returns>The join count.</returns>
    public int JoinCount(string channelKey)
    {
        lock (_sync)
        {
            return _joinedChannels.Count(c => string.Equals(c, channelKey, StringComparison.Ordinal));
        }
    }

    /// <summary>Builds a client connection bound to this server (no automatic reconnect).</summary>
    public HubConnection CreateConnection()
    {
        var builder = new HubConnectionBuilder();
        builder.Services.AddSingleton<IConnectionFactory>(this);
        builder.Services.AddSingleton<EndPoint>(new UriEndPoint(new Uri("http://in-memory/hubs/notifications")));
        return builder.Build();
    }

    /// <summary>Completes the server side of every live connection; a non-null error faults the client read.</summary>
    public void DropAll(Exception? error)
    {
        IDuplexPipe[] live;
        lock (_sync)
        {
            live = [.. _live];
            _live.Clear();
        }

        foreach (var application in live)
        {
            application.Output.Complete(error);
        }
    }

    public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _connectAttempts);
        if (RefuseConnections)
        {
            return ValueTask.FromException<ConnectionContext>(new IOException("The in-memory network is down."));
        }

        var toClient = new Pipe();
        var toServer = new Pipe();
        var transport = new PipePair(toClient.Reader, toServer.Writer);
        var application = new PipePair(toServer.Reader, toClient.Writer);
        _ = RunServerAsync(application);
        return ValueTask.FromResult<ConnectionContext>(
            new DefaultConnectionContext(Guid.NewGuid().ToString("N"), transport, application));
    }

    private async Task RunServerAsync(IDuplexPipe application)
    {
        // Handshake request: one JSON record terminated by the record separator.
        while (true)
        {
            var result = await application.Input.ReadAsync().ConfigureAwait(false);
            var buffer = result.Buffer;
            var separator = buffer.PositionOf(RecordSeparator);
            if (separator is not null)
            {
                application.Input.AdvanceTo(buffer.GetPosition(1, separator.Value));
                break;
            }

            application.Input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted)
            {
                return;
            }
        }

        lock (_sync)
        {
            _live.Add(application);
        }

        await application.Output.WriteAsync(Encoding.UTF8.GetBytes("{}\u001e")).ConfigureAwait(false);

        // Read whatever the client sends next (pings, invocations) until it goes away, answering each
        // invocation with an empty completion so the client's InvokeAsync returns.
        while (true)
        {
            ReadResult result;
            try
            {
                result = await application.Input.ReadAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var buffer = result.Buffer;
            while (buffer.PositionOf(RecordSeparator) is { } separator)
            {
                var record = buffer.Slice(0, separator).ToArray();
                buffer = buffer.Slice(buffer.GetPosition(1, separator));
                if (!await AnswerAsync(application, record).ConfigureAwait(false))
                {
                    return;
                }
            }

            application.Input.AdvanceTo(buffer.Start, buffer.End);
            if (result.IsCompleted || result.IsCanceled)
            {
                return;
            }
        }
    }

    private async Task<bool> AnswerAsync(IDuplexPipe application, byte[] record)
    {
        using var message = JsonDocument.Parse(record);
        var root = message.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetInt32() != 1
            || !root.TryGetProperty("invocationId", out var invocationId))
        {
            return true;
        }

        if (root.TryGetProperty("target", out var target)
            && string.Equals(target.GetString(), "JoinChannel", StringComparison.Ordinal)
            && root.TryGetProperty("arguments", out var arguments)
            && arguments.GetArrayLength() > 0)
        {
            lock (_sync)
            {
                _joinedChannels.Add(arguments[0].GetString() ?? string.Empty);
            }
        }

        try
        {
            var completion = "{\"type\":3,\"invocationId\":\"" + invocationId.GetString() + "\"}\u001e";
            await application.Output.WriteAsync(Encoding.UTF8.GetBytes(completion)).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException)
        {
            // DropAll completed the output underneath this write: the connection is gone.
            return false;
        }
    }

    private sealed class PipePair(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
