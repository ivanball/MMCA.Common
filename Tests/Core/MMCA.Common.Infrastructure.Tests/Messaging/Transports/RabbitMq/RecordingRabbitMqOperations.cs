using MMCA.Common.Infrastructure.Messaging.Transports.RabbitMq;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.RabbitMq;

/// <summary>An <see cref="IRabbitMqOperations"/> that records every call, in order, and can be told to fail publishes.</summary>
internal sealed class RecordingRabbitMqOperations : IRabbitMqOperations
{
    /// <summary>Gets every call, as a short description, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Gets the arguments of every queue declaration, keyed by queue.</summary>
    public Dictionary<string, IDictionary<string, object?>?> QueueArguments { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets every publish.</summary>
    public List<Published> Publishes { get; } = [];

    /// <summary>Gets or sets the exception the next publish throws.</summary>
    public Exception? PublishFailure { get; set; }

    public Task DeclareFanoutExchangeAsync(string exchange, CancellationToken cancellationToken)
    {
        Calls.Add("exchange " + exchange);
        return Task.CompletedTask;
    }

    public Task DeclareQueueAsync(string queue, IDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        Calls.Add("queue " + queue);
        QueueArguments[queue] = arguments;
        return Task.CompletedTask;
    }

    public Task EnsureQueueExistsAsync(string queue, CancellationToken cancellationToken)
    {
        Calls.Add("passive " + queue);
        return Task.CompletedTask;
    }

    public Task BindQueueAsync(string queue, string exchange, CancellationToken cancellationToken)
    {
        Calls.Add($"bind {queue} {exchange}");
        return Task.CompletedTask;
    }

    public Task PublishAsync(
        string exchange,
        string routingKey,
        bool mandatory,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        Calls.Add($"publish {exchange}/{routingKey}");
        if (PublishFailure is { } failure)
        {
            PublishFailure = null;
            throw failure;
        }

        Publishes.Add(new Published(exchange, routingKey, mandatory, body.ToArray(), new Dictionary<string, string>(headers, StringComparer.Ordinal)));
        return Task.CompletedTask;
    }

    /// <summary>One recorded publish.</summary>
    internal sealed record Published(string Exchange, string RoutingKey, bool Mandatory, byte[] Body, IReadOnlyDictionary<string, string> Headers);
}
