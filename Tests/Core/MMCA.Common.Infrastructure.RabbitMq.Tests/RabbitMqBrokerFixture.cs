using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace MMCA.Common.Infrastructure.RabbitMq.Tests;

/// <summary>One RabbitMQ container shared by every broker transport test of a class, plus a probe connection.</summary>
public sealed class RabbitMqBrokerFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.2").Build();
    private IConnection? _probe;

    /// <summary>Gets the container's AMQP URI.</summary>
    public string ConnectionString => _rabbit.GetConnectionString();

    /// <summary>Gets a connection the tests inspect the broker with.</summary>
    public IConnection Probe => _probe ?? throw new InvalidOperationException("The fixture has not started.");

    public async ValueTask InitializeAsync()
    {
        await _rabbit.StartAsync(TestContext.Current.CancellationToken);
        _probe = await new ConnectionFactory { Uri = new Uri(ConnectionString) }.CreateConnectionAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_probe is not null)
        {
            await _probe.DisposeAsync();
        }

        await _rabbit.DisposeAsync();
    }
}
