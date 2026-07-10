using AwesomeAssertions;
using MMCA.Common.Shared.Exceptions;
using MMCA.Common.UI.Pages.Common;

namespace MMCA.Common.UI.Tests.Pages.Common;

/// <summary>
/// Covers the ADR-027 Decision 9 contract: a <see cref="DomainInvariantViolationException"/>
/// (the API's server-localized Problem Details message, minted by <c>ServiceExceptionHelper</c>)
/// is surfaced verbatim, while any other exception's raw text is never shown to the user.
/// </summary>
public class ErrorMessagesTests
{
    private const string DomainMessage = "This action is only available while the event is live.";

    // ── Domain rejection: the server's localized message is shown verbatim ──
    [Fact]
    public void LoadError_WithDomainInvariantViolation_ReturnsServerMessage() =>
        ErrorMessages.LoadError("event", new DomainInvariantViolationException(DomainMessage))
            .Should().Be(DomainMessage);

    [Fact]
    public void SaveError_WithDomainInvariantViolation_ReturnsServerMessage() =>
        ErrorMessages.SaveError("event", new DomainInvariantViolationException(DomainMessage))
            .Should().Be(DomainMessage);

    [Fact]
    public void DeleteError_WithDomainInvariantViolation_ReturnsServerMessage() =>
        ErrorMessages.DeleteError("event", new DomainInvariantViolationException(DomainMessage))
            .Should().Be(DomainMessage);

    [Fact]
    public void ActionError_WithDomainInvariantViolation_ReturnsServerMessage() =>
        ErrorMessages.ActionError(new DomainInvariantViolationException(DomainMessage), "fallback")
            .Should().Be(DomainMessage);

    // ── Any other exception: raw text is never surfaced ──
    [Fact]
    public void LoadError_WithOtherException_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.LoadError("event", new InvalidOperationException("stack internals"));

        message.Should().Be("Error loading event.");
        message.Should().NotContain("stack internals");
    }

    [Fact]
    public void SaveError_WithOtherException_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.SaveError("event", new InvalidOperationException("stack internals"));

        message.Should().Be("Error saving event.");
        message.Should().NotContain("stack internals");
    }

    [Fact]
    public void DeleteError_WithOtherException_ReturnsTemplateWithoutExceptionText()
    {
        var message = ErrorMessages.DeleteError("event", new InvalidOperationException("stack internals"));

        message.Should().Be("Error deleting event.");
        message.Should().NotContain("stack internals");
    }

    [Fact]
    public void ActionError_WithOtherException_ReturnsLocalizedFallback() =>
        ErrorMessages.ActionError(new InvalidOperationException("stack internals"), "The action could not be completed.")
            .Should().Be("The action could not be completed.");

    // ── The carve-out is exact: a DomainException sibling does not qualify ──
    [Fact]
    public void ActionError_WithOtherDomainException_ReturnsLocalizedFallback() =>
        ErrorMessages.ActionError(new OtherDomainException("not from the API"), "fallback")
            .Should().Be("fallback");

#pragma warning disable CA1032 // Implement standard exception constructors — test-local type, only the message ctor is exercised
    private sealed class OtherDomainException(string message) : DomainException(message);
#pragma warning restore CA1032
}
