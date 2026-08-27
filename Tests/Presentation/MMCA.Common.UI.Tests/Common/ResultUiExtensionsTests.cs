using AwesomeAssertions;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Common;
using Moq;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// Covers <see cref="ResultUiExtensions"/>, the page-side half of the Result transport (ADR-030).
/// The behaviors pinned here are the ones a page silently depends on: a failure never unwraps
/// (including value-type payloads, whose default is not null), messages are deduplicated and
/// ordered most severe first, localization is key lookup with pass-through, and a failure raises
/// exactly one snackbar rather than one per error.
/// </summary>
public sealed class ResultUiExtensionsTests
{
    private static readonly IReadOnlyDictionary<string, string> Translations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Order.NotFound"] = "That order no longer exists.",
            ["Auth.Expired"] = "Your session expired.",
            ["Alias.One"] = "Same wording.",
            ["Alias.Two"] = "Same wording.",
        };

    private readonly Mock<ISnackbar> _snackbar = new();

    // == TryGetValue ==
    [Fact]
    public void TryGetValue_ReturnsTrueAndTheValue_ForASuccess()
    {
        var result = Result.Success("payload");

        bool unwrapped = result.TryGetValue(out var value);

        unwrapped.Should().BeTrue();
        value.Should().Be("payload");
    }

    [Fact]
    public void TryGetValue_ReturnsFalse_ForAReferenceTypeFailure()
    {
        var result = Result.Failure<string>(MakeError(ErrorType.NotFound, "gone"));

        bool unwrapped = result.TryGetValue(out var value);

        unwrapped.Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void TryGetValue_ReturnsFalse_ForAValueTypePayloadFailure()
    {
        // The trap: default(int) is 0, not null, so a null-only test would report this failure as a
        // success. The branch has to be decided by IsFailure.
        var result = Result.Failure<int>(MakeError(ErrorType.Unexpected, "counter unavailable"));

        bool unwrapped = result.TryGetValue(out var value);

        unwrapped.Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void TryGetValue_ReturnsFalse_ForATuplePayloadFailure()
    {
        // The paged-grid shape: default((IReadOnlyList<string>, int)) is a struct, never null.
        var result =
            Result.Failure<(IReadOnlyList<string> Items, int TotalItems)>(
                MakeError(ErrorType.Unexpected, "backend down"));

        bool unwrapped = result.TryGetValue(out var page);

        unwrapped.Should().BeFalse();
        page.Items.Should().BeNull();
        page.TotalItems.Should().Be(0);
    }

    [Fact]
    public void TryGetValue_ReturnsFalse_ForASuccessCarryingNull()
    {
        var result = Result.Success<string?>(null);

        bool unwrapped = result.TryGetValue(out var value);

        unwrapped.Should().BeFalse("a page cannot bind a null payload even though the call succeeded");
        value.Should().BeNull();
    }

    [Fact]
    public void TryGetValue_WithErrors_HandsBackAnEmptyErrorList_OnSuccess()
    {
        var result = Result.Success(42);

        bool unwrapped = result.TryGetValue(out var value, out var errors);

        unwrapped.Should().BeTrue();
        value.Should().Be(42);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void TryGetValue_WithErrors_HandsBackTheErrors_OnFailure()
    {
        var error = MakeError(ErrorType.Conflict, "already taken", "Seat.Taken");
        var result = Result.Failure<string>(error);

        bool unwrapped = result.TryGetValue(out var value, out var errors);

        unwrapped.Should().BeFalse();
        value.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().BeSameAs(error);
    }

    [Fact]
    public void TryGetValue_WithErrors_ReturnsFalse_ForAValueTypePayloadFailure()
    {
        var result = Result.Failure<int>(MakeError(ErrorType.Forbidden, "not yours"));

        bool unwrapped = result.TryGetValue(out var value, out var errors);

        unwrapped.Should().BeFalse();
        value.Should().Be(0);
        errors.Should().ContainSingle();
    }

    [Fact]
    public void TryGetValue_Throws_ForANullResult()
    {
        Result<string> result = null!;

        Func<bool> act = () => result.TryGetValue(out _);

        act.Should().Throw<ArgumentNullException>();
    }

    // == LocalizedErrorMessages ==
    [Fact]
    public void LocalizedErrorMessages_IsEmpty_ForASuccess() =>
        Result.Success().LocalizedErrorMessages().Should().BeEmpty();

    [Fact]
    public void LocalizedErrorMessages_ReturnsTheMessage_ForASingleError()
    {
        var result = Result.Failure(MakeError(ErrorType.Validation, "Name is required"));

        result.LocalizedErrorMessages().Should().Equal("Name is required");
    }

    [Fact]
    public void LocalizedErrorMessages_DeduplicatesRepeatedMessagesAcrossCodes()
    {
        // The Result.Combine shape: the same wording arriving under several codes reads as one line.
        var result = Failure(
            MakeError(ErrorType.Invariant, "Quantity must be positive", "Order.Quantity"),
            MakeError(ErrorType.Invariant, "Quantity must be positive", "Line.Quantity"));

        result.LocalizedErrorMessages().Should().Equal("Quantity must be positive");
    }

    [Fact]
    public void LocalizedErrorMessages_DeduplicatesOrdinally_SoCaseDifferencesSurvive()
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "Boom", "A"),
            MakeError(ErrorType.Validation, "boom", "B"));

        result.LocalizedErrorMessages().Should().Equal("Boom", "boom");
    }

    [Fact]
    public void LocalizedErrorMessages_OrdersMostSevereFirst()
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "validation", "A"),
            MakeError(ErrorType.NotFound, "not found", "B"),
            MakeError(ErrorType.Unexpected, "unexpected", "C"));

        result.LocalizedErrorMessages().Should().Equal("unexpected", "not found", "validation");
    }

    [Theory]
    [InlineData(ErrorType.Unexpected)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.UnprocessableEntity)]
    public void LocalizedErrorMessages_PutsTheSevereCategoryAheadOfValidation(ErrorType severeType)
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "fix the form", "Form"),
            MakeError(severeType, "the real problem", "Real"));

        result.LocalizedErrorMessages().Should().Equal("the real problem", "fix the form");
    }

    [Fact]
    public void LocalizedErrorMessages_KeepsOriginalOrder_ForEquallyRankedCategories()
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "first", "A"),
            MakeError(ErrorType.Invariant, "second", "B"),
            MakeError(ErrorType.Failure, "third", "C"));

        result.LocalizedErrorMessages().Should().Equal("first", "second", "third");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void LocalizedErrorMessages_DropsBlankMessages(string blank)
    {
        var result = Failure(
            MakeError(ErrorType.Validation, blank, "Blank"),
            MakeError(ErrorType.Validation, "something real", "Real"));

        result.LocalizedErrorMessages().Should().Equal("something real");
    }

    [Fact]
    public void LocalizedErrorMessages_TranslatesAMessageThatMatchesAResourceKey()
    {
        var result = Result.Failure(MakeError(ErrorType.NotFound, "Order.NotFound"));

        result.LocalizedErrorMessages(Localizer()).Should().Equal("That order no longer exists.");
    }

    [Fact]
    public void LocalizedErrorMessages_PassesAnUnknownKeyThroughVerbatim()
    {
        var result = Result.Failure(MakeError(ErrorType.Failure, "Server already said this in Spanish"));

        result.LocalizedErrorMessages(Localizer()).Should().Equal("Server already said this in Spanish");
    }

    [Fact]
    public void LocalizedErrorMessages_LeavesEverythingVerbatim_WithoutALocalizer()
    {
        var result = Result.Failure(MakeError(ErrorType.NotFound, "Order.NotFound"));

        result.LocalizedErrorMessages().Should().Equal("Order.NotFound");
    }

    [Fact]
    public void LocalizedErrorMessages_DeduplicatesAfterLocalization()
    {
        // Two distinct keys that translate to identical wording collapse to one line.
        var result = Failure(
            MakeError(ErrorType.Validation, "Alias.One", "A"),
            MakeError(ErrorType.Validation, "Alias.Two", "B"));

        result.LocalizedErrorMessages(Localizer()).Should().Equal("Same wording.");
    }

    [Fact]
    public void LocalizedErrorMessages_Throws_ForANullResult()
    {
        Result result = null!;

        Func<IReadOnlyList<string>> act = () => result.LocalizedErrorMessages();

        act.Should().Throw<ArgumentNullException>();
    }

    // == LocalizedErrorMessage ==
    [Fact]
    public void LocalizedErrorMessage_IsNull_ForASuccess() =>
        Result.Success().LocalizedErrorMessage().Should().BeNull();

    [Fact]
    public void LocalizedErrorMessage_ReturnsTheSingleMessageUnchanged()
    {
        var result = Result.Failure(MakeError(ErrorType.Validation, "Name is required"));

        result.LocalizedErrorMessage().Should().Be("Name is required");
    }

    [Fact]
    public void LocalizedErrorMessage_JoinsTheDistinctMessagesWithASingleSpace()
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "Name is required.", "A"),
            MakeError(ErrorType.Validation, "Email is required.", "B"),
            MakeError(ErrorType.Validation, "Name is required.", "C"));

        result.LocalizedErrorMessage().Should().Be("Name is required. Email is required.");
    }

    [Fact]
    public void LocalizedErrorMessage_ComposesTheLocalizedWording()
    {
        var result = Failure(
            MakeError(ErrorType.Unauthorized, "Auth.Expired", "A"),
            MakeError(ErrorType.NotFound, "Order.NotFound", "B"));

        result.LocalizedErrorMessage(Localizer())
            .Should().Be("Your session expired. That order no longer exists.");
    }

    // == LocalizeDistinct ==
    [Fact]
    public void LocalizeDistinct_IsEmpty_ForNull() =>
        ResultUiExtensions.LocalizeDistinct(null).Should().BeEmpty();

    [Fact]
    public void LocalizeDistinct_KeepsTheOriginalOrder()
    {
        string[] messages = ["zebra", "apple", "mango"];

        ResultUiExtensions.LocalizeDistinct(messages).Should().Equal("zebra", "apple", "mango");
    }

    [Fact]
    public void LocalizeDistinct_DeduplicatesOrdinally()
    {
        string[] messages = ["Required", "Required", "required"];

        ResultUiExtensions.LocalizeDistinct(messages).Should().Equal("Required", "required");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LocalizeDistinct_DropsBlankEntries(string? blank)
    {
        string[] messages = [blank!, "Name is required"];

        ResultUiExtensions.LocalizeDistinct(messages).Should().Equal("Name is required");
    }

    [Fact]
    public void LocalizeDistinct_TranslatesKeysAndPassesUnknownOnesThrough()
    {
        string[] messages = ["Order.NotFound", "Already in English"];

        ResultUiExtensions.LocalizeDistinct(messages, Localizer())
            .Should().Equal("That order no longer exists.", "Already in English");
    }

    // == OnFailureSetError ==
    [Fact]
    public void OnFailureSetError_HandsTheComposedMessageToThePageFieldAndReturnsTheSameInstance()
    {
        string? captured = "stale";
        var result = Failure(
            MakeError(ErrorType.Validation, "Name is required.", "A"),
            MakeError(ErrorType.Validation, "Email is required.", "B"));

        var returned = result.OnFailureSetError(message => captured = message);

        captured.Should().Be("Name is required. Email is required.");
        returned.Should().BeSameAs(result, "the call has to sit inline in a chain");
    }

    [Fact]
    public void OnFailureSetError_ClearsThePageField_OnSuccess()
    {
        string? captured = "stale";

        Result.Success().OnFailureSetError(message => captured = message);

        captured.Should().BeNull("a success has to clear whatever the previous attempt left behind");
    }

    [Fact]
    public void OnFailureSetError_UsesTheLocalizer()
    {
        string? captured = null;
        var result = Result.Failure(MakeError(ErrorType.Unauthorized, "Auth.Expired"));

        result.OnFailureSetError(message => captured = message, Localizer());

        captured.Should().Be("Your session expired.");
    }

    [Fact]
    public void OnFailureSetError_Generic_HandsTheComposedMessageToThePageFieldAndReturnsTheSameInstance()
    {
        string? captured = null;
        var result = Result.Failure<int>(MakeError(ErrorType.NotFound, "Order.NotFound"));

        var returned = result.OnFailureSetError(message => captured = message, Localizer());

        captured.Should().Be("That order no longer exists.");
        returned.Should().BeSameAs(result);
    }

    [Fact]
    public void OnFailureSetError_Throws_ForANullSetter()
    {
        var result = Result.Failure(MakeError(ErrorType.Failure, "boom"));

        Func<Result> act = () => result.OnFailureSetError(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // == NotifyOnFailure ==
    [Fact]
    public void NotifyOnFailure_RaisesExactlyOneSnackbar_ForSeveralErrors()
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "Name is required.", "A"),
            MakeError(ErrorType.Validation, "Email is required.", "B"),
            MakeError(ErrorType.Validation, "Phone is required.", "C"));

        var returned = result.NotifyOnFailure(_snackbar.Object);

        _snackbar.Verify(
            s => s.Add(
                "Name is required. Email is required. Phone is required.",
                Severity.Error,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
            Times.Once);
        VerifyAnySnackbar(Times.Once());
        returned.Should().BeSameAs(result, "the call has to sit inline in a chain");
    }

    [Fact]
    public void NotifyOnFailure_StaysQuiet_OnSuccess()
    {
        Result.Success().NotifyOnFailure(_snackbar.Object);

        VerifyAnySnackbar(Times.Never());
    }

    [Theory]
    [InlineData(Severity.Normal)]
    [InlineData(Severity.Info)]
    [InlineData(Severity.Success)]
    [InlineData(Severity.Warning)]
    [InlineData(Severity.Error)]
    public void NotifyOnFailure_UsesTheRequestedSeverity(Severity severity)
    {
        var result = Result.Failure(MakeError(ErrorType.Failure, "boom"));

        result.NotifyOnFailure(_snackbar.Object, localizer: null, severity);

        _snackbar.Verify(
            s => s.Add("boom", severity, It.IsAny<Action<SnackbarOptions>>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public void NotifyOnFailure_RaisesTheLocalizedWording()
    {
        var result = Result.Failure(MakeError(ErrorType.NotFound, "Order.NotFound"));

        result.NotifyOnFailure(_snackbar.Object, Localizer());

        _snackbar.Verify(
            s => s.Add(
                "That order no longer exists.",
                Severity.Error,
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public void NotifyOnFailure_Generic_RaisesOneSnackbarAndReturnsTheSameTypedInstance()
    {
        var result = Result.Failure<int>(MakeError(ErrorType.Unexpected, "boom"));

        var returned = result.NotifyOnFailure(_snackbar.Object);

        returned.Should().BeSameAs(result);
        VerifyAnySnackbar(Times.Once());
    }

    [Fact]
    public void NotifyOnFailure_Throws_ForANullSnackbar()
    {
        var result = Result.Failure(MakeError(ErrorType.Failure, "boom"));

        Func<Result> act = () => result.NotifyOnFailure(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // == HasErrorType, IsNotFound, IsUnauthorized ==
    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.Invariant)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.UnprocessableEntity)]
    [InlineData(ErrorType.Failure)]
    [InlineData(ErrorType.Unexpected)]
    public void HasErrorType_FindsTheCategoryAmongSeveralErrors(ErrorType errorType)
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "noise", "Noise"),
            MakeError(errorType, "the one we look for", "Target"));

        result.HasErrorType(errorType).Should().BeTrue();
    }

    [Fact]
    public void HasErrorType_ReturnsFalse_WhenNoErrorCarriesTheCategory()
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "a", "A"),
            MakeError(ErrorType.Invariant, "b", "B"));

        result.HasErrorType(ErrorType.Forbidden).Should().BeFalse();
    }

    [Theory]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Validation)]
    public void SuccessResults_NeverMatchAnyErrorCategory(ErrorType errorType)
    {
        Result.Success().HasErrorType(errorType).Should().BeFalse();
        Result.Success(1).IsNotFound().Should().BeFalse();
        Result.Success("value").IsUnauthorized().Should().BeFalse();
    }

    [Fact]
    public void IsNotFound_IsTrue_ForANotFoundFailure()
    {
        var result = Result.Failure(MakeError(ErrorType.NotFound, "gone"));

        result.IsNotFound().Should().BeTrue();
    }

    [Fact]
    public void IsNotFound_IsFalse_ForAnotherFailureCategory()
    {
        var result = Result.Failure(MakeError(ErrorType.Conflict, "already there"));

        result.IsNotFound().Should().BeFalse();
    }

    [Fact]
    public void IsUnauthorized_IsTrue_ForAnUnauthorizedFailure()
    {
        var result = Result.Failure(MakeError(ErrorType.Unauthorized, "no token"));

        result.IsUnauthorized().Should().BeTrue();
    }

    [Fact]
    public void IsUnauthorized_IsFalse_ForAForbiddenFailure()
    {
        // 403 is a different page decision than 401: known caller, refused action.
        var result = Result.Failure(MakeError(ErrorType.Forbidden, "not yours"));

        result.IsUnauthorized().Should().BeFalse();
    }

    private static Error MakeError(ErrorType type, string message, string code = "Test.Code") =>
        new(code, message, type);

    private static Result Failure(params Error[] errors) => Result.Failure(errors);

    private static StubLocalizer Localizer() => new(Translations);

    private void VerifyAnySnackbar(Times times) =>
        _snackbar.Verify(
            s => s.Add(
                It.IsAny<string>(),
                It.IsAny<Severity>(),
                It.IsAny<Action<SnackbarOptions>>(),
                It.IsAny<string>()),
            times);

    /// <summary>
    /// Hand-written localizer: a known key resolves, an unknown one comes back with
    /// <c>ResourceNotFound</c> set, which is exactly the pass-through signal the extensions read.
    /// </summary>
    private sealed class StubLocalizer(IReadOnlyDictionary<string, string> entries) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            entries.TryGetValue(name, out var value)
                ? new LocalizedString(name, value, resourceNotFound: false)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            entries.Select(entry => new LocalizedString(entry.Key, entry.Value, resourceNotFound: false));
    }
}
