using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Infrastructure.Messaging;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;
using MMCA.Common.Tests.Shared.Messaging;
using Npgsql;

namespace MMCA.Common.Infrastructure.PostgreSQL.Tests;

/// <summary>
/// The built-in PostgreSQL broker transport against a REAL PostgreSQL server: the shared transport
/// conformance scenarios (round trip, fan-out, delayed redelivery, dead letter, competing receivers) plus
/// the schema the startup migration creates. Each test gets its own schema, so each one also runs the
/// migration, and two hosts of one test run it concurrently.
/// <para>
/// These tests need a Docker daemon, so this project is outside <c>MMCA.Common.slnx</c> and runs in
/// its own CI job (<c>postgresql-integration</c>).
/// </para>
/// </summary>
public sealed class PostgreSqlBrokerTransportTests(PostgreSqlBrokerFixture fixture)
    : BrokerTransportConformanceTestsBase, IClassFixture<PostgreSqlBrokerFixture>
{
    private readonly string _schema = "broker_" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Migration_CreatesTheTables_OwnedByTheTransportRole_AndIsIdempotent()
    {
        var sql = new PostgreSqlTransportSql(_schema, PostgreSqlTransportSettings.DefaultName);
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var store = new PostgreSqlMessageStore(dataSource, ownsDataSource: false, sql);

        await store.EnsureSchemaAsync(Ct);
        await using var second = new PostgreSqlMessageStore(dataSource, ownsDataSource: false, sql);
        await second.EnsureSchemaAsync(Ct);

        await using var command = dataSource.CreateCommand(
            "SELECT tablename, tableowner FROM pg_catalog.pg_tables WHERE schemaname = @schema ORDER BY tablename");
        command.Parameters.AddWithValue("schema", _schema);
        List<(string Table, string Owner)> tables = [];
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        tables.Should().Equal(
            ("dead_letters", PostgreSqlTransportSettings.DefaultName),
            ("messages", PostgreSqlTransportSettings.DefaultName),
            ("subscriptions", PostgreSqlTransportSettings.DefaultName));
    }

    protected override void RegisterTransport(IServiceCollection services, MessageBusSettings settings, IConfiguration configuration, BrokerTopology topology)
    {
        var transportConfiguration = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MessageBus:PostgreSql:Schema"] = _schema })
            .Build();

        new PostgreSqlBrokerTransport().Register(
            services,
            new BrokerTransportContext(settings, fixture.ConnectionString, transportConfiguration, topology));
    }

    protected override async Task WaitUntilReceivingAsync(string queue, IReadOnlyList<string> topics, int receivers)
    {
        // Rows are claimed whenever a receiver looks, so only the bindings decide whether a publish is kept.
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (await ScalarAsync<long>($"SELECT count(*) FROM \"{_schema}\".subscriptions WHERE queue = @queue", queue, swallowMissingTable: true) < topics.Count)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "the receiver should bind its queue");
            await Task.Delay(50, Ct);
        }
    }

    protected override async Task AssertSettledAsync(string queue)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (await ScalarAsync<long>($"SELECT count(*) FROM \"{_schema}\".messages WHERE queue = @queue", queue) > 0)
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "every row of the queue should be settled");
            await Task.Delay(50, Ct);
        }
    }

    protected override async Task<IReadOnlyList<DeadLetteredMessage>> ReadDeadLettersAsync(string queue)
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var command = dataSource.CreateCommand($"SELECT reason, detail, body FROM \"{_schema}\".dead_letters WHERE queue = @queue");
        command.Parameters.AddWithValue(nameof(queue), queue);

        List<DeadLetteredMessage> deadLetters = [];
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            deadLetters.Add(new DeadLetteredMessage(
                reader.GetString(0),
                await reader.IsDBNullAsync(1, Ct) ? null : reader.GetString(1),
                await reader.GetFieldValueAsync<byte[]>(2, Ct)));
        }

        return deadLetters;
    }

    private async Task<T> ScalarAsync<T>(string sql, string queue, bool swallowMissingTable = false)
        where T : struct
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(nameof(queue), queue);
        try
        {
            return (T)(await command.ExecuteScalarAsync(Ct))!;
        }
        catch (PostgresException ex) when (swallowMissingTable && (string.Equals(ex.SqlState, PostgresErrorCodes.UndefinedTable, StringComparison.Ordinal) || string.Equals(ex.SqlState, PostgresErrorCodes.InvalidSchemaName, StringComparison.Ordinal)))
        {
            return default;
        }
    }
}
