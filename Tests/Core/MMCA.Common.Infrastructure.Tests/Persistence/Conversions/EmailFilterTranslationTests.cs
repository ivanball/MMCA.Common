using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Services.Filtering;
using MMCA.Common.Infrastructure.Persistence.Conversions;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Conversions;

/// <summary>
/// The database half of the Email value-object filter (grid-filter audit, run 9, item C). The value
/// object is stored through <see cref="EmailValueConverter"/> (<c>HasConversion</c> onto one string
/// column), which EF Core cannot see through with member access (<c>e.Email.Value</c> does not
/// translate), so <see cref="QueryFilterService"/> reads the column through a <c>(string)(object)</c>
/// cast against a database. These tests run the filters against a real SQLite database and render
/// the SQL Server query, so "the expression works in memory" is proven to also mean "it translates".
/// </summary>
public sealed class EmailFilterTranslationTests : IDisposable
{
    private static readonly Dictionary<string, string> EmptyMap = [];

    private readonly CustomerContext _sqlite = CustomerContext.CreateSqlite();

    public EmailFilterTranslationTests()
    {
        _sqlite.Customers.AddRange(
            new Customer { Id = 1, Name = "Ada", Email = Email.Create("ada@example.com").Value!, BackupEmail = null },
            new Customer { Id = 2, Name = "Grace", Email = Email.Create("grace@example.org").Value!, BackupEmail = Email.Create("g@example.net").Value },
            new Customer { Id = 3, Name = "Alan", Email = Email.Create("alan@example.org").Value!, BackupEmail = null });
        _sqlite.SaveChanges();
        _sqlite.ChangeTracker.Clear();
    }

    public void Dispose() => _sqlite.Dispose();

    private static Dictionary<string, (string Operator, string Value)> One(string property, string op, string value) =>
        new() { [property] = (op, value) };

    private List<string> NamesMatching(string property, string op, string value) =>
        [.. QueryFilterService.ApplyFilters(_sqlite.Customers.OrderBy(c => c.Id), One(property, op, value), EmptyMap).Select(c => c.Name)];

    [Theory]
    [InlineData("contains", "example.org", new[] { "Grace", "Alan" })]
    [InlineData("CONTAINS", "EXAMPLE.ORG", new[] { "Grace", "Alan" })]
    [InlineData("not contains", "example.org", new[] { "Ada" })]
    [InlineData("equals", "ada@example.com", new[] { "Ada" })]
    [InlineData("equals", " Ada@Example.com ", new[] { "Ada" })]
    [InlineData("not equals", "ada@example.com", new[] { "Grace", "Alan" })]
    [InlineData("starts with", "al", new[] { "Alan" })]
    [InlineData("ends with", ".com", new[] { "Ada" })]
    public void Sqlite_FiltersTheRequiredEmailColumnLikeAString(string op, string value, string[] expected)
    {
        QueryFilterService.ValidateFilters<Customer>(One(nameof(Customer.Email), op, value), EmptyMap).IsSuccess.Should().BeTrue();

        NamesMatching(nameof(Customer.Email), op, value).Should().Equal(expected);
    }

    [Theory]
    [InlineData("is empty", new[] { "Ada", "Alan" })]
    [InlineData("is not empty", new[] { "Grace" })]
    [InlineData("contains", new[] { "Grace" })]
    public void Sqlite_FiltersTheOptionalEmailColumn(string op, string[] expected) =>
        NamesMatching(nameof(Customer.BackupEmail), op, "example.net").Should().Equal(expected);

    [Fact]
    public void SqlServer_TranslatesContainsToALikeOverTheStoredColumn()
    {
        using var context = CustomerContext.CreateSqlServer();

        var sql = QueryFilterService.ApplyFilters(context.Customers, One(nameof(Customer.Email), "contains", "example.org"), EmptyMap)
            .ToQueryString();

        sql.Should().Contain("[Email]").And.Contain("LIKE");
    }

    public sealed class Customer
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public Email Email { get; set; } = default!;

        public Email? BackupEmail { get; set; }
    }

    /// <summary>A plain context mapping the value object exactly as the converter's usage note says.</summary>
    private sealed class CustomerContext(DbContextOptions<CustomerContext> options) : DbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();

        public static CustomerContext CreateSqlite()
        {
            var context = new CustomerContext(new DbContextOptionsBuilder<CustomerContext>().UseSqlite("DataSource=:memory:").Options);
            context.Database.OpenConnection();
            context.Database.EnsureCreated();
            return context;
        }

        public static CustomerContext CreateSqlServer() =>
            new(new DbContextOptionsBuilder<CustomerContext>().UseSqlServer("Server=unused;Database=unused;").Options);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Customer>(entity =>
            {
                entity.HasKey(c => c.Id);
                entity.Property(c => c.Id).ValueGeneratedNever();
                entity.Property(c => c.Email).HasConversion(new EmailValueConverter()).HasMaxLength(EmailInvariants.MaxLength);
                entity.Property(c => c.BackupEmail).HasConversion(new NullableEmailValueConverter()).HasMaxLength(EmailInvariants.MaxLength);
            });
    }
}
