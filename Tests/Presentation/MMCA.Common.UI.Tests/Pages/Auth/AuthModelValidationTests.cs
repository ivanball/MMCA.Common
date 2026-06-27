using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using MMCA.Common.UI.Pages.Auth;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// DataAnnotations validation for the Login/Register EditForm models (rubric §24): client rules mirror
/// the server (required, email format, password complexity, password match) so the two agree.
/// </summary>
public sealed class AuthModelValidationTests
{
    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void RegisterModel_FullyValid_PassesValidation()
    {
        var model = new RegisterModel
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
            Password = "Str0ng!pass",
            ConfirmPassword = "Str0ng!pass",
        };

        Validate(model).Should().BeEmpty();
    }

    [Fact]
    public void RegisterModel_Empty_ReportsRequiredFields()
    {
        var members = Validate(new RegisterModel()).SelectMany(r => r.MemberNames).ToList();

        members.Should().Contain([nameof(RegisterModel.FirstName), nameof(RegisterModel.LastName), nameof(RegisterModel.Email), nameof(RegisterModel.Password), nameof(RegisterModel.ConfirmPassword)]);
    }

    [Fact]
    public void RegisterModel_MismatchedPasswords_FailsOnConfirmPassword()
    {
        var model = new RegisterModel
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
            Password = "Str0ng!pass",
            ConfirmPassword = "different!1A",
        };

        Validate(model).Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(RegisterModel.ConfirmPassword));
    }

    [Theory]
    [InlineData("alllower1!")] // no uppercase
    [InlineData("ALLUPPER1!")] // no lowercase
    [InlineData("NoDigits!!")] // no digit
    [InlineData("NoSpecial1")] // no special char
    [InlineData("Ab1!")] // too short
    public void RegisterModel_WeakPassword_FailsComplexity(string password)
    {
        var model = new RegisterModel
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
            Password = password,
            ConfirmPassword = password,
        };

        Validate(model).SelectMany(r => r.MemberNames).Should().Contain(nameof(RegisterModel.Password));
    }

    [Fact]
    public void RegisterModel_StrongEightCharPassword_PassesComplexity()
    {
        var model = new RegisterModel
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
            Password = "short1!A",
            ConfirmPassword = "short1!A",
        };

        Validate(model).Should().BeEmpty();
    }

    [Fact]
    public void RegisterModel_InvalidEmail_FailsOnEmail()
    {
        var model = new RegisterModel
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "not-an-email",
            Password = "Str0ng!pass",
            ConfirmPassword = "Str0ng!pass",
        };

        Validate(model).SelectMany(r => r.MemberNames).Should().Contain(nameof(RegisterModel.Email));
    }

    [Fact]
    public void LoginModel_Valid_PassesValidation() =>
        Validate(new LoginModel { Email = "ada@example.com", Password = "anything" }).Should().BeEmpty();

    [Fact]
    public void LoginModel_Empty_ReportsRequiredFields()
    {
        var members = Validate(new LoginModel()).SelectMany(r => r.MemberNames).ToList();

        members.Should().Contain([nameof(LoginModel.Email), nameof(LoginModel.Password)]);
    }
}
