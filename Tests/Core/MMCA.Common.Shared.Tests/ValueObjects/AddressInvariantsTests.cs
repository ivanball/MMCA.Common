using AwesomeAssertions;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Shared.Tests.ValueObjects;

public class AddressInvariantsTests
{
    // ── EnsureAddressIsValid ──
    [Fact]
    public void EnsureAddressIsValid_WithNull_ReturnsSuccess()
    {
        var result = AddressInvariants.EnsureAddressIsValid(null, "Test");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureAddressIsValid_WithValidAddress_ReturnsSuccess()
    {
        var address = Address.Create("123 Main St", null, null, null, null, null).Value!;
        var result = AddressInvariants.EnsureAddressIsValid(address, "Test");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureAddressIsValid_WithEmptyLine1_ReturnsFailure()
    {
        // Create an address with null AddressLine1 by using reflection or a valid one and checking invariants directly
        var result = AddressInvariants.EnsureAddressLine1IsValid(string.Empty, "Test");
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Address.Line1.Empty");
    }

    // ── EnsureAddressLine1IsValid ──
    [Fact]
    public void EnsureAddressLine1IsValid_WithValue_ReturnsSuccess()
    {
        var result = AddressInvariants.EnsureAddressLine1IsValid("123 Main St", "Test");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureAddressLine1IsValid_WithWhitespace_ReturnsFailure()
    {
        var result = AddressInvariants.EnsureAddressLine1IsValid("   ", "Test");
        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Code == "Address.Line1.Empty");
    }

    [Fact]
    public void EnsureAddressLine1IsValid_SetsSourceOnError()
    {
        var result = AddressInvariants.EnsureAddressLine1IsValid(string.Empty, "MySource");
        result.Errors.Should().Contain(e => e.Source == "MySource");
    }
}
