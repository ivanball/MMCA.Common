using System.Globalization;
using FluentValidation;
using FluentValidation.TestHelper;
using MMCA.Common.Application.Validation;
using MMCA.Common.Shared.ValueObjects.Contact;

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
            .WithErrorMessage(string.Create(CultureInfo.InvariantCulture, $"Address Line 1 cannot be longer than {AddressInvariants.AddressLine1MaxLength} characters"));
    }

    [Fact]
    public void AddressLine1_WhenValid_HasNoValidationError()
    {
        var line1Validator = new AddressLine1Rules<TestAddressModel>(p => p.AddressLine1);
        var model = new TestAddressModel { AddressLine1 = "123 Main St" };

        var result = line1Validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(p => p.AddressLine1);
    }

    // ── Optional fields: one character over the max length fails ──
    [Theory]
    [InlineData(nameof(TestAddressModel.AddressLine2))]
    [InlineData(nameof(TestAddressModel.City))]
    [InlineData(nameof(TestAddressModel.State))]
    [InlineData(nameof(TestAddressModel.ZipCode))]
    [InlineData(nameof(TestAddressModel.Country))]
    public void OptionalField_WhenTooLong_HasValidationError(string field)
    {
        (AbstractValidator<TestAddressModel> validator, TestAddressModel model) = field switch
        {
            nameof(TestAddressModel.AddressLine2) => ((AbstractValidator<TestAddressModel>)new AddressLine2Rules<TestAddressModel>(p => p.AddressLine2),
                new TestAddressModel { AddressLine1 = "123 Main St", AddressLine2 = new string('B', AddressInvariants.AddressLine2MaxLength + 1) }),
            nameof(TestAddressModel.City) => (new CityRules<TestAddressModel>(p => p.City),
                new TestAddressModel { AddressLine1 = "123 Main St", City = new string('C', AddressInvariants.CityMaxLength + 1) }),
            nameof(TestAddressModel.State) => (new StateRules<TestAddressModel>(p => p.State),
                new TestAddressModel { AddressLine1 = "123 Main St", State = new string('S', AddressInvariants.StateMaxLength + 1) }),
            nameof(TestAddressModel.ZipCode) => (new ZipCodeRules<TestAddressModel>(p => p.ZipCode),
                new TestAddressModel { AddressLine1 = "123 Main St", ZipCode = new string('9', AddressInvariants.ZipCodeMaxLength + 1) }),
            nameof(TestAddressModel.Country) => (new CountryRules<TestAddressModel>(p => p.Country),
                new TestAddressModel { AddressLine1 = "123 Main St", Country = new string('X', AddressInvariants.CountryMaxLength + 1) }),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field."),
        };

        var result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(field);
    }

    // ── AddressLine2 ──
    [Fact]
    public void AddressLine2_WhenNull_HasNoValidationError()
    {
        var validator = new AddressLine2Rules<TestAddressModel>(p => p.AddressLine2);
        var model = new TestAddressModel { AddressLine1 = "123 Main St", AddressLine2 = null };

        var result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(p => p.AddressLine2);
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
