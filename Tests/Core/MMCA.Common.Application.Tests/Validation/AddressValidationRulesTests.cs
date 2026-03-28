using AwesomeAssertions;
using FluentValidation.TestHelper;
using MMCA.Common.Application.Validation;
using MMCA.Common.Shared.ValueObjects;

namespace MMCA.Common.Application.Tests.Validation;

public sealed class AddressValidationRulesTests
{
    private readonly AddressValidator _validator = new();

    private static Address CreateValidAddress() =>
        Address.Create("123 Main St", "Apt 4", "Springfield", "IL", "62704", "US").Value!;

    // ── AddressLine1 ──
    [Fact]
    public void AddressLine1_WhenEmpty_HasValidationError()
    {
        var line1Validator = new AddressLine1Rules<TestAddressModel>(p => p.AddressLine1);
        var model = new TestAddressModel { AddressLine1 = string.Empty };

        var result = line1Validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(p => p.AddressLine1)
            .WithErrorMessage("You must enter an Address Line 1");
    }

    [Fact]
    public void AddressLine1_WhenTooLong_HasValidationError()
    {
        var line1Validator = new AddressLine1Rules<TestAddressModel>(p => p.AddressLine1);
        var model = new TestAddressModel { AddressLine1 = new string('A', AddressInvariants.AddressLine1MaxLength + 1) };

        var result = line1Validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(p => p.AddressLine1)
            .WithErrorMessage($"Address Line 1 cannot be longer than {AddressInvariants.AddressLine1MaxLength} characters");
    }

    [Fact]
    public void AddressLine1_WhenValid_HasNoValidationError()
    {
        var line1Validator = new AddressLine1Rules<TestAddressModel>(p => p.AddressLine1);
        var model = new TestAddressModel { AddressLine1 = "123 Main St" };

        var result = line1Validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(p => p.AddressLine1);
    }

    // ── AddressLine2 ──
    [Fact]
    public void AddressLine2_WhenTooLong_HasValidationError()
    {
        var validator = new AddressLine2Rules<TestAddressModel>(p => p.AddressLine2);
        var model = new TestAddressModel
        {
            AddressLine1 = "123 Main St",
            AddressLine2 = new string('B', AddressInvariants.AddressLine2MaxLength + 1)
        };

        var result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(p => p.AddressLine2);
    }

    [Fact]
    public void AddressLine2_WhenNull_HasNoValidationError()
    {
        var validator = new AddressLine2Rules<TestAddressModel>(p => p.AddressLine2);
        var model = new TestAddressModel { AddressLine1 = "123 Main St", AddressLine2 = null };

        var result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(p => p.AddressLine2);
    }

    // ── City ──
    [Fact]
    public void City_WhenTooLong_HasValidationError()
    {
        var validator = new CityRules<TestAddressModel>(p => p.City);
        var model = new TestAddressModel
        {
            AddressLine1 = "123 Main St",
            City = new string('C', AddressInvariants.CityMaxLength + 1)
        };

        var result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(p => p.City);
    }

    // ── State ──
    [Fact]
    public void State_WhenTooLong_HasValidationError()
    {
        var validator = new StateRules<TestAddressModel>(p => p.State);
        var model = new TestAddressModel
        {
            AddressLine1 = "123 Main St",
            State = new string('S', AddressInvariants.StateMaxLength + 1)
        };

        var result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(p => p.State);
    }

    // ── ZipCode ──
    [Fact]
    public void ZipCode_WhenTooLong_HasValidationError()
    {
        var validator = new ZipCodeRules<TestAddressModel>(p => p.ZipCode);
        var model = new TestAddressModel
        {
            AddressLine1 = "123 Main St",
            ZipCode = new string('9', AddressInvariants.ZipCodeMaxLength + 1)
        };

        var result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(p => p.ZipCode);
    }

    // ── Country ──
    [Fact]
    public void Country_WhenTooLong_HasValidationError()
    {
        var validator = new CountryRules<TestAddressModel>(p => p.Country);
        var model = new TestAddressModel
        {
            AddressLine1 = "123 Main St",
            Country = new string('X', AddressInvariants.CountryMaxLength + 1)
        };

        var result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(p => p.Country);
    }

    // ── Full AddressValidator on valid address ──
    [Fact]
    public void AddressValidator_WhenAllFieldsValid_HasNoErrors()
    {
        var address = CreateValidAddress();

        var result = _validator.TestValidate(address);

        result.ShouldNotHaveAnyValidationErrors();
    }
}

// ── Test model for individual rule validators ──
public sealed class TestAddressModel
{
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Country { get; set; }
}
