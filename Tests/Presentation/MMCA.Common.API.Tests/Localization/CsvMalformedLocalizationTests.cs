using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Localization;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Serialization;

namespace MMCA.Common.API.Tests.Localization;

/// <summary>
/// D8 (Run 15): <see cref="CsvReader"/> names the line of a malformed file in its English message,
/// but the edge localizer replaced that message with the <c>Csv.Malformed</c> resource, which had no
/// line, so the uploader could not find the fault. The resource is now parameterized and the line
/// travels as structured data (the error's <c>Target</c>), so the localized text names it in every
/// culture.
/// </summary>
public sealed class CsvMalformedLocalizationTests
{
    // The quoted field opens on line 2 and is never closed.
    private const string UnclosedQuote = "Name,Email\r\n\"Ada,ada@example.com\r\nGrace,grace@example.com\r\n";

    [Theory]
    [InlineData("en-US", "line 2")]
    [InlineData("es", "línea 2")]
    public void UnclosedQuote_LocalizedAtTheEdge_NamesTheLine(string culture, string expectedLine)
    {
        var result = CsvReader.ReadTable(UnclosedQuote);
        result.IsFailure.Should().BeTrue();

        var message = WithCulture(culture, () =>
            ErrorHttpMapping.BuildErrorsExtension(result.Errors, CreateLocalizer()))
            .Select(MessageOf)
            .Single();

        message.Should().Contain(expectedLine);
    }

    [Fact]
    public void UnclosedQuote_CarriesTheLineAsStructuredData()
    {
        var result = CsvReader.ReadTable(UnclosedQuote);

        result.Errors.Should().ContainSingle(e => e.Code == CsvReader.MalformedCode)
            .Which.Target.Should().Be("2");
    }

    [Fact]
    public void TextAfterAClosingQuote_LocalizedInSpanish_NamesTheLine()
    {
        var result = CsvReader.ReadTable("A,B\r\n1,2\r\n\"x\"y,3\r\n");

        var message = WithCulture("es", () =>
            ErrorHttpMapping.BuildErrorsExtension(result.Errors, CreateLocalizer()))
            .Select(MessageOf)
            .Single();

        message.Should().Contain("línea 3");
    }

    private static IErrorLocalizer CreateLocalizer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddErrorLocalization();
        return services.BuildServiceProvider().GetRequiredService<IErrorLocalizer>();
    }

    private static string MessageOf(object error) =>
        JsonSerializer.SerializeToElement(error).GetProperty("Message").GetString()!;

    private static T WithCulture<T>(string culture, Func<T> action)
    {
        var original = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
