using AwesomeAssertions;
using MMCA.Common.Shared.Exceptions;
using MMCA.Common.UI.Pages.Common;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// Covers the ADR-027 Decision 9 contract: an exception's raw text is never surfaced to the user, so
/// every helper answers with its localized entity-noun template regardless of the exception type.
/// </summary>
public class ErrorMessagesTests
{
    private const string DomainMessage = "This action is only available while the event is live.";

    [Fact]
    public void LoadError_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.LoadError("event", new InvalidOperationException("stack internals"));

        message.Should().Be("Error loading event.");
        message.Should().NotContain("stack internals");
    }

    [Fact]
    public void SaveError_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.SaveError("event", new InvalidOperationException("stack internals"));

        message.Should().Be("Error saving event.");
        message.Should().NotContain("stack internals");
    }

    [Fact]
    public void DeleteError_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.DeleteError("event", new InvalidOperationException("stack internals"));

        message.Should().Be("Error deleting event.");
        message.Should().NotContain("stack internals");
    }

    // A domain exception raised inside the page gets the same treatment as any other: its text is a
    // server-side wording that only reaches the user through a Result, never through these helpers.
    [Fact]
    public void LoadError_WithDomainInvariantViolation_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.LoadError("event", new DomainInvariantViolationException(DomainMessage));

        message.Should().Be("Error loading event.");
        message.Should().NotContain(DomainMessage);
    }

    [Fact]
    public void SaveError_WithDomainInvariantViolation_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.SaveError("event", new DomainInvariantViolationException(DomainMessage));

        message.Should().Be("Error saving event.");
        message.Should().NotContain(DomainMessage);
    }

    [Fact]
    public void DeleteError_WithDomainInvariantViolation_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.DeleteError("event", new DomainInvariantViolationException(DomainMessage));

        message.Should().Be("Error deleting event.");
        message.Should().NotContain(DomainMessage);
    }

    [Fact]
    public void DeleteFailed_ReturnsTheEntityNounTemplate() =>
        ErrorMessages.DeleteFailed("event").Should().Be("Failed to delete the event.");

    [Fact]
    public void NotFound_NamesTheEntityAndItsId() =>
        ErrorMessages.NotFound("Event", 42).Should().Be("Event with Id 42 was not found.");

    [Fact]
    public void ValidationError_ReturnsTheFormTemplate() =>
        ErrorMessages.ValidationError.Should().Be("There were validation errors. Please check the form.");
}
