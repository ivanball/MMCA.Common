using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Api;
using Moq;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// Covers <see cref="ResultUiExtensions"/>, the page-side half of the Result transport (ADR-030).
/// The behaviors pinned here are the ones a page silently depends on: a failure never unwraps
/// (including value-type payloads, whose default is not null), messages are deduplicated and
/// ordered most severe first, localization is key lookup with pass-through, and a failure raises
/// exactly one toast rather than one per error.
/// </summary>
public sealed class ResultUiExtensionsTests
{
    private const string RawStatusSentence = "The request failed with HTTP status code";

    // SharedResource.resx "Http.429".
    private const string English429 = "Too many requests. Wait a moment and try again.";

    // SharedResource.es.resx "Http.429".
    private const string Spanish429 = "Demasiadas solicitudes. Espere un momento e int\u00E9ntelo de nuevo.";

    // SharedResource.resx "Http.502".
    private const string English502 = "The service is temporarily unavailable. Please try again in a moment.";

    // SharedResource.es.resx "Http.502".
    private const string Spanish502 = "El servicio no est\u00E1 disponible temporalmente. Int\u00E9ntelo de nuevo en unos momentos.";

    // SharedResource.es.resx "Http.Status" formatted with 418 (a status with no Http.{status} key of its own).
    private const string SpanishGeneric418 = "La solicitud fall\u00F3 con el c\u00F3digo de estado HTTP 418.";

    private static readonly IReadOnlyDictionary<string, string> Translations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Order.NotFound"] = "That order no longer exists.",
            ["Auth.Expired"] = "Your session expired.",
            ["Alias.One"] = "Same wording.",
            ["Alias.Two"] = "Same wording.",
        };

    private readonly Mock<IToastService> _toast = new();

    public static TheoryData<int> BodilessStatuses => [429, 500, 502, 503];

    public static TheoryData<string, int, string> SharedTranslations => new()
    {
        { "es-ES", 429, Spanish429 },
        { "en-US", 429, English429 },
        { "es-ES", 502, Spanish502 },
        { "en-US", 502, English502 },
        { "es-ES", 418, SpanishGeneric418 },
    };

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
    public void NotifyOnFailure_RaisesExactlyOneToast_ForSeveralErrors()
    {
        var result = Failure(
            MakeError(ErrorType.Validation, "Name is required.", "A"),
            MakeError(ErrorType.Validation, "Email is required.", "B"),
            MakeError(ErrorType.Validation, "Phone is required.", "C"));

        var returned = result.NotifyOnFailure(_toast.Object);

        _toast.Verify(
            t => t.Show("Name is required. Email is required. Phone is required.", ToastSeverity.Error),
            Times.Once);
        VerifyAnyToast(Times.Once());
        returned.Should().BeSameAs(result, "the call has to sit inline in a chain");
    }

    [Fact]
    public void NotifyOnFailure_StaysQuiet_OnSuccess()
    {
        Result.Success().NotifyOnFailure(_toast.Object);

        VerifyAnyToast(Times.Never());
    }

    [Theory]
    [InlineData(ToastSeverity.Normal)]
    [InlineData(ToastSeverity.Info)]
    [InlineData(ToastSeverity.Success)]
    [InlineData(ToastSeverity.Warning)]
    [InlineData(ToastSeverity.Error)]
    public void NotifyOnFailure_UsesTheRequestedSeverity(ToastSeverity severity)
    {
        var result = Result.Failure(MakeError(ErrorType.Failure, "boom"));

        result.NotifyOnFailure(_toast.Object, localizer: null, severity);

        _toast.Verify(t => t.Show("boom", severity), Times.Once);
    }

    [Fact]
    public void NotifyOnFailure_RaisesTheLocalizedWording()
    {
        var result = Result.Failure(MakeError(ErrorType.NotFound, "Order.NotFound"));

        result.NotifyOnFailure(_toast.Object, Localizer());

        _toast.Verify(t => t.Show("That order no longer exists.", ToastSeverity.Error), Times.Once);
    }

    [Fact]
    public void NotifyOnFailure_Generic_RaisesOneToastAndReturnsTheSameTypedInstance()
    {
        var result = Result.Failure<int>(MakeError(ErrorType.Unexpected, "boom"));

        var returned = result.NotifyOnFailure(_toast.Object);

        returned.Should().BeSameAs(result);
        VerifyAnyToast(Times.Once());
    }

    [Fact]
    public void NotifyOnFailure_Throws_ForANullToastService()
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

    // == HTTP failures localized by code (X-01c, ADC local test run 6) ==
    // A failure the client synthesized itself, with no server-phrased message to pass through (a
    // bodiless 429 from the gateway rate limiter, a bodiless 5xx, a transport failure, a client
    // timeout), reached a Spanish user as the English "The request failed with HTTP status code 429."
    // because LocalizedErrorMessage localized by message text only. The contract pinned here: those
    // failures are localized by their error CODE (Http.{status}, HttpResultExecutor.TransportErrorCode,
    // HttpResultExecutor.TimeoutErrorCode) against the shared UI resources, with the existing message
    // as the fallback, so a server-phrased message is still shown verbatim. Runs against the real
    // SharedResource resources, not a stub.
    [Theory]
    [MemberData(nameof(BodilessStatuses))]
    public void ABodilessStatusFailure_IsLocalizedUnderASpanishCulture(int status)
    {
        var result = ProblemDetailsResultReader.ToFailureResult(status, null);

        var english = MessageUnder("en-US", result);
        var spanish = MessageUnder("es-ES", result);

        spanish.Should().NotBeNullOrWhiteSpace();
        spanish.Should().NotContain(RawStatusSentence, "a Spanish user must not read the synthesized English sentence");
        spanish.Should().NotBe(english, "the Spanish resource must carry its own wording for this status");
    }

    [Fact]
    public async Task ATransportFailure_IsLocalizedUnderASpanishCulture()
    {
        var result = await HttpResultExecutor.ExecuteAsync(
            () => Task.FromException<Result>(new HttpRequestException("Connection refused")),
            CancellationToken.None);
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(HttpResultExecutor.TransportErrorCode);
        var englishMessage = result.Errors[0].Message;

        var spanish = MessageUnder("es-ES", result);

        spanish.Should().NotBeNullOrWhiteSpace();
        spanish.Should().NotBe(englishMessage, "the transport failure is localized by its code");
    }

    [Fact]
    public async Task ATimeout_IsLocalizedUnderASpanishCulture()
    {
        var result = await HttpResultExecutor.ExecuteAsync(
            () => Task.FromException<Result>(new TaskCanceledException("HttpClient.Timeout")),
            CancellationToken.None);
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(HttpResultExecutor.TimeoutErrorCode);
        var englishMessage = result.Errors[0].Message;

        var spanish = MessageUnder("es-ES", result);

        spanish.Should().NotBeNullOrWhiteSpace();
        spanish.Should().NotBe(englishMessage, "the timeout is localized by its code");
    }

    [Fact]
    public void AServerPhrasedMessage_IsStillShownVerbatim()
    {
        // Localization by code must not clobber a message the server already phrased for the user.
        const string serverMessage = "El nombre es obligatorio.";
        var result = Result.Failure(Error.Validation("Validation.Name", serverMessage));

        MessageUnder("es-ES", result).Should().Be(serverMessage);
    }

    // == Page localizer falls back to SharedResource (X-01, local test run 7) ==
    // The client-synthesized HTTP failures (a bodiless 429 from the gateway rate limiter, a bodiless
    // 5xx, any status with no body) carry translations ONLY in the framework's own SharedResource pair
    // (Http.{status} and the generic Http.Status format). A consumer page localizes through its OWN
    // IStringLocalizer<PageType>, which has no Http.* keys, so the lookup by code missed and the
    // English synthesized sentence ("The request failed with HTTP status code 429.") reached a Spanish
    // user. The contract pinned here: whatever localizer the page passes, a synthesized HTTP failure
    // renders the SharedResource translation for the current UI culture. The expected strings are
    // copied from SharedResource.resx / SharedResource.es.resx.
    [Theory]
    [MemberData(nameof(SharedTranslations))]
    public void LocalizedErrorMessage_WithAPageLocalizerLackingHttpKeys_RendersTheSharedTranslation(
        string culture, int status, string expected)
    {
        var result = ProblemDetailsResultReader.ToFailureResult(status, null);

        var message = Under(culture, () => result.LocalizedErrorMessage(new PageLocalizer()));

        message.Should().Be(
            expected,
            $"X-01: a synthesized HTTP {status.ToString(CultureInfo.InvariantCulture)} must fall back to the SharedResource translation under {culture} when the page's own localizer has no Http.* keys");
    }

    [Theory]
    [MemberData(nameof(SharedTranslations))]
    public void LocalizedErrorMessages_WithAPageLocalizerLackingHttpKeys_RendersTheSharedTranslation(
        string culture, int status, string expected)
    {
        var result = ProblemDetailsResultReader.ToFailureResult(status, null);

        var messages = Under(culture, () => result.LocalizedErrorMessages(new PageLocalizer()));

        messages.Should().Equal(
            [expected],
            $"X-01: a synthesized HTTP {status.ToString(CultureInfo.InvariantCulture)} must fall back to the SharedResource translation under {culture} when the page's own localizer has no Http.* keys");
    }

    [Fact]
    public void LocalizedErrorMessage_WithARealPageTypeLocalizer_RendersTheSpanishSharedTranslation()
    {
        // A real ResourceManager-backed localizer for a type with no resx of its own: the shape a
        // consumer page's IStringLocalizer<PageType> has for every Http.* key.
        var result = ProblemDetailsResultReader.ToFailureResult(429, null);

        var message = Under("es-ES", () =>
        {
            using var provider = new ServiceCollection()
                .AddLogging()
                .AddLocalization()
                .BuildServiceProvider();
            var pageLocalizer = provider.GetRequiredService<IStringLocalizer<ResultUiExtensionsTests>>();
            return result.LocalizedErrorMessage(pageLocalizer);
        });

        message.Should().Be(
            Spanish429,
            "X-01: a page's own IStringLocalizer<PageType> has no Http.429 key, so the SharedResource translation must be used");
    }

    [Fact]
    public void NotifyOnFailure_WithAPageLocalizerLackingHttpKeys_ToastsTheSpanishSharedTranslation()
    {
        var toast = new Mock<IToastService>();
        var result = ProblemDetailsResultReader.ToFailureResult(429, null);

        Under("es-ES", () => result.NotifyOnFailure(toast.Object, new PageLocalizer()));

        var shown = toast.Invocations
            .Where(invocation => invocation.Method.Name == nameof(IToastService.Show))
            .Select(invocation => invocation.Arguments[0] as string)
            .ToList();
        shown.Should().Equal(
            [Spanish429],
            "X-01: the toast for a synthesized 429 must carry the SharedResource Spanish translation, not the English synthesized sentence");
    }

    [Fact]
    public void AServerPhrasedMessage_IsStillShownVerbatim_ThroughAPageLocalizer()
    {
        // The fallback applies only to the client-synthesized failures: a message the server
        // already phrased must not be replaced by a shared translation.
        const string serverMessage = "El nombre es obligatorio.";
        var result = Result.Failure(Error.Validation("Validation.Name", serverMessage));

        Under("es-ES", () => result.LocalizedErrorMessage(new PageLocalizer())).Should().Be(serverMessage);
    }

    private static Error MakeError(ErrorType type, string message, string code = "Test.Code") =>
        new(code, message, type);

    private static Result Failure(params Error[] errors) => Result.Failure(errors);

    private static StubLocalizer Localizer() => new(Translations);

    private void VerifyAnyToast(Times times) =>
        _toast.Verify(t => t.Show(It.IsAny<string>(), It.IsAny<ToastSeverity>()), times);

    private static string? MessageUnder(string culture, Result result)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            using var provider = new ServiceCollection()
                .AddLogging()
                .AddLocalization()
                .BuildServiceProvider();
            var localizer = provider.GetRequiredService<IStringLocalizer<SharedResource>>();

            return result.LocalizedErrorMessage(localizer);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static T Under<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

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

    /// <summary>
    /// A consumer page's localizer: its own resource pair holds page keys only, so every Http.* key
    /// comes back with <c>ResourceNotFound</c> set.
    /// </summary>
    private sealed class PageLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.CurrentCulture, name, arguments), resourceNotFound: true);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
