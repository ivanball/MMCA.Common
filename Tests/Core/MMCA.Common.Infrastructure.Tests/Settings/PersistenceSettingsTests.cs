using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Settings;

namespace MMCA.Common.Infrastructure.Tests.Settings;

/// <summary>
/// The command timeout is bound and validated through the options pipeline, so the annotated
/// range is what stops a host booting with an unusable value. These tests pin the default (which
/// must reproduce the previous implicit ADO.NET behavior) and both ends of that range.
/// </summary>
public class PersistenceSettingsTests
{
    [Fact]
    public void SectionName_IsPersistence() =>
        PersistenceSettings.SectionName.Should().Be("Persistence");

    [Fact]
    public void Default_CommandTimeoutSeconds_Is30() =>
        new PersistenceSettings().CommandTimeoutSeconds.Should().Be(
            30,
            "the default must match the previous implicit ADO.NET timeout so nothing changes for an app that sets nothing");

    [Fact]
    public void Properties_RoundTrip()
    {
        var sut = new PersistenceSettings { CommandTimeoutSeconds = 120 };
        sut.CommandTimeoutSeconds.Should().Be(120);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(600)]
    public void CommandTimeoutSeconds_InRange_PassesValidation(int seconds) =>
        Validate(new PersistenceSettings { CommandTimeoutSeconds = seconds }).Should().BeEmpty();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    public void CommandTimeoutSeconds_OutOfRange_FailsValidation(int seconds) =>
        Validate(new PersistenceSettings { CommandTimeoutSeconds = seconds })
            .Should().ContainSingle()
            .Which.MemberNames.Should().Contain(nameof(PersistenceSettings.CommandTimeoutSeconds));

    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }
}
