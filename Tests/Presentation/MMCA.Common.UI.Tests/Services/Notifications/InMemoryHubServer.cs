using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace MMCA.Common.UI.Tests.Services.Notifications;

/// <summary>
/// A minimal in-memory SignalR "server" for <see cref="NotificationHubServiceTests"/>: it plays the JSON
/// hub handshake over a pair of pipes so a real <see cref="HubConnection"/> reaches
/// <see cref="HubConnectionState.Connected"/> with no network, and lets the test drop every live
/// connection, with or without an error, to drive the client's close path.
/// </summary>
internal sealed class InMemoryHubServer : IConnectionFactory
{
    private const byte RecordSeparator = 0x1e;
    private readonly Lock _sync = new();
    private readonly List<IDuplexPipe> _live = [];

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

        // Drain whatever the client sends next (pings, invocations) until it goes away.
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

            application.Input.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted || result.IsCanceled)
            {
                return;
            }
        }
    }

    private sealed class PipePair(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
