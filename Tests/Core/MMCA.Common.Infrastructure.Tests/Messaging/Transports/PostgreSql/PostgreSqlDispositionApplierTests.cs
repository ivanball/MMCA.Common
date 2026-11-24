using AwesomeAssertions;
using MMCA.Common.Infrastructure.Messaging.Broker;
using MMCA.Common.Infrastructure.Messaging.Transports.PostgreSql;

namespace MMCA.Common.Infrastructure.Tests.Messaging.Transports.PostgreSql;

/// <summary>Each disposition maps to its one settling statement on the claimed row.</summary>
public sealed class PostgreSqlDispositionApplierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Complete_DeletesTheRow()
    {
        var store = new FakePostgreSqlMessageStore();
        var row = store.Enqueue();

        (await PostgreSqlDispositionApplier.ApplyAsync(store, row, BrokerDisposition.Complete, Ct)).Should().BeTrue();

        store.Completed.Should().ContainSingle().Which.Should().BeSameAs(row);
        store.Redelivered.Should().BeEmpty();
        store.DeadLettered.Should().BeEmpty();
    }

    [Fact]
    public async Task Redeliver_HidesTheRowForTheDelay_WithTheNewCount_KeepingTheOtherHeaders()
    {
        var store = new FakePostgreSqlMessageStore();
        var row = store.Enqueue();

        await PostgreSqlDispositionApplier.ApplyAsync(store, row, new BrokerDisposition.Redeliver(TimeSpan.FromMinutes(1), 3), Ct);

        var (message, delay, headers) = store.Redelivered.Should().ContainSingle().Subject;
        message.Should().BeSameAs(row);
        delay.Should().Be(TimeSpan.FromMinutes(1));
        headers.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [BrokerHeaders.EventName] = "order-placed",
            [BrokerHeaders.RedeliveryCount] = "3",
        });
        row.Headers.Should().NotContainKey(BrokerHeaders.RedeliveryCount, "the claimed row's own headers are not mutated");
    }

    [Fact]
    public async Task DeadLetter_MovesTheRowWithReasonAndDetail()
    {
        var store = new FakePostgreSqlMessageStore();
        var row = store.Enqueue();

        await PostgreSqlDispositionApplier.ApplyAsync(store, row, new BrokerDisposition.DeadLetter("poison", "JsonException: bad"), Ct);

        store.DeadLettered.Should().ContainSingle().Which.Should().Be((row, "poison", (string?)"JsonException: bad"));
    }

    [Fact]
    public async Task LostLease_IsReportedAsNotSettled()
    {
        var store = new FakePostgreSqlMessageStore { SettleResult = false };

        (await PostgreSqlDispositionApplier.ApplyAsync(store, store.Enqueue(), BrokerDisposition.Complete, Ct)).Should().BeFalse();
    }
}
