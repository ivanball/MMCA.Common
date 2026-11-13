using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Interfaces.Navigation;
using MMCA.Common.Application.Services;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Tests.Persistence.Tenancy;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories;

/// <summary>
/// The changed-since read end to end over SQLite, through the real query service and the real
/// repository with both named query filters in force: soft-deleted rows come back as tombstones while
/// the tenant filter still holds, and a keyset walk over rows that share one <c>LastModifiedOn</c>
/// neither skips nor repeats a row.
/// </summary>
public sealed class ChangeFeedQueryTests : IDisposable
{
    private const string Acme = "acme";
    private const string Globex = "globex";

    private readonly SqliteConnection _connection = TenantTestContext.OpenDatabase();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SoftDeletedRowReturned_TenantFilterKept()
    {
        await SeedAsync();
        await using var acme = TenantTestContext.Create(_connection, () => Acme);
        var sut = CreateQueryService(acme);

        var result = await sut.GetChangesSinceAsync(since: null, cursor: null, pageSize: 100);

        result.IsSuccess.Should().BeTrue();
        List<EntityChange<ThingChangeDTO, int>> changes = [.. result.Value!.Items];
        changes.Select(c => c.Id).Should().BeEquivalentTo(
            [1, 2, 3, 4, 5],
            "the acme rows come back, deleted one included, and globex's row never does");

        EntityChange<ThingChangeDTO, int> tombstone = changes.Single(c => c.Id == 2);
        tombstone.IsDeleted.Should().BeTrue();
        tombstone.Item.Should().BeNull();
        changes.Where(c => c.Id != 2).Should().AllSatisfy(c =>
        {
            c.IsDeleted.Should().BeFalse();
            c.Item.Should().NotBeNull();
        });
        result.Value.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task PagingAcrossEqualTimestamps_NeitherSkipsNorRepeats()
    {
        await SeedAsync();
        await using var acme = TenantTestContext.Create(_connection, () => Acme);
        var sut = CreateQueryService(acme);

        List<EntityChange<ThingChangeDTO, int>> walked = [];
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var result = await sut.GetChangesSinceAsync(since: null, cursor: cursor, pageSize: 2);
            result.IsSuccess.Should().BeTrue();
            walked.AddRange(result.Value!.Items);
            cursor = result.Value.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        cursor.Should().BeNull("the walk must terminate");
        walked.Select(c => c.Id).Should().OnlyHaveUniqueItems("a page boundary inside a shared timestamp must not repeat a row");
        walked.Select(c => c.Id).Should().Equal(
            [1, 3, 4, 5, 2],
            "rows sharing one LastModifiedOn are ordered by Id, and the row deleted later comes last");
        walked.Take(4).Select(c => c.ChangedOn).Distinct().Should().ContainSingle(
            "the four live rows were saved together, so the walk really did page across one shared timestamp");
        walked.Select(c => c.ChangedOn).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Since_ReturnsOnlyRowsChangedAtOrAfterIt()
    {
        await SeedAsync();
        await using var acme = TenantTestContext.Create(_connection, () => Acme);
        var sut = CreateQueryService(acme);

        var all = await sut.GetChangesSinceAsync(since: null, cursor: null, pageSize: 100);
        DateTime deletedAt = all.Value!.Items.Single(c => c.Id == 2).ChangedOn;

        var result = await sut.GetChangesSinceAsync(since: deletedAt, cursor: null, pageSize: 100);

        result.Value!.Items.Select(c => c.Id).Should().Equal(
            [2],
            "only the delete happened at or after its own timestamp, and since is inclusive");
    }

    [Fact]
    public async Task RowLeavingScope_YieldsNoTombstone()
    {
        await SeedAsync();

        // Row 3 leaves the scope (every name but "moved") by an ordinary update, not a delete.
        await using (var system = TenantTestContext.Create(_connection, tenantAccessor: null))
        {
            var moved = await system.Things.SingleAsync(t => t.Id == 3);
            moved.Name = "moved";
            await system.SaveChangesAsync(null);
        }

        await using var acme = TenantTestContext.Create(_connection, () => Acme);
        var sut = CreateQueryService(acme);
        var scope = new InlineSpecification<TenantThing, int>(t => t.Name != "moved");

        var result = await sut.GetChangesSinceAsync(since: null, cursor: null, pageSize: 100, specification: scope);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Select(c => c.Id).Should().NotContain(
            3,
            "a row that left the read scope without being deleted is simply absent (documented limitation), not a tombstone");
    }

    // -- Helpers --

    /// <summary>
    /// Seeds, from a system (untenanted) context: five acme rows in ONE save, so they share a single
    /// LastModifiedOn, and one globex row; then soft-deletes acme row 2 in a later save.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var system = TenantTestContext.Create(_connection, tenantAccessor: null);

        system.Things.AddRange(
            new TenantThing { Id = 1, TenantId = Acme, Name = "keep-1" },
            new TenantThing { Id = 2, TenantId = Acme, Name = "keep-2" },
            new TenantThing { Id = 3, TenantId = Acme, Name = "keep-3" },
            new TenantThing { Id = 4, TenantId = Acme, Name = "keep-4" },
            new TenantThing { Id = 5, TenantId = Acme, Name = "keep-5" },
            new TenantThing { Id = 6, TenantId = Globex, Name = "keep-6" });
        await system.SaveChangesAsync(null);

        // A distinct, later instant for the delete stamp, whatever the clock's resolution.
        await Task.Delay(TimeSpan.FromMilliseconds(20));

        var deleted = await system.Things.SingleAsync(t => t.Id == 2);
        deleted.Delete().IsSuccess.Should().BeTrue();
        await system.SaveChangesAsync(null);
    }

    private static EntityQueryService<TenantThing, ThingChangeDTO, int> CreateQueryService(TenantTestContext context)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.GetReadRepository<TenantThing, int>())
            .Returns(new EFReadRepository<TenantThing, int>(context));

        return new EntityQueryService<TenantThing, ThingChangeDTO, int>(
            unitOfWork.Object,
            Mock.Of<INavigationMetadataProvider>(),
            Mock.Of<IEntityQueryPipeline>(),
            new ThingMapper(),
            Mock.Of<INavigationPopulator<TenantThing>>());
    }

    /// <summary>The DTO the feed maps live rows to.</summary>
    public sealed class ThingChangeDTO : IBaseDTO<int>
    {
        public required int Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private sealed class ThingMapper : IEntityDTOMapper<TenantThing, ThingChangeDTO, int>
    {
        public ThingChangeDTO MapToDTO(TenantThing entity) => new() { Id = entity.Id, Name = entity.Name };

        public IReadOnlyCollection<ThingChangeDTO> MapToDTOs(IReadOnlyCollection<TenantThing> entityCollection) =>
            [.. entityCollection.Select(MapToDTO)];
    }
}
