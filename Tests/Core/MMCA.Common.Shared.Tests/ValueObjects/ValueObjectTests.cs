using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class ValueObjectTests
{
    private sealed record TestValueObject(string Name, int Age) : ValueObject;

    [Fact]
    public void ValueObjects_WithSameProperties_AreEqual()
    {
        var a = new TestValueObject("Alice", 30);
        var b = new TestValueObject("Alice", 30);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ValueObjects_WithDifferentProperties_AreNotEqual()
    {
        var a = new TestValueObject("Alice", 30);
        var b = new TestValueObject("Bob", 30);

        a.Should().NotBe(b);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void ExistingValueObjects_DeriveFromValueObject()
    {
        var money = Money.Create(10m, Currency.Usd).Value!;
        var address = Address.Create("123 Main St", null, null, null, null, null).Value!;
        var dateRange = DateRange.Create(new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)).Value!;
        var dateTimeRange = DateTimeRange.Create(DateTime.UtcNow, DateTime.UtcNow.AddHours(1)).Value!;

        money.Should().BeAssignableTo<ValueObject>();
        address.Should().BeAssignableTo<ValueObject>();
        dateRange.Should().BeAssignableTo<ValueObject>();
        dateTimeRange.Should().BeAssignableTo<ValueObject>();
        Currency.Usd.Should().BeAssignableTo<ValueObject>();
    }
}
