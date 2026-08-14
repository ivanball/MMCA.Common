using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration.Notifications;

namespace MMCA.Common.Infrastructure.Tests.Persistence.Configuration;

/// <summary>
/// Tests for <c>PushNotificationConfiguration</c>'s deduplication index. The index is unique and
/// hand-filtered, and <c>SoftDeleteUniqueIndexConvention</c> deliberately leaves a hand-authored
/// filter alone, so this configuration has to opt into the soft-delete predicate itself. Without it
/// a soft-deleted notification keeps occupying its dedup slot and blocks every later send under the
/// same key (BugHunt M58). The scope-key column is pinned here too, including the deliberate absence
/// of an index on it.
/// </summary>
public sealed class PushNotificationConfigurationTests : IDisposable
{
    private readonly PushNotificationTestDbContext _dbContext = PushNotificationTestDbContext.Create();

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public void DedupKeyIndex_IsUnique()
        => DedupKeyIndex().IsUnique.Should().BeTrue("the database is what arbitrates a retried send");

    [Fact]
    public void DedupKeyIndex_FiltersOutSoftDeletedRows()
        => DedupKeyIndex().GetFilter().Should().Be(
            "[DedupKey] IS NOT NULL AND [IsDeleted] = 0",
            "a soft-deleted notification must not keep occupying its dedup slot");

    // ── ScopeKey column ──
    // The scope key is an opaque view filter, so it gets a bounded nullable column and, unlike the
    // dedup key, no index at all: the filter runs after the primary-key join from UserNotification.
    [Fact]
    public void ScopeKey_IsBoundedToTheDomainMaximum()
        => ScopeKeyProperty().GetMaxLength().Should().Be(PushNotification.ScopeKeyMaxLength);

    [Fact]
    public void ScopeKey_IsNullable()
        => ScopeKeyProperty().IsNullable.Should().BeTrue("an unscoped send is the default and stores NULL");

    [Fact]
    public void ScopeKey_IsNotIndexed()
        => _dbContext.Model.FindEntityType(typeof(PushNotification))!
            .GetIndexes()
            .Should().NotContain(
                i => i.Properties.Any(p => p.Name == nameof(PushNotification.ScopeKey)),
                "the table holds one row per send, so an index would cost writes without buying a read");

    private Microsoft.EntityFrameworkCore.Metadata.IProperty ScopeKeyProperty()
        => _dbContext.Model.FindEntityType(typeof(PushNotification))!
            .FindProperty(nameof(PushNotification.ScopeKey))!;

    private Microsoft.EntityFrameworkCore.Metadata.IIndex DedupKeyIndex()
        => _dbContext.Model.FindEntityType(typeof(PushNotification))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(PushNotification.DedupKey));

    public sealed class PushNotificationTestDbContext : DbContext
    {
        private PushNotificationTestDbContext(DbContextOptions<PushNotificationTestDbContext> options)
            : base(options)
        {
        }

        public static PushNotificationTestDbContext Create()
        {
            var options = new DbContextOptionsBuilder<PushNotificationTestDbContext>()
                .UseSqlite("DataSource=:memory:")
                .Options;

            return new PushNotificationTestDbContext(options);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            // The real configuration, applied directly: the assertions below pin what it declares,
            // not what a rewritten test double declares.
            modelBuilder.ApplyConfiguration(new PushNotificationConfiguration());
        }
    }
}
