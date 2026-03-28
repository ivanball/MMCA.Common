using AwesomeAssertions;
using FluentValidation.Results;
using MMCA.Common.Application.Extensions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Tests.Extensions;

public class ValidationFailureExtensionsTests
{
    // ── ToErrors ──
    [Fact]
    public void ToErrors_MapsFailuresToErrors()
    {
        var result = new ValidationResult(
        [
            new ValidationFailure("Name", "Name is required") { ErrorCode = "NotEmpty" },
            new ValidationFailure("Price", "Price must be positive") { ErrorCode = "GreaterThan" },
        ]);

        var errors = result.ToErrors("Product").ToList();

        errors.Should().HaveCount(2);
        errors[0].Code.Should().Be("NotEmpty");
        errors[0].Message.Should().Be("Name is required");
        errors[0].Source.Should().Be("Product");
        errors[0].Target.Should().Be("Name");
        errors[0].Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public void ToErrors_EmptyResult_ReturnsEmptyCollection()
    {
        var result = new ValidationResult();
        result.ToErrors("Entity").Should().BeEmpty();
    }

    [Fact]
    public void ToErrors_SingleFailure_ReturnsSingleError()
    {
        var result = new ValidationResult(
        [
            new ValidationFailure("Email", "Invalid email") { ErrorCode = "EmailValidator" },
        ]);

        var errors = result.ToErrors("User").ToList();

        errors.Should().ContainSingle();
        errors[0].Code.Should().Be("EmailValidator");
        errors[0].Target.Should().Be("Email");
    }
}
