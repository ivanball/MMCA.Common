using AwesomeAssertions;
using FluentValidation.TestHelper;
using MMCA.Common.Application.Validation;

namespace MMCA.Common.Application.Tests.Validation;

public sealed class CommonValidationRulesTests
{
    // ── RequiredStringRules ──
    [Fact]
    public void RequiredStringRules_WhenEmpty_HasValidationError()
    {
        var validator = new RequiredStringRules<TestStringModel>(x => x.Name, "Name", 50);
        var model = new TestStringModel { Name = string.Empty };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("You must enter a Name");
    }

    [Fact]
    public void RequiredStringRules_WhenTooLong_HasValidationError()
    {
        var validator = new RequiredStringRules<TestStringModel>(x => x.Name, "Name", 5);
        var model = new TestStringModel { Name = "123456" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Name cannot be longer than 5 characters");
    }

    [Fact]
    public void RequiredStringRules_WhenValid_NoErrors()
    {
        var validator = new RequiredStringRules<TestStringModel>(x => x.Name, "Name", 50);
        var model = new TestStringModel { Name = "Valid Name" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    // ── OptionalStringRules ──
    [Fact]
    public void OptionalStringRules_WhenTooLong_HasValidationError()
    {
        var validator = new OptionalStringRules<TestOptionalStringModel>(x => x.Description, "Description", 5);
        var model = new TestOptionalStringModel { Description = "123456" };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage("Description cannot be longer than 5 characters");
    }

    [Fact]
    public void OptionalStringRules_WhenNull_NoErrors()
    {
        var validator = new OptionalStringRules<TestOptionalStringModel>(x => x.Description, "Description", 50);
        var model = new TestOptionalStringModel { Description = null };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void OptionalStringRules_WhenEmpty_NoErrors()
    {
        var validator = new OptionalStringRules<TestOptionalStringModel>(x => x.Description, "Description", 50);
        var model = new TestOptionalStringModel { Description = string.Empty };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    // ── EmailRules ──
    [Fact]
    public void EmailRules_WhenEmpty_HasValidationError()
    {
        var validator = new EmailRules<TestStringModel>(x => x.Name, "Email", 100);
        var model = new TestStringModel { Name = string.Empty };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("You must enter a Email");
    }

    [Fact]
    public void EmailRules_WhenInvalidFormat_HasValidationError()
    {
        var validator = new EmailRules<TestStringModel>(x => x.Name, "Email", 100);
        var model = new TestStringModel { Name = "not-an-email" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("You must enter a valid Email");
    }

    [Fact]
    public void EmailRules_WhenTooLong_HasValidationError()
    {
        var validator = new EmailRules<TestStringModel>(x => x.Name, "Email", 10);
        var model = new TestStringModel { Name = "test@verylongdomain.com" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Email cannot be longer than 10 characters");
    }

    [Fact]
    public void EmailRules_WhenValid_NoErrors()
    {
        var validator = new EmailRules<TestStringModel>(x => x.Name, "Email", 100);
        var model = new TestStringModel { Name = "test@example.com" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    // ── PositiveIntRules ──
    [Fact]
    public void PositiveIntRules_WhenZero_HasValidationError()
    {
        var validator = new PositiveIntRules<TestIntModel>(x => x.Quantity, "Quantity");
        var model = new TestIntModel { Quantity = 0 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Quantity)
            .WithErrorMessage("Quantity must be a positive value");
    }

    [Fact]
    public void PositiveIntRules_WhenNegative_HasValidationError()
    {
        var validator = new PositiveIntRules<TestIntModel>(x => x.Quantity, "Quantity");
        var model = new TestIntModel { Quantity = -1 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Quantity);
    }

    [Fact]
    public void PositiveIntRules_WhenPositive_NoErrors()
    {
        var validator = new PositiveIntRules<TestIntModel>(x => x.Quantity, "Quantity");
        var model = new TestIntModel { Quantity = 5 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Quantity);
    }

    // ── PositiveDecimalRules ──
    [Fact]
    public void PositiveDecimalRules_WhenZero_HasValidationError()
    {
        var validator = new PositiveDecimalRules<TestDecimalModel>(x => x.Price, "Price");
        var model = new TestDecimalModel { Price = 0m };

        TestValidationResult<TestDecimalModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Price)
            .WithErrorMessage("Price must be a positive value");
    }

    [Fact]
    public void PositiveDecimalRules_WhenPositive_NoErrors()
    {
        var validator = new PositiveDecimalRules<TestDecimalModel>(x => x.Price, "Price");
        var model = new TestDecimalModel { Price = 9.99m };

        TestValidationResult<TestDecimalModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Price);
    }

    // ── NonNegativeIntRules ──
    [Fact]
    public void NonNegativeIntRules_WhenNegative_HasValidationError()
    {
        var validator = new NonNegativeIntRules<TestIntModel>(x => x.Quantity, "Stock");
        var model = new TestIntModel { Quantity = -1 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Quantity)
            .WithErrorMessage("Stock must be greater than or equal to 0");
    }

    [Fact]
    public void NonNegativeIntRules_WhenZero_NoErrors()
    {
        var validator = new NonNegativeIntRules<TestIntModel>(x => x.Quantity, "Stock");
        var model = new TestIntModel { Quantity = 0 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Quantity);
    }

    // ── PasswordRules ──
    [Fact]
    public void PasswordRules_WhenEmpty_HasValidationError()
    {
        var validator = new PasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = string.Empty };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Password is required.");
    }

    [Fact]
    public void PasswordRules_WhenTooShort_HasValidationError()
    {
        var validator = new PasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = "short" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Password must be at least 8 characters.");
    }

    [Fact]
    public void PasswordRules_WhenTooLong_HasValidationError()
    {
        var validator = new PasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = new string('a', 129) };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Password cannot be longer than 128 characters.");
    }

    [Fact]
    public void PasswordRules_WhenValid_NoErrors()
    {
        var validator = new PasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = "validpassword" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }

    // ── StrongPasswordRules ──
    [Fact]
    public void StrongPasswordRules_WhenMissingUppercase_HasValidationError()
    {
        var validator = new StrongPasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = "lowercase1!" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Password must contain at least one uppercase letter.");
    }

    [Fact]
    public void StrongPasswordRules_WhenMissingLowercase_HasValidationError()
    {
        var validator = new StrongPasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = "UPPERCASE1!" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Password must contain at least one lowercase letter.");
    }

    [Fact]
    public void StrongPasswordRules_WhenMissingDigit_HasValidationError()
    {
        var validator = new StrongPasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = "NoDigits!!" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Password must contain at least one digit.");
    }

    [Fact]
    public void StrongPasswordRules_WhenMissingSpecialChar_HasValidationError()
    {
        var validator = new StrongPasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = "NoSpecial1" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorMessage("Password must contain at least one special character.");
    }

    [Fact]
    public void StrongPasswordRules_WhenAllCriteriaMet_NoErrors()
    {
        var validator = new StrongPasswordRules<TestStringModel>(x => x.Name);
        var model = new TestStringModel { Name = "Strong1!a" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Name);
    }
}

// ── Test models ──
public sealed record TestStringModel
{
    public string Name { get; init; } = string.Empty;
}

public sealed record TestOptionalStringModel
{
    public string? Description { get; init; }
}

public sealed record TestIntModel
{
    public int Quantity { get; init; }
}

public sealed record TestDecimalModel
{
    public decimal Price { get; init; }
}
