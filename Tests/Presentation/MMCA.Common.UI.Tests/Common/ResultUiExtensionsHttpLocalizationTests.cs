using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Resources;
using MMCA.Common.UI.Services.Api;

namespace MMCA.Common.UI.Tests.Common;

/// <summary>
/// X-01c (ADC local test run 6): a failure the client synthesized itself, with no server-phrased
/// message to pass through (a bodiless 429 from the gateway rate limiter, a bodiless 5xx, a transport
/// failure, a client timeout), reached a Spanish user as the English "The request failed with HTTP
/// status code 429." because <see cref="ResultUiExtensions.LocalizedErrorMessage(Result, IStringLocalizer?)"/>
/// localized by message text only. The contract pinned here: those failures are localized by their
/// error CODE (<c>Http.{status}</c>, <see cref="HttpResultExecutor.TransportErrorCode"/>,
/// <see cref="HttpResultExecutor.TimeoutErrorCode"/>) against the shared UI resources, with the
/// existing message as the fallback, so a server-phrased message is still shown verbatim.
/// Runs against the real <see cref="SharedResource"/> resources, not a stub.
/// </summary>
public sealed class ResultUiExtensionsHttpLocalizationTests
{
    private const string RawStatusSentence = "The request failed with HTTP status code";

    public static TheoryData<int> BodilessStatuses => [429, 500, 502, 503];

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
}
