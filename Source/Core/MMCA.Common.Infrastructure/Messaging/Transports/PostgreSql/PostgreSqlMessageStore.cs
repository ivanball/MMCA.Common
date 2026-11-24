using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using MMCA.Common.Infrastructure.Messaging.Broker;
using Npgsql;
using NpgsqlTypes;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// The Npgsql <see cref="IPostgreSqlMessageStore"/>. Runs on the shared <see cref="NpgsqlDataSource"/>
/// the PostgreSQL contexts use for the same connection string when an
/// <c>INpgsqlDataSourceConfigurator</c> is registered (an RDS, Cloud SQL or Entra token provider then
/// authenticates the broker's connections too, migration included), otherwise on a data source of its
/// own, disposed with the store.
/// </summary>
internal sealed class PostgreSqlMessageStore : IPostgreSqlMessageStore, IAsyncDisposable, IDisposable
{
    private const string StatementJustification =
        "Every statement comes from PostgreSqlTransportSql: constants plus the schema, role and channel names, which are validated as plain identifiers and quoted; every value is a parameter.";

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;
    private readonly PostgreSqlTransportSql _sql;
    private readonly Lock _schemaGate = new();
    private Task? _schemaTask;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlMessageStore"/> class.</summary>
    /// <param name="dataSource">The data source to open connections from.</param>
    /// <param name="ownsDataSource">Whether disposing the store disposes <paramref name="dataSource"/>.</param>
    /// <param name="sql">The statements for the configured schema.</param>
    public PostgreSqlMessageStore(NpgsqlDataSource dataSource, bool ownsDataSource, PostgreSqlTransportSql sql)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(sql);

        _dataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _sql = sql;
    }

    /// <summary>Serializes headers for the <c>headers</c> jsonb column; keys keep their case.</summary>
    /// <param name="headers">The headers.</param>
    /// <returns>The JSON object text.</returns>
    internal static string SerializeHeaders(IReadOnlyDictionary<string, string> headers) =>
        JsonSerializer.Serialize(headers);

    /// <summary>Reads the <c>headers</c> column back into string-to-string headers keyed ordinally.</summary>
    /// <param name="json">The JSON object text.</param>
    /// <returns>The headers.</returns>
    internal static Dictionary<string, string> DeserializeHeaders(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.Ordinal);

    /// <inheritdoc />
    public Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        lock (_schemaGate)
        {
            if (_schemaTask is null || _schemaTask.IsFaulted || _schemaTask.IsCanceled)
            {
                // Not tied to any one caller's token: a caller that gives up must not cancel the
                // migration another caller is waiting on.
                _schemaTask = MigrateAsync();
            }

            return _schemaTask.WaitAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task SubscribeAsync(string queue, IReadOnlyCollection<string> topics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);

        var command = _dataSource.CreateCommand(_sql.Subscribe);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue(nameof(queue), queue);
            command.Parameters.AddWithValue(nameof(topics), topics.ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(OutgoingBrokerMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var command = _dataSource.CreateCommand(_sql.Publish);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("topic", message.Topic);
            command.Parameters.AddWithValue("message_id", message.MessageId);
            command.Parameters.Add(new NpgsqlParameter("headers", NpgsqlDbType.Jsonb) { Value = SerializeHeaders(message.Headers) });
            command.Parameters.AddWithValue("body", message.Body.ToArray());
            command.Parameters.AddWithValue("channel", _sql.NotifyChannel);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostgreSqlClaimedMessage>> ClaimAsync(string queue, int limit, TimeSpan lease, CancellationToken cancellationToken)
    {
        var lockToken = Guid.NewGuid();
        List<PostgreSqlClaimedMessage> claimed = [];

        var command = _dataSource.CreateCommand(_sql.Claim);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue(nameof(queue), queue);
            command.Parameters.AddWithValue(nameof(limit), limit);
            command.Parameters.AddWithValue("lock_token", lockToken);
            command.Parameters.AddWithValue(nameof(lease), lease);

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    claimed.Add(new PostgreSqlClaimedMessage(
                        reader.GetInt64(0),
                        lockToken,
                        reader.GetGuid(1),
                        DeserializeHeaders(reader.GetString(2)),
                        await reader.GetFieldValueAsync<byte[]>(3, cancellationToken).ConfigureAwait(false),
                        reader.GetInt32(4)));
                }
            }
        }

        // RETURNING order is unspecified; hand rows over oldest first.
        claimed.Sort((left, right) => left.Id.CompareTo(right.Id));
        return claimed;
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(PostgreSqlClaimedMessage message, CancellationToken cancellationToken) =>
        SettleAsync(_sql.Complete, message, configure: null, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RedeliverAsync(
        PostgreSqlClaimedMessage message,
        TimeSpan delay,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return SettleAsync(
            _sql.Redeliver,
            message,
            parameters =>
            {
                parameters.AddWithValue(nameof(delay), delay);
                parameters.Add(new NpgsqlParameter(nameof(headers), NpgsqlDbType.Jsonb) { Value = SerializeHeaders(headers) });
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeadLetterAsync(PostgreSqlClaimedMessage message, string reason, string? detail, CancellationToken cancellationToken) =>
        SettleAsync(
            _sql.DeadLetter,
            message,
            parameters =>
            {
                parameters.Add(new NpgsqlParameter(nameof(reason), NpgsqlDbType.Text) { Value = reason });
                parameters.Add(new NpgsqlParameter(nameof(detail), NpgsqlDbType.Text) { Value = (object?)detail ?? DBNull.Value });
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(PostgreSqlClaimedMessage message, CancellationToken cancellationToken) =>
        SettleAsync(_sql.Release, message, configure: null, cancellationToken);

    /// <inheritdoc />
    public async Task RenewAsync(IReadOnlyCollection<PostgreSqlClaimedMessage> messages, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return;
        }

        var command = _dataSource.CreateCommand(_sql.Renew);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("ids", messages.Select(m => m.Id).ToArray());
            command.Parameters.AddWithValue("lock_tokens", messages.Select(m => m.LockToken).ToArray());
            command.Parameters.AddWithValue(nameof(lease), lease);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> NextDueAsync(string queue, CancellationToken cancellationToken)
    {
        var command = _dataSource.CreateCommand(_sql.NextDue);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue(nameof(queue), queue);
            var seconds = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return seconds is double value ? TimeSpan.FromSeconds(value) : null;
        }
    }

    /// <inheritdoc />
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = StatementJustification)]
    public async Task ListenAsync(string queue, Action notified, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notified);

        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            connection.Notification += (_, e) =>
            {
                if (string.Equals(e.Payload, queue, StringComparison.Ordinal))
                {
                    notified();
                }
            };

            var command = new NpgsqlCommand(_sql.Listen, connection);
            await using (command.ConfigureAwait(false))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Anything published before LISTEN took effect raised no notification for this connection.
            notified();

            try
            {
                while (true)
                {
                    await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopping.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsDataSource)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Disposes synchronously, for a container disposed without <see langword="await"/>.</summary>
    public void Dispose()
    {
        if (_ownsDataSource)
        {
            _dataSource.Dispose();
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = StatementJustification)]
    private async Task MigrateAsync()
    {
        var connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var command = new NpgsqlCommand(_sql.Migrate, connection, transaction);
                await using (command.ConfigureAwait(false))
                {
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await transaction.CommitAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> SettleAsync(
        string statement,
        PostgreSqlClaimedMessage message,
        Action<NpgsqlParameterCollection>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var command = _dataSource.CreateCommand(statement);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("id", message.Id);
            command.Parameters.AddWithValue("lock_token", message.LockToken);
            configure?.Invoke(command.Parameters);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
    }
}
