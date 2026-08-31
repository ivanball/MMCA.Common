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

    // ── RequiredIdRules ──
    [Fact]
    public void RequiredIdRules_WhenIntIsZero_HasValidationError()
    {
        var validator = new RequiredIdRules<TestIntModel, int>(x => x.Quantity, "a Category");
        var model = new TestIntModel { Quantity = 0 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Quantity)
            .WithErrorMessage("You must specify a Category");
    }

    [Fact]
    public void RequiredIdRules_WhenIntIsPositive_NoErrors()
    {
        var validator = new RequiredIdRules<TestIntModel, int>(x => x.Quantity, "Category");
        var model = new TestIntModel { Quantity = 7 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Quantity);
    }

    [Fact]
    public void RequiredIdRules_WhenGuidIsEmpty_HasValidationError()
    {
        var validator = new RequiredIdRules<TestGuidModel, Guid>(x => x.OwnerId, "an Owner");
        var model = new TestGuidModel { OwnerId = Guid.Empty };

        TestValidationResult<TestGuidModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.OwnerId)
            .WithErrorMessage("You must specify an Owner");
    }

    [Fact]
    public void RequiredIdRules_WhenGuidIsPopulated_NoErrors()
    {
        var validator = new RequiredIdRules<TestGuidModel, Guid>(x => x.OwnerId, "Owner");
        var model = new TestGuidModel { OwnerId = Guid.NewGuid() };

        TestValidationResult<TestGuidModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.OwnerId);
    }

    [Fact]
    public void RequiredIdRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new RequiredIdRules<TestIntModel, int>(x => x.Quantity, "Event", "Session.EventId.Required");
        var model = new TestIntModel { Quantity = 0 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Quantity)
            .WithErrorCode("Session.EventId.Required");
    }

    // ── OptionalPositiveIdRules ──
    [Fact]
    public void OptionalPositiveIdRules_WhenNull_NoErrors()
    {
        var validator = new OptionalPositiveIdRules<TestOptionalIntModel, int>(x => x.CategoryId, "Category ID");
        var model = new TestOptionalIntModel { CategoryId = null };

        TestValidationResult<TestOptionalIntModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.CategoryId);
    }

    [Fact]
    public void OptionalPositiveIdRules_WhenZero_HasValidationError()
    {
        var validator = new OptionalPositiveIdRules<TestOptionalIntModel, int>(x => x.CategoryId, "Category ID");
        var model = new TestOptionalIntModel { CategoryId = 0 };

        TestValidationResult<TestOptionalIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.CategoryId)
            .WithErrorMessage("Category ID must be a valid positive value when provided.");
    }

    [Fact]
    public void OptionalPositiveIdRules_WhenNegative_HasValidationError()
    {
        var validator = new OptionalPositiveIdRules<TestOptionalIntModel, int>(x => x.CategoryId, "Category ID");
        var model = new TestOptionalIntModel { CategoryId = -1 };

        TestValidationResult<TestOptionalIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.CategoryId);
    }

    [Fact]
    public void OptionalPositiveIdRules_WhenPositive_NoErrors()
    {
        var validator = new OptionalPositiveIdRules<TestOptionalIntModel, int>(x => x.CategoryId, "Category ID");
        var model = new TestOptionalIntModel { CategoryId = 42 };

        TestValidationResult<TestOptionalIntModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.CategoryId);
    }

    [Fact]
    public void OptionalPositiveIdRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new OptionalPositiveIdRules<TestOptionalIntModel, int>(
            x => x.CategoryId, "Category ID", "Product.CategoryId.Invalid");
        var model = new TestOptionalIntModel { CategoryId = 0 };

        TestValidationResult<TestOptionalIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.CategoryId)
            .WithErrorCode("Product.CategoryId.Invalid");
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

    // ── Optional errorCode: omitted leaves the existing behaviour untouched ──
    [Fact]
    public void RequiredStringRules_WhenErrorCodeOmitted_UsesFluentValidationDefaultCode()
    {
        var validator = new RequiredStringRules<TestStringModel>(x => x.Name, "Name", 50);
        var model = new TestStringModel { Name = string.Empty };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorCode("NotEmptyValidator");
    }

    [Fact]
    public void RequiredStringRules_WhenErrorCodeSupplied_AppliesItToTheNotEmptyRule()
    {
        var validator = new RequiredStringRules<TestStringModel>(x => x.Name, "Name", 50, "Question.QuestionText.Required");
        var model = new TestStringModel { Name = string.Empty };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorCode("Question.QuestionText.Required")
            .WithErrorMessage("You must enter a Name");
    }

    [Fact]
    public void RequiredStringRules_WhenErrorCodeSupplied_AppliesItToTheMaxLengthRuleToo()
    {
        var validator = new RequiredStringRules<TestStringModel>(x => x.Name, "Name", 5, "Question.QuestionText.Invalid");
        var model = new TestStringModel { Name = "123456" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorCode("Question.QuestionText.Invalid");
    }

    [Fact]
    public void OptionalStringRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new OptionalStringRules<TestOptionalStringModel>(x => x.Description, "Description", 5, "Activity.VenueUrl.MaxLength");
        var model = new TestOptionalStringModel { Description = "123456" };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorCode("Activity.VenueUrl.MaxLength");
    }

    [Fact]
    public void EmailRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new EmailRules<TestStringModel>(x => x.Name, "Email", 100, "User.Email.Invalid");
        var model = new TestStringModel { Name = "not-an-email" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorCode("User.Email.Invalid");
    }

    [Fact]
    public void PositiveIntRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new PositiveIntRules<TestIntModel>(x => x.Quantity, "Sponsor ID", "CheckIn.SponsorId.Required");
        var model = new TestIntModel { Quantity = 0 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Quantity)
            .WithErrorCode("CheckIn.SponsorId.Required")
            .WithErrorMessage("Sponsor ID must be a positive value");
    }

    [Fact]
    public void PositiveDecimalRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new PositiveDecimalRules<TestDecimalModel>(x => x.Price, "Price", "Product.Price.NotPositive");
        var model = new TestDecimalModel { Price = 0m };

        TestValidationResult<TestDecimalModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Price)
            .WithErrorCode("Product.Price.NotPositive");
    }

    [Fact]
    public void NonNegativeIntRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new NonNegativeIntRules<TestIntModel>(x => x.Quantity, "Sort Order", "Activity.SortOrder.Negative");
        var model = new TestIntModel { Quantity = -1 };

        TestValidationResult<TestIntModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Quantity)
            .WithErrorCode("Activity.SortOrder.Negative");
    }

    [Fact]
    public void PasswordRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new PasswordRules<TestStringModel>(x => x.Name, "User.CurrentPassword.Required");
        var model = new TestStringModel { Name = string.Empty };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorCode("User.CurrentPassword.Required");
    }

    [Fact]
    public void StrongPasswordRules_WhenErrorCodeSupplied_AppliesItToEveryComplexityRule()
    {
        var validator = new StrongPasswordRules<TestStringModel>(x => x.Name, "User.NewPassword.Weak");
        var model = new TestStringModel { Name = "nospecial1" };

        TestValidationResult<TestStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Name)
            .WithErrorCode("User.NewPassword.Weak");
    }

    // ── AbsoluteUrlRules ──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/logo.png")]
    [InlineData("http://example.com")]
    public void AbsoluteUrlRules_WhenAbsentOrAbsoluteHttp_NoErrors(string? url)
    {
        var validator = new AbsoluteUrlRules<TestOptionalStringModel>(x => x.Description, "Logo URL", 200);
        var model = new TestOptionalStringModel { Description = url };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldNotHaveValidationErrorFor(x => x.Description);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    public void AbsoluteUrlRules_WhenNotAbsoluteHttp_HasValidationError(string url)
    {
        var validator = new AbsoluteUrlRules<TestOptionalStringModel>(x => x.Description, "Logo URL", 200);
        var model = new TestOptionalStringModel { Description = url };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage("Logo URL must be an absolute http or https URL");
    }

    [Fact]
    public void AbsoluteUrlRules_WhenTooLong_HasValidationError()
    {
        var validator = new AbsoluteUrlRules<TestOptionalStringModel>(x => x.Description, "Logo URL", 20);
        var model = new TestOptionalStringModel { Description = "https://example.com/a-very-long-path/logo.png" };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage("Logo URL cannot be longer than 20 characters");
    }

    [Fact]
    public void AbsoluteUrlRules_WhenErrorCodeSupplied_AppliesIt()
    {
        var validator = new AbsoluteUrlRules<TestOptionalStringModel>(x => x.Description, "Logo URL", 200, "Sponsor.LogoUrl.Invalid");
        var model = new TestOptionalStringModel { Description = "javascript:alert(1)" };

        TestValidationResult<TestOptionalStringModel> result = validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorCode("Sponsor.LogoUrl.Invalid");
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

public sealed record TestOptionalIntModel
{
    public int? CategoryId { get; init; }
}

public sealed record TestGuidModel
{
    public Guid OwnerId { get; init; }
}
