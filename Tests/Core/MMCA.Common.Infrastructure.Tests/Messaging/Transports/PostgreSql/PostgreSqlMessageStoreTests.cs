using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;
using Npgsql;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.PostgreSql;

/// <summary>
/// The Npgsql message store's guards that run before any statement: nothing here reaches a server, so
/// the data source points at a port nothing listens on, and a test that opened a connection would fail.
/// The statements themselves run against a real PostgreSQL in the <c>postgresql-integration</c> tier.
/// </summary>
public sealed class PostgreSqlMessageStoreTests
{
    private const string UnreachableConnectionString = "Host=127.0.0.1;Port=1;Database=broker;Username=u;Password=p;Timeout=1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PostgreSqlMessageStore Store() =>
        new(NpgsqlDataSource.Create(UnreachableConnectionString), ownsDataSource: true, new PostgreSqlTransportSql("transport", "transport"));

    [Fact]
    public async Task RenewAsync_NoMessagesInFlight_ReturnsWithoutTouchingTheDatabase()
    {
        await using var store = Store();

        var act = () => store.RenewAsync([], TimeSpan.FromSeconds(60), Ct);

        await act.Should().NotThrowAsync("an idle receiver's renewal tick must not cost a round trip");
    }

    [Fact]
    public async Task RenewAsync_NullMessages_Throws()
    {
        await using var store = Store();

        var act = () => store.RenewAsync(null!, TimeSpan.FromSeconds(60), Ct);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("messages");
    }

    [Fact]
    public async Task RenewAsync_WithMessages_RunsTheStatement_AndSurfacesAnUnreachableServer()
    {
        await using var store = Store();
        PostgreSqlClaimedMessage[] inFlight = [new(1, Guid.NewGuid(), Guid.NewGuid(), new Dictionary<string, string>(StringComparer.Ordinal), [1], 1)];

        var act = () => store.RenewAsync(inFlight, TimeSpan.FromSeconds(60), Ct);

        // The receiver's renew loop logs this and tries again on the next tick.
        await act.Should().ThrowAsync<NpgsqlException>();
    }
}
