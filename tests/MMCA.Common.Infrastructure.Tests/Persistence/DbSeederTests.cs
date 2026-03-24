using FluentAssertions;
using MMCA.Common.Infrastructure.Persistence.DbContexts.Seeding;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class DbSeederTests
{
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
    }
}
