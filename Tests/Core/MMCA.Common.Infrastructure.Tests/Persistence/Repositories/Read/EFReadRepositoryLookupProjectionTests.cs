using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Infrastructure.Persistence.Repositories;
using MMCA.Common.Infrastructure.Tests.Persistence.Specifications;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Repositories.Read;

/// <summary>
/// Regression tests for the lookup projection over a NON-string name property (ledger CD-2). The
/// projection used to append <c>ToString()</c> server-side, which EF cannot translate for a
/// value-object property: the whole query threw and the lookup endpoint answered HTTP 500 for a
/// property <c>QueryFieldService</c> had already approved. The raw value now comes back in its own
/// CLR type and is formatted after materialization, so every mapped property type projects.
/// </summary>
public sealed class EFReadRepositoryLookupProjectionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SpecificationTestDbContext _context;
    private readonly EFReadRepository<SpecTestEntity, int> _sut;

    public EFReadRepositoryLookupProjectionTests()
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

    private void Seed(params string?[] contactEmails)
    {
        for (var i = 0; i < contactEmails.Length; i++)
        {
            var raw = contactEmails[i];
            _context.Add(new SpecTestEntity
            {
                Id = i + 1,
                Name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"row-{i + 1}"),
                Rank = i + 1,
                ContactEmail = raw is null ? null : Email.Create(raw).Value,
            });
        }

        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task GetAllForLookup_ProjectsAValueObjectProperty()
    {
        Seed("Second@Example.com", "first@example.com");

        var lookups = await _sut.GetAllForLookupAsync(
            "ContactEmail",
            cancellationToken: TestContext.Current.CancellationToken);

        // The value object's string form, ordered by the converted column server-side.
        lookups.Select(l => l.Name).Should().Equal("first@example.com", "second@example.com");
        lookups.Select(l => l.Id).Should().Equal(2, 1);
    }

    [Fact]
    public async Task GetAllForLookup_ProjectsANullValueObjectAsEmpty()
    {
        Seed(null, "only@example.com");

        var lookups = await _sut.GetAllForLookupAsync(
            "ContactEmail",
            cancellationToken: TestContext.Current.CancellationToken);

        lookups.Should().HaveCount(2);
        lookups.Select(l => l.Name).Should().Contain(string.Empty);
    }

    [Fact]
    public async Task GetAllForLookup_ProjectsAnIntProperty()
    {
        Seed("a@example.com", "b@example.com", "c@example.com");

        var lookups = await _sut.GetAllForLookupAsync(
            "Rank",
            cancellationToken: TestContext.Current.CancellationToken);

        lookups.Select(l => l.Name).Should().Equal("1", "2", "3");
    }

    [Fact]
    public async Task GetAllForLookup_StillProjectsAStringProperty()
    {
        Seed("a@example.com", "b@example.com");

        var lookups = await _sut.GetAllForLookupAsync(
            "Name",
            cancellationToken: TestContext.Current.CancellationToken);

        lookups.Select(l => l.Name).Should().Equal("row-1", "row-2");
    }
}
