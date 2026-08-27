using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.UI.Components;
using MMCA.Common.UI.Resources;
using MudBlazor;

namespace MMCA.Common.UI.Tests.Components;

/// <summary>
/// Covers <see cref="ErrorSummary"/>, the one error block a form shows for both failure shapes it
/// has: a failed <see cref="Result"/> from the API and the validation messages the form produced
/// before the call. It renders nothing at all when there is nothing to say, so it can sit
/// unconditionally in markup, and it deduplicates across both sources.
/// </summary>
public sealed class ErrorSummaryTests : BunitTestBase
{
    private static readonly IReadOnlyDictionary<string, string> SharedEntries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shared.Generic"] = "Something went wrong.",
        };

    private static readonly IReadOnlyDictionary<string, string> PageEntries =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Order.NotFound"] = "That order no longer exists.",
            ["Name.Required"] = "Name is required.",
        };

    private static readonly string[] BlankMessages = [string.Empty, "   "];

    // Registered after the base class's AddLocalization, so this wins: it stands in for the
    // framework's own SharedResource localizer that the component injects as its default.
    public ErrorSummaryTests() =>
        Services.AddSingleton<IStringLocalizer<SharedResource>>(new StubSharedLocalizer(SharedEntries));

    [Fact]
    public void RendersNothing_WhenBothInputsAreAbsent()
    {
        var cut = RenderUnderTest<ErrorSummary>(_ => { });

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void RendersNothing_ForASuccessfulResult()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p.Add(c => c.Result, Result.Success()));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void RendersNothing_ForAnEmptyOrBlankMessageList()
    {
        var empty = RenderUnderTest<ErrorSummary>(p => p
            .Add<IEnumerable<string>?>(c => c.Messages, Array.Empty<string>()));
        var blank = RenderUnderTest<ErrorSummary>(p => p
            .Add<IEnumerable<string>?>(c => c.Messages, BlankMessages));

        empty.Markup.Trim().Should().BeEmpty();
        blank.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void RendersPlainText_ForASingleMessage()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Result, Failure(MakeError(ErrorType.Failure, "The only problem"))));

        cut.Markup.Should().Contain("The only problem");
        cut.FindAll("li").Should().BeEmpty("one message reads as a sentence, not as a list of one");
    }

    [Fact]
    public void RendersOneListItemPerMessage_ForSeveralMessages()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("First problem", "Second problem")));

        var items = cut.FindAll("li");
        items.Should().HaveCount(2);
        items[0].TextContent.Should().Contain("First problem");
        items[1].TextContent.Should().Contain("Second problem");
    }

    [Fact]
    public void DeduplicatesRepeatedMessagesAcrossBothSources()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Result, Failure(MakeError(ErrorType.Validation, "Name is required.")))
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("Name is required.", "Email is required.")));

        var items = cut.FindAll("li");
        items.Should().HaveCount(2);
        items[0].TextContent.Should().Contain("Name is required.");
        items[1].TextContent.Should().Contain("Email is required.");
    }

    [Fact]
    public void PutsTheApiVerdictAheadOfTheFormMessages()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Result, Failure(MakeError(ErrorType.Conflict, "The record changed while you were editing.")))
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("Name is required.")));

        var items = cut.FindAll("li");
        items.Should().HaveCount(2);
        items[0].TextContent.Should().Contain("The record changed while you were editing.");
    }

    [Fact]
    public void LocalizesEveryMessageThroughTheSuppliedLocalizer_AndPassesUnknownKeysThrough()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Result, Failure(MakeError(ErrorType.NotFound, "Order.NotFound")))
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("Name.Required", "The server already localized this"))
            .Add(c => c.Localizer, PageLocalizer()));

        cut.Markup.Should().Contain("That order no longer exists.");
        cut.Markup.Should().Contain("Name is required.");
        cut.Markup.Should().Contain("The server already localized this");
        cut.Markup.Should().NotContain("Order.NotFound");
    }

    [Fact]
    public void FallsBackToTheInjectedSharedLocalizer_WhenNoLocalizerIsSupplied()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Result, Failure(MakeError(ErrorType.Unexpected, "Shared.Generic"))));

        cut.Markup.Should().Contain("Something went wrong.");
        cut.Markup.Should().NotContain("Shared.Generic");
    }

    [Fact]
    public void RendersTheTitle_WhenSupplied()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Title, "We could not save your changes")
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("Name is required.")));

        cut.Markup.Should().Contain("We could not save your changes");
        cut.Find(".mmca-error-summary-title").Should().NotBeNull();
    }

    [Fact]
    public void DefaultsToADenseTextErrorAlertWithNoTitleBlock()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("Name is required.")));

        var alert = cut.FindComponent<MudAlert>().Instance;
        alert.Severity.Should().Be(Severity.Error);
        alert.Variant.Should().Be(Variant.Text);
        alert.Dense.Should().BeTrue();
        alert.Class.Should().Be("mb-4");
        cut.Markup.Should().NotContain("mmca-error-summary-title");
    }

    [Theory]
    [InlineData(Severity.Warning)]
    [InlineData(Severity.Info)]
    [InlineData(Severity.Error)]
    public void AppliesTheSeverityParameter(Severity severity)
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("Name is required."))
            .Add(c => c.Severity, severity));

        cut.FindComponent<MudAlert>().Instance.Severity.Should().Be(severity);
    }

    [Fact]
    public void AppliesTheClassParameter()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add<IEnumerable<string>?>(c => c.Messages, Messages("Name is required."))
            .Add(c => c.Class, "mt-8 custom-summary"));

        cut.FindComponent<MudAlert>().Instance.Class.Should().Be("mt-8 custom-summary");
        cut.Markup.Should().Contain("custom-summary");
    }

    [Fact]
    public void RecomputesTheMessages_WhenParametersChange()
    {
        var cut = RenderUnderTest<ErrorSummary>(p => p
            .Add(c => c.Result, Failure(MakeError(ErrorType.Failure, "First attempt failed"))));
        cut.Markup.Should().Contain("First attempt failed");

        cut.Render(p => p.Add(c => c.Result, Result.Success()));

        cut.Markup.Trim().Should().BeEmpty("a retry that succeeded must clear the block");
    }

    /// <summary>
    /// Wraps the message list so each call site allocates through one method instead of an inline
    /// constant array argument (CA1861).
    /// </summary>
    private static string[] Messages(params string[] messages) => messages;

    private static Error MakeError(ErrorType type, string message) => new("Test.Code", message, type);

    private static Result Failure(Error error) => Result.Failure(error);

    private static StubSharedLocalizer PageLocalizer() => new(PageEntries);

    /// <summary>
    /// Hand-written localizer standing in for both the injected <c>SharedResource</c> default and a
    /// page's own resource pair: a known key resolves, an unknown one comes back with
    /// <c>ResourceNotFound</c> set, which is the pass-through signal.
    /// </summary>
    private sealed class StubSharedLocalizer(IReadOnlyDictionary<string, string> entries)
        : IStringLocalizer<SharedResource>
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
