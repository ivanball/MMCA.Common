using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Services.Query;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Tests.Persistence.Specifications;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories.Read;

/// <summary>
/// Security tests for the lookup projection: it is capped at the framework's row ceiling like every
/// other read (SEC-Common-24), and its compiled-selector cache is keyed on the property's canonical
/// name so a caller cannot mint one never-evicted entry per case permutation (SEC-Store-14).
/// </summary>
public sealed class EFReadRepositoryLookupSecurityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;
    private readonly EFReadRepository<SpecTestEntity, int> _sut;

    public EFReadRepositoryLookupSecurityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SpecificationTestDbContext(
            new DbContextOptionsBuilder<SpecificationTestDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _sut = new EFReadRepository<SpecTestEntity, int>(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private void Seed(int rows)
    {
        for (var i = 1; i <= rows; i++)
        {
            _context.Add(new SpecTestEntity { Id = i, Name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"row-{i:D5}"), Rank = i });
        }

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Reads the private static selector cache by reflection. Asserting on the CACHE is the only way
    /// to see the finding: two spellings that both work still differ in how many entries they mint.
    /// </summary>
    private static ConcurrentDictionary<(Type EntityType, string PropertyName), LambdaExpression> SelectorCache()
    {
        var field = typeof(EFReadRepository<SpecTestEntity, int>)
            .GetField("LookupSelectorCache", BindingFlags.NonPublic | BindingFlags.Static);

        field.Should().NotBeNull();
        return (ConcurrentDictionary<(Type, string), LambdaExpression>)field!.GetValue(null)!;
    }

    [Fact]
    public async Task GetAllForLookup_IsCappedAtTheFrameworkRowCeiling()
    {
        Seed(EntityQueryPipeline.MaxUnboundedResultLimit + 25);

        var lookups = await _sut.GetAllForLookupAsync("Name", cancellationToken: TestContext.Current.CancellationToken);

        // Before the cap this path returned EVERY row, unpaginated: it never entered the pipeline
        // that enforces the ceiling for every other read.
        lookups.Should().HaveCount(EntityQueryPipeline.MaxUnboundedResultLimit);
    }

    [Fact]
    public async Task GetAllForLookup_MisspelledCasingCollapsesOntoOneCacheEntry()
    {
        Seed(3);
        var cache = SelectorCache();
        var before = cache.Count;

        foreach (var spelling in new[] { "Name", "name", "NAME", "nAmE", "NaMe" })
        {
            var lookups = await _sut.GetAllForLookupAsync(spelling, cancellationToken: TestContext.Current.CancellationToken);
            lookups.Should().HaveCount(3);
        }

        // Five spellings of one real column: one entry, not five. That is what stops an anonymous
        // caller growing a process-lifetime dictionary by permuting case.
        (cache.Count - before).Should().Be(1);
        cache.Keys.Should().Contain(key => key.EntityType == typeof(SpecTestEntity) && key.PropertyName == "Name");
        cache.Keys.Should().NotContain(key => key.PropertyName == "nAmE");
    }

    [Fact]
    public async Task GetAllForLookup_StillProjectsTheNamedColumn()
    {
        Seed(2);

        var lookups = await _sut.GetAllForLookupAsync("Rank", cancellationToken: TestContext.Current.CancellationToken);

        lookups.Select(l => l.Name).Should().BeEquivalentTo("1", "2");
    }
}
