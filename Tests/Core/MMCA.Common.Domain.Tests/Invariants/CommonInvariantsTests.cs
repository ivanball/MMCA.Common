using AwesomeAssertions;
using MMCA.Common.Domain.Invariants;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Domain.Tests.Invariants;

public sealed class CommonInvariantsTests
{
    // ── EnsureStringIsNotEmpty ──
    [Fact]
    public void EnsureStringIsNotEmpty_WithValidString_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringIsNotEmpty(
            "valid", "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnsureStringIsNotEmpty_WithEmptyOrWhitespace_ReturnsFailure(string? value)
    {
        Result result = CommonInvariants.EnsureStringIsNotEmpty(
            value!, "Test.Code", "Name cannot be empty.", "Create", "Name");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Code");
        result.Errors[0].Message.Should().Be("Name cannot be empty.");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    // ── EnsureStringMaxLength ──
    [Fact]
    public void EnsureStringMaxLength_WhenWithinLimit_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            "short", 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureStringMaxLength_WhenExceedsLimit_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            "this is too long", 5, "Test.TooLong", "Too long.", "Create", "Name");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.TooLong");
    }

    [Fact]
    public void EnsureStringMaxLength_WhenNull_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            null, 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureStringMaxLength_WhenEmpty_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureStringMaxLength(
            string.Empty, 10, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    // ── EnsureIdIsNotDefault ──
    [Fact]
    public void EnsureIdIsNotDefault_WithValidInt_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            42, "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureIdIsNotDefault_WithDefaultInt_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            0, "Test.InvalidId", "ID must be provided.", "Create", "Id");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.InvalidId");
        result.Errors[0].Type.Should().Be(ErrorType.Invariant);
    }

    [Fact]
    public void EnsureIdIsNotDefault_WithValidGuid_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            Guid.NewGuid(), "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureIdIsNotDefault_WithDefaultGuid_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureIdIsNotDefault(
            Guid.Empty, "Test.InvalidGuid", "GUID required.", "Create", "Id");

        result.IsFailure.Should().BeTrue();
    }

    // ── EnsureBytesAreNotEmpty ──
    [Fact]
    public void EnsureBytesAreNotEmpty_WithData_ReturnsSuccess()
    {
        Result result = CommonInvariants.EnsureBytesAreNotEmpty(
            [1, 2, 3], "Code", "Message", "Source", "Target");

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void EnsureBytesAreNotEmpty_WithEmptyArray_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureBytesAreNotEmpty(
            [], "Test.Empty", "Data is required.", "Upload", "File");

        result.IsFailure.Should().BeTrue();
        result.Errors[0].Code.Should().Be("Test.Empty");
    }

    [Fact]
    public void EnsureBytesAreNotEmpty_WithNull_ReturnsFailure()
    {
        Result result = CommonInvariants.EnsureBytesAreNotEmpty(
            null!, "Test.Null", "Data is required.", "Upload", "File");

        result.IsFailure.Should().BeTrue();
    }
}
