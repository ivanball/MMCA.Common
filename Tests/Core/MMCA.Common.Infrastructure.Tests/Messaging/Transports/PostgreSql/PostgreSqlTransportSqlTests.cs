using System.Globalization;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.PostgreSql;

/// <summary>
/// The shape of the PostgreSQL transport's SQL: quoted schema, the SKIP LOCKED claim under a lease, the
/// one-statement fan-out with notify, lease-guarded settles, and the idempotent migration. The
/// PostgreSQL tier runs the same statements against a real server.
/// </summary>
public sealed class PostgreSqlTransportSqlTests
{
    private static readonly PostgreSqlTransportSql Sql = new("transport", "transport");

    [Fact]
    public void Claim_SkipsLockedRows_HonorsVisibilityAndLease_AndTakesALease() =>
        Sql.Claim.Should().Contain("FOR UPDATE SKIP LOCKED")
            .And.Contain("FROM \"transport\".messages")
            .And.Contain("visible_after <= now()")
            .And.Contain("(locked_until IS NULL OR locked_until <= now())")
            .And.Contain("LIMIT @limit")
            .And.Contain("lock_token = @lock_token, locked_until = now() + @lease")
            .And.Contain("delivery_count = m.delivery_count + 1")
            .And.Contain("ORDER BY visible_after, id");

    [Fact]
    public void Publish_FansOutToEverySubscribedQueueInOneStatement_AndNotifiesThem()
    {
        Sql.Publish.Should().StartWith("WITH inserted AS (")
            .And.Contain("INSERT INTO \"transport\".messages (queue, message_id, headers, body)")
            .And.Contain("FROM \"transport\".subscriptions AS sub")
            .And.Contain("WHERE sub.topic = @topic")
            .And.Contain("pg_notify(@channel, q.queue)");
        Sql.Publish.Split(';').Should().ContainSingle("the fan-out is one statement");
    }

    [Fact]
    public void Subscribe_IsIdempotent() =>
        Sql.Subscribe.Should().Contain("INSERT INTO \"transport\".subscriptions (topic, queue)")
            .And.Contain("ON CONFLICT (topic, queue) DO NOTHING");

    [Fact]
    public void SettlingStatements_MatchTheClaimsLockToken()
    {
        foreach (var statement in new[] { Sql.Complete, Sql.Redeliver, Sql.DeadLetter, Sql.Release })
        {
            statement.Should().Contain("id = @id AND lock_token = @lock_token");
        }

        Sql.Renew.Should().Contain("m.lock_token = t.lock_token");
    }

    [Fact]
    public void Redeliver_PushesVisibilityOut_ReplacesHeaders_AndReleasesTheClaim() =>
        Sql.Redeliver.Should().Contain("visible_after = now() + @delay")
            .And.Contain("headers = @headers")
            .And.Contain("lock_token = NULL, locked_until = NULL");

    [Fact]
    public void DeadLetter_MovesTheRowWithReasonAndDetail_InOneStatement()
    {
        Sql.DeadLetter.Should().Contain("DELETE FROM \"transport\".messages")
            .And.Contain("INSERT INTO \"transport\".dead_letters")
            .And.Contain("@reason, @detail");
        Sql.DeadLetter.Split(';').Should().ContainSingle();
    }

    [Fact]
    public void Migrate_IsIdempotent_Serialized_AndOwnedByTheRole()
    {
        Sql.Migrate.Should().Contain("pg_advisory_xact_lock(" + PostgreSqlTransportSql.MigrationLockKey("transport").ToString(CultureInfo.InvariantCulture))
            .And.Contain("CREATE ROLE \"transport\"")
            .And.Contain("GRANT \"transport\" TO CURRENT_USER")
            .And.Contain("CREATE SCHEMA IF NOT EXISTS \"transport\" AUTHORIZATION \"transport\"")
            .And.Contain("SET LOCAL ROLE \"transport\"")
            .And.Contain("CREATE TABLE IF NOT EXISTS \"transport\".subscriptions")
            .And.Contain("CREATE TABLE IF NOT EXISTS \"transport\".messages")
            .And.Contain("CREATE TABLE IF NOT EXISTS \"transport\".dead_letters")
            .And.Contain("CREATE INDEX IF NOT EXISTS ix_messages_queue_visible_after");
        Sql.Migrate.Should().NotContain("DROP");
    }

    [Fact]
    public void ConfiguredSchemaAndRole_AreQuotedEverywhere()
    {
        var sql = new PostgreSqlTransportSql("Broker_1", "svc_role");

        sql.Claim.Should().Contain("\"Broker_1\".messages");
        sql.Migrate.Should().Contain("AUTHORIZATION \"svc_role\"").And.Contain("rolname = 'svc_role'");
        sql.NotifyChannel.Should().Be("Broker_1_messages");
        sql.Listen.Should().Be("LISTEN \"Broker_1_messages\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1schema")]
    [InlineData("bad-name")]
    [InlineData("x\"; DROP TABLE t; --")]
    [InlineData("a b")]
    public void NotAPlainIdentifier_IsRejectedNamingTheKey(string schema)
    {
        var act = () => new PostgreSqlTransportSql(schema, "transport");

        act.Should().Throw<InvalidOperationException>().WithMessage("MessageBus:PostgreSql:Schema*plain PostgreSQL identifier*");
    }

    [Fact]
    public void TooLongIdentifier_IsRejected()
    {
        var act = () => new PostgreSqlTransportSql("transport", new string('r', 64));

        act.Should().Throw<InvalidOperationException>().WithMessage("MessageBus:PostgreSql:Role*");
    }

    [Fact]
    public void LongSchema_FitsTheNotifyChannelIntoTheIdentifierLimit()
    {
        var sql = new PostgreSqlTransportSql(new string('s', 63), "transport");

        sql.NotifyChannel.Length.Should().BeLessThanOrEqualTo(PostgreSqlTransportSql.MaxIdentifierLength);
    }

    [Fact]
    public void MigrationLockKey_IsStablePerSchema()
    {
        PostgreSqlTransportSql.MigrationLockKey("transport").Should().Be(PostgreSqlTransportSql.MigrationLockKey("transport"));
        PostgreSqlTransportSql.MigrationLockKey("transport").Should().NotBe(PostgreSqlTransportSql.MigrationLockKey("other"));
    }
}
