using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.Persistence.DbContexts.Seeding;

public sealed class DbSeederTests
{
    private const string DuplicateKeyMessage =
        "Cannot insert duplicate key row in object 'Conference.Event' with unique index 'IX_Event_SessionizeCode'.";

    [Fact]
    public void GetId_WithIntType_ReturnsIntValue()
    {
        var result = TestableDbSeeder.TestGetId<int>(42);

        result.Should().Be(42);
    }

    [Fact]
    public void GetId_WithGuidType_ReturnsDeterministicGuid()
    {
        var result1 = TestableDbSeeder.TestGetId<Guid>(1);
        var result2 = TestableDbSeeder.TestGetId<Guid>(1);

        result1.Should().Be(result2);
        result1.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void GetId_WithGuidType_DifferentIds_ReturnDifferentGuids()
    {
        var result1 = TestableDbSeeder.TestGetId<Guid>(1);
        var result2 = TestableDbSeeder.TestGetId<Guid>(2);

        result1.Should().NotBe(result2);
    }

    [Fact]
    public void GetId_WithUnsupportedType_ThrowsNotSupportedException()
    {
        var act = () => TestableDbSeeder.TestGetId<string>(1);

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*String*");
    }

    [Fact]
    public async Task SeedAsync_CanBeImplemented()
    {
        var seeder = new TestableDbSeeder();
        await seeder.SeedAsync(CancellationToken.None);

        seeder.WasCalled.Should().BeTrue();
    }

    // -- SaveToleratingConcurrentSeedAsync --
    [Fact]
    public async Task SaveToleratingConcurrentSeedAsync_WhenTheSaveSucceeds_ReturnsTrue()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var saved = await TestableDbSeeder.TestSaveAsync(unitOfWork.Object);

        saved.Should().BeTrue();
        unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // Two replicas seeding together both pass the existence check; the slower save is rejected by
    // the unique index. That is a lost race: the call reports it and detaches the rejected rows so
    // the scoped context does not replay them on the next save.
    [Fact]
    public async Task SaveToleratingConcurrentSeedAsync_OnAUniqueViolation_ReturnsFalseAndDetachesTheFailedSave()
    {
        await using var context = new RaceProbeContext();
        var rejectedRow = new RaceProbeRow { Id = 1 };
        var capturedRow = new RaceProbeRow { Id = 2 };
        context.Add(rejectedRow);
        context.Add(capturedRow);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(UpdateFailure(DuplicateKeyMessage, context.Entry(rejectedRow)));

        var saved = await TestableDbSeeder.TestSaveAsync(unitOfWork.Object);

        saved.Should().BeFalse("a concurrent replica already inserted the rows");
        context.Entry(rejectedRow).State.Should().Be(EntityState.Detached);
        context.Entry(capturedRow).State.Should().Be(
            EntityState.Detached,
            "every pending entry of the failed context belongs to the rolled-back save");
    }

    [Fact]
    public async Task SaveToleratingConcurrentSeedAsync_OnAnyOtherFailure_PropagatesAndKeepsTheEntriesTracked()
    {
        await using var context = new RaceProbeContext();
        var row = new RaceProbeRow { Id = 1 };
        context.Add(row);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(UpdateFailure(
                "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_Session_Event\".",
                context.Entry(row)));

        var save = () => TestableDbSeeder.TestSaveAsync(unitOfWork.Object);

        await save.Should().ThrowAsync<DbUpdateException>();
        context.Entry(row).State.Should().Be(EntityState.Added, "only a lost race discards the pending save");
    }

    [Fact]
    public async Task SaveToleratingConcurrentSeedAsync_PrefersTheUnitOfWorksOwnDetector()
    {
        // The unit of work's engine-aware detector decides, not the SQL Server fallback: here it
        // refuses to classify a message the fallback would have read as a duplicate key.
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.As<IUniqueConstraintViolationDetector>()
            .Setup(x => x.IsUniqueConstraintViolation(It.IsAny<Exception>()))
            .Returns(false);
        unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(UpdateFailure(DuplicateKeyMessage));

        var save = () => TestableDbSeeder.TestSaveAsync(unitOfWork.Object);

        await save.Should().ThrowAsync<DbUpdateException>();
        unitOfWork.As<IUniqueConstraintViolationDetector>()
            .Verify(x => x.IsUniqueConstraintViolation(It.IsAny<Exception>()), Times.Once);
    }

    [Fact]
    public async Task SaveToleratingConcurrentSeedAsync_WithANullUnitOfWork_Throws()
    {
        var save = () => TestableDbSeeder.TestSaveAsync(null!);

        await save.Should().ThrowAsync<ArgumentNullException>();
    }

    private static DbUpdateException UpdateFailure(
        string providerMessage,
        params Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry[] entries) =>
        new(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            new InvalidOperationException(providerMessage),
            entries);

    private sealed class TestableDbSeeder : DbSeeder
    {
        public bool WasCalled { get; private set; }

        public override Task SeedAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }

        public static TIdentifier TestGetId<TIdentifier>(int id)
            where TIdentifier : notnull =>
            GetId<TIdentifier>(id);

        public static Task<bool> TestSaveAsync(IUnitOfWork unitOfWork) =>
            SaveToleratingConcurrentSeedAsync(unitOfWork, CancellationToken.None);
    }

    /// <summary>
    /// A real change tracker, so a failed save can carry genuine entries and the test can observe
    /// what was detached. Never opened: tracking needs no connection.
    /// </summary>
    private sealed class RaceProbeContext() : DbContext(
        new DbContextOptionsBuilder<RaceProbeContext>().UseSqlite("DataSource=:memory:").Options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<RaceProbeRow>();
    }

    private sealed class RaceProbeRow
    {
        public int Id { get; set; }
    }
}
