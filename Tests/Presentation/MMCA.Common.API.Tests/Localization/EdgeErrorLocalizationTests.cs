using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Controllers;
using MMCA.Common.API.Localization;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Tests.Localization;

/// <summary>
/// Verifies the controller edge applies <see cref="IErrorLocalizer"/> to the human-readable message while
/// leaving the ProblemDetails <c>title</c> (a machine marker) and the error <c>Code</c>/<c>Source</c>/
/// <c>Target</c> untouched, and that a missing localizer degrades to the English message (ADR-027).
/// </summary>
public sealed class EdgeErrorLocalizationTests
{
    private sealed class StubErrorLocalizer : IErrorLocalizer
    {
        public string Localize(string code, string fallbackMessage) =>
            code == "PhoneNumber.Empty" ? "ES: teléfono vacío" : fallbackMessage;
    }

    private sealed class TestController : ApiControllerBase
    {
        public ObjectResult Invoke(IEnumerable<Error> errors) => HandleFailure(errors);
    }

    private static TestController CreateController(IErrorLocalizer? localizer)
    {
        var services = new ServiceCollection();
        if (localizer is not null)
        {
            services.AddSingleton(localizer);
        }

        return new TestController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() },
            },
        };
    }

    [Fact]
    public void HandleFailure_LocalizesMessage_ButKeepsTitleCodeSourceTarget()
    {
        TestController sut = CreateController(new StubErrorLocalizer());
        Error[] errors = [Error.Invariant("PhoneNumber.Empty", "Phone number cannot be empty.", "Ensure", "phoneNumber")];

        ObjectResult result = sut.Invoke(errors);

        var problem = result.Value as ProblemDetails;
        problem.Should().NotBeNull();
        problem!.Title.Should().Be("Operation failed");

        var entries = problem.Extensions["errors"] as object[];
        entries.Should().NotBeNull();
        object entry = entries![0];
        System.Type type = entry.GetType();
        type.GetProperty("Code")!.GetValue(entry).Should().Be("PhoneNumber.Empty");
        type.GetProperty("Message")!.GetValue(entry).Should().Be("ES: teléfono vacío");
        type.GetProperty("Source")!.GetValue(entry).Should().Be("Ensure");
        type.GetProperty("Target")!.GetValue(entry).Should().Be("phoneNumber");
    }

    [Fact]
    public void HandleFailure_WithNoLocalizerRegistered_KeepsEnglishMessage()
    {
        TestController sut = CreateController(localizer: null);
        Error[] errors = [Error.Invariant("PhoneNumber.Empty", "Phone number cannot be empty.")];

        ObjectResult result = sut.Invoke(errors);

        var problem = result.Value as ProblemDetails;
        var entries = problem!.Extensions["errors"] as object[];
        object entry = entries![0];
        entry.GetType().GetProperty("Message")!.GetValue(entry).Should().Be("Phone number cannot be empty.");
    }
}
