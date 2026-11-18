using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.DbContexts;
using MMCA.Common.Infrastructure.Persistence.Outbox;
using MMCA.Common.Infrastructure.Persistence.Retention;

namespace MMCA.Common.Tests.Shared.Persistence;

/// <summary>
/// The provider-translation check for <c>BatchedDelete</c>, run unchanged by each engine tier's own
/// <c>BatchedDelete_TranslatesAndDeletesInBatches</c> test against its real server.
/// <para>
/// Linked as source into the SQL Server and PostgreSQL provider test projects (see the
/// <c>Compile Include</c> in each project file), so every project compiles its own internal copy.
/// </para>
/// </summary>
internal static class BatchedDeleteScenario
{
    public static async Task TranslatesAndDeletesInBatchesAsync(Func<ApplicationDbContext> createContext)
    {
        var ct = TestContext.Current.CancellationToken;
        var cutoff = DateTime.UtcNow.AddDays(-1);

        await using (var seed = createContext())
        {
            for (var index = 0; index < 25; index++)
            {
                seed.Add(ProcessedOutboxRow(cutoff.AddHours(-1 - index)));
            }

            seed.Add(ProcessedOutboxRow(DateTime.UtcNow));
            await seed.SaveChangesAsync(1, ct);
        }

        await using var sweep = createContext();
        var batches = new List<int>();
        await foreach (var deleted in BatchedDelete.DeleteBatchesAsync<OutboxMessage, DateTime?>(
            sweep,
            m => m.ProcessedOn != null && m.ProcessedOn < cutoff,
            m => m.ProcessedOn,
            batchSize: 10,
            ct))
        {
            batches.Add(deleted);
        }

        batches.Should().Equal(10, 10, 5);

        await using var reader = createContext();
        (await reader.Set<OutboxMessage>().CountAsync(ct)).Should().Be(1, "the row inside the retention window stays");
    }

    private static OutboxMessage ProcessedOutboxRow(DateTime processedOn) => new()
    {
        EventType = "BatchedDeleteFixture",
        Payload = "{}",
        OccurredOn = processedOn.AddMinutes(-1),
        ProcessedOn = processedOn,
    };
}
