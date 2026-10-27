using AwesomeAssertions;
using MMCA.Common.Application.Validation;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Application.Tests.Validation;

/// <summary>
/// <see cref="UsAddressValidator"/>: a US address needs a street line, city, state and ZIP code; the
/// state must be a USPS region code and the ZIP code 5 digits or ZIP+4; an empty field reports only that
/// it is required; and the plain <see cref="AddressValidator"/> is unchanged.
/// </summary>
public sealed class UsAddressValidationRulesTests
{
    private readonly UsAddressValidator _validator = new();

    [Fact]
    public void CompleteUsAddress_IsValid() =>
        _validator.Validate(Address("3320 Rose Rd", "Douglasville", "GA", "30135")).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("PR", "00901")]
    [InlineData("AE", "09001-1234")]
    [InlineData("dc", "20001")]
    public void TerritoryMilitaryAndLowerCaseCodes_AreValid(string state, string zip) =>
        _validator.Validate(Address("1 Main St", "Anytown", state, zip)).IsValid.Should().BeTrue();

    [Fact]
    public void MissingCityStateAndZip_AreEachRequired()
    {
        var result = _validator.Validate(Address("1 Main St", city: null, state: null, zip: null));

        result.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo("City", "State", "ZipCode");
        result.Errors.Should().OnlyContain(e => e.ErrorMessage.StartsWith("You must enter", StringComparison.Ordinal),
            "an empty field reports only that it is required, not also that its format is wrong");
    }

    [Fact]
    public void UnknownState_IsRefused()
    {
        var result = _validator.Validate(Address("1 Main St", "Anytown", "Georgia", "30135"));

        result.Errors.Should().ContainSingle(e => e.PropertyName == "State");
    }

    [Theory]
    [InlineData("3013")]
    [InlineData("30135-12")]
    [InlineData("ABCDE")]
    public void MalformedZip_IsRefused(string zip)
    {
        var result = _validator.Validate(Address("1 Main St", "Anytown", "GA", zip));

        result.Errors.Should().ContainSingle(e => e.PropertyName == "ZipCode");
    }

    [Fact]
    public void TheGeneralAddressValidator_IsUnchanged() =>
        new AddressValidator().Validate(Address("1 Main St", city: null, state: null, zip: null)).IsValid.Should().BeTrue(
            "the plain validator keeps city, state and ZIP optional for apps that are not US-only");

    private static Address Address(string line1, string? city, string? state, string? zip) =>
        MMCA.Common.Shared.ValueObjects.Contact.Address.Create(line1, null, city, state, zip, "United States").Value!;
}
