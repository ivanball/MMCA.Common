using System.Net;
using System.Text.Json.Nodes;
using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Tests.OpenApi;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Tests.Concurrency;

/// <summary>
/// <c>[SupportsIfMatch]</c> owns its response metadata: an action carrying it is documented with the
/// 412 and 428 it can answer, appended after the action's own response types, without duplicating a
/// status the action already declares and without touching any other status. Asserted on the real
/// ApiExplorer output and the served <c>/openapi/v1.json</c>.
/// </summary>
public sealed class SupportsIfMatchResponseMetadataTests
{
    private const string Root = IfMatchMetadataProbeController.Root;

    // -- Rule 2: both codes are added, typed as ProblemDetails --
    [Fact]
    public async Task ActionDeclaringNeither_Gets412And428_WithProblemDetails()
    {
        await using WebApplication app = await CreateHostAsync();

        var description = Describe(app, "PUT", $"{Root}/neither");

        StatusCodesOf(description).Should().Equal(
            StatusCodes.Status204NoContent, StatusCodes.Status412PreconditionFailed, StatusCodes.Status428PreconditionRequired);
        TypeOf(description, StatusCodes.Status412PreconditionFailed).Should().Be<ProblemDetails>();
        TypeOf(description, StatusCodes.Status428PreconditionRequired).Should().Be<ProblemDetails>();

        var responses = await ReadResponseKeysAsync(app, $"/{Root}/neither", "put");
        responses.Should().Contain(["412", "428"], "the document carries the conditional-write failures");
    }

    // -- Rule 3: an action declaring both is untouched --
    [Fact]
    public async Task ActionDeclaringBoth_IsUnchanged_DocumentIdentical()
    {
        await using WebApplication app = await CreateHostAsync();

        StatusCodesOf(Describe(app, "PUT", $"{Root}/both")).Should().Equal(
            StatusCodesOf(Describe(app, "PUT", $"{Root}/both-control")),
            "an action that already declares 412 and 428 gets nothing added");

        var document = await ReadDocumentAsync(app);
        OperationJson(document, $"/{Root}/both", "put").Should().Be(
            OperationJson(document, $"/{Root}/both-control", "put"),
            "the attribute changes nothing in the document of an action that declares both codes");
    }

    [Fact]
    public async Task ActionDeclaring428Only_Gets412Only()
    {
        await using WebApplication app = await CreateHostAsync();

        var description = Describe(app, "PUT", $"{Root}/only-428");

#if NET11_0_OR_GREATER
        // .NET 11 ApiExplorer lists response types in status-code order, whatever order the filters ran in.
        StatusCodesOf(description).Should().Equal(
            [StatusCodes.Status204NoContent, StatusCodes.Status412PreconditionFailed, StatusCodes.Status428PreconditionRequired],
            "428 is already declared, so only 412 is added, and .NET 11 orders response types by status code");
#else
        StatusCodesOf(description).Should().Equal(
            [StatusCodes.Status204NoContent, StatusCodes.Status428PreconditionRequired, StatusCodes.Status412PreconditionFailed],
            "428 is already declared, so only 412 is appended, after the declared types");
#endif
    }

    [Fact]
    public async Task UntypedDeclared412_IsKeptAsDeclared()
    {
        await using WebApplication app = await CreateHostAsync();

        var description = Describe(app, "PUT", $"{Root}/untyped-412");
        var control = Describe(app, "PUT", $"{Root}/untyped-412-control");

        description.SupportedResponseTypes.Count(r => r.StatusCode == StatusCodes.Status412PreconditionFailed)
            .Should().Be(1, "an untyped declaration still counts as declared");
        TypeOf(description, StatusCodes.Status412PreconditionFailed).Should().Be(
            TypeOf(control, StatusCodes.Status412PreconditionFailed),
            "the declared 412 is not replaced by a typed one");
        StatusCodesOf(description).Should().Equal(
            StatusCodes.Status204NoContent, StatusCodes.Status412PreconditionFailed, StatusCodes.Status428PreconditionRequired);

        var document = await ReadDocumentAsync(app);
        ResponseJson(document, $"/{Root}/untyped-412", "put", "412").Should().Be(
            ResponseJson(document, $"/{Root}/untyped-412-control", "put", "412"));
    }

    [Fact]
    public async Task ControllerLevelDeclared412_IsNotAddedAgain()
    {
        await using WebApplication app = await CreateHostAsync();

        var description = Describe(app, "PUT", $"{ControllerLevel412ProbeController.Root}/item");

        description.SupportedResponseTypes.Count(r => r.StatusCode == StatusCodes.Status412PreconditionFailed)
            .Should().Be(1, "a 412 declared on the controller counts as declared for the action");
        description.SupportedResponseTypes.Count(r => r.StatusCode == StatusCodes.Status428PreconditionRequired)
            .Should().Be(1, "the missing 428 is still added");
    }

    // -- Rule 2 / F3: order --
    [Fact]
    public async Task AddedResponses_FollowDeclaredResponses_InOrder()
    {
        await using WebApplication app = await CreateHostAsync();

        StatusCodesOf(Describe(app, "PUT", $"{Root}/ordered")).Should().Equal(
            StatusCodes.Status200OK,
            StatusCodes.Status400BadRequest,
            StatusCodes.Status404NotFound,
            StatusCodes.Status412PreconditionFailed,
            StatusCodes.Status428PreconditionRequired);

        var keys = await ReadResponseKeysAsync(app, $"/{Root}/ordered", "put");
        keys.Should().Equal(["200", "400", "404", "412", "428"], "document response order is filter order");
    }

    // -- Rule 4 --
    [Fact]
    public async Task SuccessCodeUntouched()
    {
        await using WebApplication app = await CreateHostAsync();

        var description = Describe(app, "PUT", $"{Root}/ordered");

        TypeOf(description, StatusCodes.Status200OK).Should().Be<IfMatchMetadataProbeDto>();
        description.SupportedResponseTypes
            .Where(r => r.StatusCode is >= 200 and < 300)
            .Select(r => r.StatusCode)
            .Should().Equal([StatusCodes.Status200OK], "no success code is added or changed");
        TypeOf(description, StatusCodes.Status400BadRequest).Should().Be<ProblemDetails>();
        TypeOf(description, StatusCodes.Status404NotFound).Should().Be<ProblemDetails>();
    }

    // -- Rule 5 --
    [Fact]
    public async Task ClassLevelAttribute_AppliesToEveryAction()
    {
        await using WebApplication app = await CreateHostAsync();

        foreach (var (method, path) in new[] { ("PUT", "first"), ("DELETE", "second") })
        {
            var codes = StatusCodesOf(Describe(app, method, $"{ClassLevelIfMatchProbeController.Root}/{path}"));

            codes.Should().Contain(
                [StatusCodes.Status412PreconditionFailed, StatusCodes.Status428PreconditionRequired],
                "a class-level [SupportsIfMatch] documents every action of the controller ({0} {1})",
                method,
                path);
        }
    }

    [Fact]
    public async Task ActionWithoutAttribute_GetsNothing()
    {
        await using WebApplication app = await CreateHostAsync();

        StatusCodesOf(Describe(app, "PUT", $"{Root}/plain")).Should().Equal(StatusCodes.Status204NoContent);
    }

    // -- Rule 3 + F3: the consumer migration is a no-op on the document --
    // DESIGN-QUESTION: the explicit probe declares 412/428 UNTYPED (the Store/ADC shape the migration deletes), while rule 2 appends TYPED ProblemDetails; equality relies on [ApiController] defaulting untyped 4xx to ProblemDetails.
    [Fact]
    public async Task DeletingDeclared412And428_WhenLast_YieldsIdenticalDocument()
    {
        await using WebApplication app = await CreateHostAsync();

        StatusCodesOf(Describe(app, "PUT", $"{Root}/implicit")).Should().Equal(
            StatusCodesOf(Describe(app, "PUT", $"{Root}/explicit-trailing")));

        var document = await ReadDocumentAsync(app);
        OperationJson(document, $"/{Root}/implicit", "put").Should().Be(
            OperationJson(document, $"/{Root}/explicit-trailing", "put"),
            "deleting hand-written trailing 412/428 declarations must leave the operation byte-identical");
    }

    // -- Rule 6: runtime behaviour is unchanged --
    [Fact]
    public async Task RuntimeBehaviour_IsUnchanged()
    {
        await using WebApplication app = await CreateHostAsync();
        using HttpClient client = app.GetTestClient();

        using var missing = new HttpRequestMessage(HttpMethod.Put, new Uri($"/{Root}/neither", UriKind.Relative));
        using var missingResponse = await client.SendAsync(missing, TestContext.Current.CancellationToken);

        using var present = new HttpRequestMessage(HttpMethod.Put, new Uri($"/{Root}/neither", UriKind.Relative));
        present.Headers.Add(ConcurrencyETag.IfMatchHeaderName, ConcurrencyETag.Format([0, 0, 0, 0, 0, 0, 7, 209]));
        using var presentResponse = await client.SendAsync(present, TestContext.Current.CancellationToken);

        missingResponse.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        presentResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // -- Helpers --
    private static Task<WebApplication> CreateHostAsync() =>
        OpenApiProbeHost.CreateAsync(
            typeof(IfMatchMetadataProbeController),
            typeof(ClassLevelIfMatchProbeController),
            typeof(ControllerLevel412ProbeController));

    private static ApiDescription Describe(WebApplication app, string httpMethod, string relativePath) =>
        app.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Single(d => string.Equals(d.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

    private static int[] StatusCodesOf(ApiDescription description) =>
        [.. description.SupportedResponseTypes.Select(r => r.StatusCode)];

    private static Type? TypeOf(ApiDescription description, int statusCode) =>
        description.SupportedResponseTypes.Single(r => r.StatusCode == statusCode).Type;

    private static async Task<JsonNode> ReadDocumentAsync(WebApplication app)
    {
        using HttpClient client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(body)!;
    }

    private static JsonNode Operation(JsonNode document, string path, string method) =>
        document["paths"]?[path]?[method]
        ?? throw new InvalidOperationException($"The document has no {method} operation at {path}.");

    private static string OperationJson(JsonNode document, string path, string method) =>
        Operation(document, path, method).ToJsonString();

    private static string ResponseJson(JsonNode document, string path, string method, string status) =>
        Operation(document, path, method)["responses"]?[status]?.ToJsonString()
        ?? throw new InvalidOperationException($"The {method} operation at {path} has no {status} response.");

    private static async Task<string[]> ReadResponseKeysAsync(WebApplication app, string path, string method)
    {
        var document = await ReadDocumentAsync(app);
        return [.. Operation(document, path, method)["responses"]!.AsObject().Select(p => p.Key)];
    }
}

/// <summary>The 200 body of the ordered probe action.</summary>
public sealed record IfMatchMetadataProbeDto(int Id);

/// <summary>
/// One action per response-metadata shape. The <c>-control</c> actions declare the same responses
/// without <c>[SupportsIfMatch]</c>, so a comparison isolates what the attribute contributes.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route(IfMatchMetadataProbeController.Root)]
public sealed class IfMatchMetadataProbeController : ControllerBase
{
    public const string Root = "if-match-metadata-probe";

    [HttpPut("neither")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Neither() => NoContent();

    [HttpPut("both")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired, Type = typeof(ProblemDetails))]
    public IActionResult Both() => NoContent();

    [HttpPut("both-control")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired, Type = typeof(ProblemDetails))]
    public IActionResult BothControl() => NoContent();

    [HttpPut("only-428")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired, Type = typeof(ProblemDetails))]
    public IActionResult Only428() => NoContent();

    [HttpPut("untyped-412")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public IActionResult Untyped412() => NoContent();

    [HttpPut("untyped-412-control")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public IActionResult Untyped412Control() => NoContent();

    [HttpPut("ordered")]
    [SupportsIfMatch]
    [ProducesResponseType(typeof(IfMatchMetadataProbeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status404NotFound, Type = typeof(ProblemDetails))]
    public ActionResult<IfMatchMetadataProbeDto> Ordered() => new IfMatchMetadataProbeDto(1);

    [HttpPut("plain")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Plain() => NoContent();

    // The consumer shape before migration: untyped 412/428 declared last (Store CategoriesController).
    [HttpPut("explicit-trailing")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
    public IActionResult ExplicitTrailing() => NoContent();

    // The same action after migration: the two trailing declarations deleted.
    [HttpPut("implicit")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest, Type = typeof(ProblemDetails))]
    public IActionResult Implicit() => NoContent();
}

/// <summary><c>[SupportsIfMatch]</c> on the class: every action is a conditional write.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route(ClassLevelIfMatchProbeController.Root)]
[SupportsIfMatch]
public sealed class ClassLevelIfMatchProbeController : ControllerBase
{
    public const string Root = "if-match-class-probe";

    [HttpPut("first")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult First() => NoContent();

    [HttpDelete("second")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Second() => NoContent();
}

/// <summary>Declares 412 at controller level; the action declares neither code itself.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route(ControllerLevel412ProbeController.Root)]
[ProducesResponseType(StatusCodes.Status412PreconditionFailed, Type = typeof(ProblemDetails))]
public sealed class ControllerLevel412ProbeController : ControllerBase
{
    public const string Root = "if-match-controller-412-probe";

    [HttpPut("item")]
    [SupportsIfMatch]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Item() => NoContent();
}
