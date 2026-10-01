using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using MMCA.Common.UI.Pages.Auth;

namespace MMCA.Common.UI.Tests.Pages.Auth;

/// <summary>
/// The context-free <see cref="ValidationAttribute.IsValid(object)"/> overload routes into the
/// attribute's override with a null <see cref="ValidationContext"/>. A weak password used to throw a
/// <see cref="NullReferenceException"/> there instead of answering <see langword="false"/>.
/// </summary>
public sealed class PasswordComplexityAttributeTests
{
    [Fact]
    public void IsValid_WithoutAValidationContext_ReturnsFalseForAWeakPassword_InsteadOfThrowing()
    {
        var attribute = new PasswordComplexityAttribute();

        Func<bool> act = () => attribute.IsValid("weak");

        act.Should().NotThrow().Which.Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithoutAValidationContext_ReturnsTrueForAStrongPassword() =>
        new PasswordComplexityAttribute().IsValid("Str0ng!pass").Should().BeTrue();

    [Fact]
    public void GetValidationResult_WithAContext_AttachesTheErrorToTheMember()
    {
        var model = new RegisterModel { Password = "weak" };
        var context = new ValidationContext(model) { MemberName = nameof(RegisterModel.Password) };

        var result = new PasswordComplexityAttribute().GetValidationResult(model.Password, context);

        result.Should().NotBeNull();
        result!.MemberNames.Should().Equal(nameof(RegisterModel.Password));
    }
}
