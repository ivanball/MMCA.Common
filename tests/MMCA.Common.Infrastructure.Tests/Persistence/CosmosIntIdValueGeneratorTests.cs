using FluentAssertions;
using MMCA.Common.Infrastructure.Persistence.ValueGenerators;

namespace MMCA.Common.Infrastructure.Tests.Persistence;

public sealed class CosmosIntIdValueGeneratorTests
{
    [Fact]
    public void GeneratesTemporaryValues_ReturnsFalse() =>
        new CosmosIntIdValueGenerator().GeneratesTemporaryValues.Should().BeFalse();

    [Fact]
    public void Next_ReturnsIncrementingValues()
    {
        var generator = new CosmosIntIdValueGenerator();

        var first = generator.Next(null!);
        var second = generator.Next(null!);

        second.Should().Be(first + 1);
    }

    [Fact]
    public void Next_ReturnsNonZeroValue()
    {
        var generator = new CosmosIntIdValueGenerator();

        var value = generator.Next(null!);

        value.Should().NotBe(0);
    }
}
