using FluentAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class AddressTests
{
    // ── Create ──
    [Fact]
    public void Create_WithValidAddressLine1_ReturnsSuccess()
    {
        var result = Address.Create("123 Main St", null, "City", "ST", "12345", "US");

        result.IsSuccess.Should().BeTrue();
        result.Value!.AddressLine1.Should().Be("123 Main St");
        result.Value.City.Should().Be("City");
        result.Value.Country.Should().Be("US");
    }

    [Fact]
    public void Create_WithAllFields_ReturnsSuccess()
    {
        var result = Address.Create("123 Main St", "Apt 4", "City", "ST", "12345", "US");

        result.IsSuccess.Should().BeTrue();
        result.Value!.AddressLine2.Should().Be("Apt 4");
    }

    [Fact]
    public void Create_WithEmptyAddressLine1_ReturnsFailure()
    {
        var result = Address.Create(string.Empty, null, null, null, null, null);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Address.Line1.Empty");
    }

    [Fact]
    public void Create_WithWhitespaceAddressLine1_ReturnsFailure()
    {
        var result = Address.Create("   ", null, null, null, null, null);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Address.Line1.Empty");
    }

    // ── ToString ──
    [Fact]
    public void ToString_WithAllFields_ReturnsCommaSeparatedParts()
    {
        var address = Address.Create("123 Main St", "Apt 4", "City", "ST", "12345", "US").Value!;

        var result = address.ToString();

        result.Should().Be("123 Main St, Apt 4, City, ST, 12345, US");
    }

    [Fact]
    public void ToString_WithNullOptionalFields_OmitsEmptyParts()
    {
        var address = Address.Create("123 Main St", null, "City", null, "12345", "US").Value!;

        var result = address.ToString();

        result.Should().Be("123 Main St, City, 12345, US");
    }

    // ── Equality ──
    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = Address.Create("123 Main St", null, "City", null, null, null).Value!;
        var b = Address.Create("123 Main St", null, "City", null, null, null).Value!;

        a.Should().Be(b);
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var a = Address.Create("123 Main St", null, null, null, null, null).Value!;
        var b = Address.Create("456 Oak Ave", null, null, null, null, null).Value!;

        a.Should().NotBe(b);
    }
}
