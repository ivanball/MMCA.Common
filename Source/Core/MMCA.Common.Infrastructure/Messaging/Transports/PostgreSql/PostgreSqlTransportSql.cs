using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MMCA.Common.Infrastructure.Messaging.Broker;

namespace MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

/// <summary>
/// Every SQL statement the PostgreSQL transport runs, built once for one schema and role. Three tables:
/// <list type="bullet">
///   <item><c>{schema}.subscriptions (topic, queue)</c>: which queues a topic fans out to.</item>
///   <item><c>{schema}.messages</c>: one row per queue per published message, claimed by a receiver with
///   <c>FOR UPDATE SKIP LOCKED</c> and a lease (<c>lock_token</c>, <c>locked_until</c>), and hidden until
///   <c>visible_after</c>.</item>
///   <item><c>{schema}.dead_letters</c>: dead-lettered rows, with the reason and detail.</item>
/// </list>
/// A publish inserts every subscribed queue's row in one statement and signals each queue with
/// <c>pg_notify</c> on <see cref="NotifyChannel"/> (the payload is the queue name), which wakes the
/// receivers listening on it. Every time comparison uses the server's clock (<c>now()</c>), so host clock
/// skew cannot release a lease early.
/// </summary>
internal sealed partial class PostgreSqlTransportSql
{
    /// <summary>PostgreSQL's identifier limit (NAMEDATALEN - 1).</summary>
    internal const int MaxIdentifierLength = 63;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlTransportSql"/> class.</summary>
    /// <param name="schema">The transport schema.</param>
    /// <param name="role">The role that owns the schema.</param>
    /// <exception cref="InvalidOperationException">The schema or the role is not a plain identifier.</exception>
    public PostgreSqlTransportSql(string schema, string role)
    {
        ValidateIdentifier(schema, "MessageBus:PostgreSql:Schema");
        ValidateIdentifier(role, "MessageBus:PostgreSql:Role");

        Schema = schema;
        Role = role;
        NotifyChannel = BrokerNaming.Fit(schema + "_messages", MaxIdentifierLength);

        var s = Quote(schema);
        Migrate = BuildMigrate(schema, role);

        Subscribe = Lines(
            $"INSERT INTO {s}.subscriptions (topic, queue)",
            "SELECT t.topic, @queue FROM unnest(@topics) AS t(topic)",
            "ON CONFLICT (topic, queue) DO NOTHING");

        Publish = Lines(
            "WITH inserted AS (",
            $"    INSERT INTO {s}.messages (queue, message_id, headers, body)",
            "    SELECT sub.queue, @message_id, @headers, @body",
            $"    FROM {s}.subscriptions AS sub",
            "    WHERE sub.topic = @topic",
            "    RETURNING queue)",
            "SELECT pg_notify(@channel, q.queue) FROM (SELECT DISTINCT queue FROM inserted) AS q");

        Claim = Lines(
            "WITH due AS (",
            $"    SELECT id FROM {s}.messages",
            "    WHERE queue = @queue",
            "      AND visible_after <= now()",
            "      AND (locked_until IS NULL OR locked_until <= now())",
            "    ORDER BY visible_after, id",
            "    LIMIT @limit",
            "    FOR UPDATE SKIP LOCKED)",
            $"UPDATE {s}.messages AS m",
            "SET lock_token = @lock_token, locked_until = now() + @lease, delivery_count = m.delivery_count + 1",
            "FROM due",
            "WHERE m.id = due.id",
            "RETURNING m.id, m.message_id, m.headers::text, m.body, m.delivery_count");

        Complete = $"DELETE FROM {s}.messages WHERE id = @id AND lock_token = @lock_token";

        Redeliver = Lines(
            $"UPDATE {s}.messages",
            "SET visible_after = now() + @delay, headers = @headers, lock_token = NULL, locked_until = NULL",
            "WHERE id = @id AND lock_token = @lock_token");

        DeadLetter = Lines(
            "WITH moved AS (",
            $"    DELETE FROM {s}.messages WHERE id = @id AND lock_token = @lock_token",
            "    RETURNING id, queue, message_id, headers, body, enqueued_at, delivery_count)",
            $"INSERT INTO {s}.dead_letters (id, queue, message_id, headers, body, enqueued_at, delivery_count, reason, detail)",
            "SELECT id, queue, message_id, headers, body, enqueued_at, delivery_count, @reason, @detail FROM moved");

        Release = $"UPDATE {s}.messages SET lock_token = NULL, locked_until = NULL WHERE id = @id AND lock_token = @lock_token";

        Renew = Lines(
            $"UPDATE {s}.messages AS m SET locked_until = now() + @lease",
            "FROM unnest(@ids, @lock_tokens) AS t(id, lock_token)",
            "WHERE m.id = t.id AND m.lock_token = t.lock_token");

        NextDue = Lines(
            "SELECT EXTRACT(EPOCH FROM (min(GREATEST(visible_after, COALESCE(locked_until, visible_after))) - now()))::double precision",
            $"FROM {s}.messages WHERE queue = @queue");

        Listen = "LISTEN " + Quote(NotifyChannel);
    }

    /// <summary>Gets the transport schema.</summary>
    public string Schema { get; }

    /// <summary>Gets the role that owns the schema.</summary>
    public string Role { get; }

    /// <summary>Gets the notification channel publishes signal on (the payload is the queue name).</summary>
    public string NotifyChannel { get; }

    /// <summary>Gets the idempotent schema migration (no parameters; run in one transaction).</summary>
    public string Migrate { get; }

    /// <summary>Gets the statement binding topics to a queue (<c>@queue</c>, <c>@topics</c>).</summary>
    public string Subscribe { get; }

    /// <summary>Gets the fan-out publish (<c>@topic</c>, <c>@message_id</c>, <c>@headers</c>, <c>@body</c>, <c>@channel</c>).</summary>
    public string Publish { get; }

    /// <summary>Gets the claim of due rows (<c>@queue</c>, <c>@limit</c>, <c>@lock_token</c>, <c>@lease</c>).</summary>
    public string Claim { get; }

    /// <summary>Gets the delete of a handled row (<c>@id</c>, <c>@lock_token</c>).</summary>
    public string Complete { get; }

    /// <summary>Gets the redelivery: push <c>visible_after</c> out, replace the headers, release the claim (<c>@id</c>, <c>@lock_token</c>, <c>@delay</c>, <c>@headers</c>).</summary>
    public string Redeliver { get; }

    /// <summary>Gets the move to <c>dead_letters</c> (<c>@id</c>, <c>@lock_token</c>, <c>@reason</c>, <c>@detail</c>).</summary>
    public string DeadLetter { get; }

    /// <summary>Gets the release of a claim without settling the row (<c>@id</c>, <c>@lock_token</c>).</summary>
    public string Release { get; }

    /// <summary>Gets the lease renewal of in-flight rows (<c>@ids</c>, <c>@lock_tokens</c>, <c>@lease</c>).</summary>
    public string Renew { get; }

    /// <summary>Gets the seconds until the queue's next row becomes claimable, or NULL when it is empty (<c>@queue</c>).</summary>
    public string NextDue { get; }

    /// <summary>Gets the LISTEN statement for <see cref="NotifyChannel"/>.</summary>
    public string Listen { get; }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdentifierPattern { get; }

    /// <summary>Double-quotes an identifier already checked by <see cref="ValidateIdentifier"/>.</summary>
    /// <param name="identifier">The identifier.</param>
    /// <returns>The quoted identifier.</returns>
    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Rejects a schema or role name that is not a plain identifier (a letter or underscore, then letters,
    /// digits or underscores, at most 63 characters), because both are written into DDL.
    /// </summary>
    /// <param name="value">The configured name.</param>
    /// <param name="key">The configuration key, for the message.</param>
    /// <exception cref="InvalidOperationException">The name is not a plain identifier.</exception>
    internal static void ValidateIdentifier(string value, string key)
    {
        if (string.IsNullOrEmpty(value) || !IdentifierPattern.IsMatch(value))
        {
            throw new InvalidOperationException(
                $"{key} is '{value}', which is not a plain PostgreSQL identifier: use a letter or underscore followed by letters, digits or underscores, at most {MaxIdentifierLength} characters.");
        }
    }

    /// <summary>
    /// The advisory-lock key that serializes concurrent migrations of one schema: the first eight bytes of
    /// a SHA-256 of the schema name, so every host computes the same key.
    /// </summary>
    /// <param name="schema">The schema.</param>
    /// <returns>The lock key.</returns>
    internal static long MigrationLockKey(string schema) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("mmca-broker-transport:" + schema)));

    private static string Lines(params string[] lines) => string.Join('\n', lines);

    /// <summary>
    /// The schema migration: under a transaction-scoped advisory lock, create the role when missing and
    /// grant it to the connecting user, create the schema owned by the role, then create the tables AS the
    /// role, so every member of it (each service's database user, once it has run this) owns them too.
    /// Every step is idempotent.
    /// </summary>
    private static string BuildMigrate(string schema, string role)
    {
        var s = Quote(schema);
        var r = Quote(role);
        var roleLiteral = "'" + role + "'";
        var lockKey = MigrationLockKey(schema).ToString(CultureInfo.InvariantCulture);

        return Lines(
            $"SELECT pg_advisory_xact_lock({lockKey});",
            "DO $mmca$",
            "BEGIN",
            $"    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = {roleLiteral}) THEN",
            $"        CREATE ROLE {r};",
            "    END IF;",
            $"    IF {roleLiteral} <> current_user AND NOT pg_catalog.pg_has_role(current_user, {roleLiteral}, 'MEMBER') THEN",
            $"        GRANT {r} TO CURRENT_USER;",
            "    END IF;",
            "END",
            "$mmca$;",
            $"CREATE SCHEMA IF NOT EXISTS {s} AUTHORIZATION {r};",
            $"SET LOCAL ROLE {r};",
            $"CREATE TABLE IF NOT EXISTS {s}.subscriptions (",
            "    topic text NOT NULL,",
            "    queue text NOT NULL,",
            "    created_at timestamptz NOT NULL DEFAULT now(),",
            "    PRIMARY KEY (topic, queue));",
            $"CREATE TABLE IF NOT EXISTS {s}.messages (",
            "    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,",
            "    queue text NOT NULL,",
            "    message_id uuid NOT NULL,",
            "    headers jsonb NOT NULL,",
            "    body bytea NOT NULL,",
            "    visible_after timestamptz NOT NULL DEFAULT now(),",
            "    enqueued_at timestamptz NOT NULL DEFAULT now(),",
            "    delivery_count integer NOT NULL DEFAULT 0,",
            "    lock_token uuid NULL,",
            "    locked_until timestamptz NULL);",
            $"CREATE INDEX IF NOT EXISTS ix_messages_queue_visible_after ON {s}.messages (queue, visible_after, id);",
            $"CREATE TABLE IF NOT EXISTS {s}.dead_letters (",
            "    id bigint PRIMARY KEY,",
            "    queue text NOT NULL,",
            "    message_id uuid NOT NULL,",
            "    headers jsonb NOT NULL,",
            "    body bytea NOT NULL,",
            "    enqueued_at timestamptz NOT NULL,",
            "    delivery_count integer NOT NULL,",
            "    reason text NOT NULL,",
            "    detail text NULL,",
            "    dead_lettered_at timestamptz NOT NULL DEFAULT now());",
            $"CREATE INDEX IF NOT EXISTS ix_dead_letters_queue ON {s}.dead_letters (queue, dead_lettered_at);",
            "RESET ROLE;");
    }
}
