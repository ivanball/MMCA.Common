using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;

namespace MMCA.Common.API.Tests.OpenApi;

/// <summary>
/// Contract-snapshot gate over the framework-owned OpenAPI surface: the document that
/// <c>AddCommonApiVersioning</c> + <c>AddCommonOpenApi</c> + <c>MapCommonOpenApi</c> generate is fetched
/// from a real in-memory host, normalized, and diffed against the committed
/// <c>openapi-baseline.v1.json</c>. Anything that moves the generated contract (an SDK or
/// <c>Asp.Versioning.OpenApi</c> bump, a change to the framework's OpenAPI registration, the
/// unbound-route-token backfill, the versioned document naming convention, or the generated
/// <c>ProblemDetails</c> error schema) fails this test instead of reaching consumers unnoticed.
/// <para>
/// The probe controllers stand in for a consumer's real controllers deliberately: this test guards the
/// framework's document-generation behaviour, not any concrete API. Consumer hosts keep their own
/// contract-snapshot tests over their own endpoints.
/// </para>
/// <para>
/// Regeneration is deliberate, never automatic. After an intended contract change, set
/// <c>MMCA_UPDATE_OPENAPI_BASELINE=1</c>, re-run this test to rewrite the baseline in the source tree,
/// then review the diff and commit it in the same pull request as the change that caused it.
/// </para>
/// </summary>
public sealed class OpenApiBaselineTests
{
    /// <summary>Set to <c>1</c> to rewrite the committed baseline instead of asserting against it.</summary>
    private const string UpdateBaselineVariable = "MMCA_UPDATE_OPENAPI_BASELINE";

    /// <summary>Root properties dropped before comparison because they vary per host, not per contract.</summary>
    private static readonly HashSet<string> VolatileRootProperties =
        new(StringComparer.Ordinal) { "servers" };

    // ── The gate: the generated document must equal the committed baseline ──
    [Fact]
    public async Task GeneratedOpenApiDocument_MatchesTheCommittedBaseline()
    {
        await using WebApplication app = await OpenApiProbeHost.CreateAsync(
            typeof(SegmentVersionedProbeController),
            typeof(UnboundRouteTokenProbeController),
            typeof(ProblemDetailsProbeController));
        using HttpClient client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the contract surface cannot be compared if the document does not generate");

        string actual = Normalize(await response.Content.ReadAsStringAsync());
        string baselinePath = GetBaselinePath();

        if (IsUpdatingBaseline())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            await File.WriteAllTextAsync(baselinePath, actual.ReplaceLineEndings());
            return;
        }

        File.Exists(baselinePath).Should().BeTrue(
            "the committed OpenAPI baseline is missing from " + baselinePath + ". "
            + RegenerationGuidance);

        string expected = Normalize(await File.ReadAllTextAsync(baselinePath));
        actual.Should().Be(
            expected,
            "the framework-owned OpenAPI contract surface changed against " + baselinePath + ". "
            + RegenerationGuidance);
    }

    // ── Normalization must be stable, or the gate would flag noise as drift ──
    [Fact]
    public void Normalize_SortsPropertiesAndDropsVolatileRootContent()
    {
        const string json = """
            {"paths":{},"servers":[{"url":"http://localhost:5000"}],"openapi":"3.1.1"}
            """;

        string normalized = Normalize(json);

        normalized.Should().NotContain("servers", "a host-assigned server URL is not part of the contract");
        normalized.IndexOf("openapi", StringComparison.Ordinal).Should().BeLessThan(
            normalized.IndexOf("paths", StringComparison.Ordinal),
            "object properties are sorted ordinally so generator ordering never shows up as drift");
    }

    // ── Helpers ──

    /// <summary>The guidance appended to every failure so the fix is one documented step.</summary>
    private static string RegenerationGuidance =>
        "If the change is intended, regenerate the baseline deliberately in the same pull request: set "
        + UpdateBaselineVariable
        + "=1 and re-run this test, then review and commit the updated openapi-baseline.v1.json. "
        + "If it is not intended, the framework's OpenAPI generation regressed.";

    /// <summary>
    /// Reduces a generated document to a comparable form: volatile root content is dropped, every object's
    /// properties are ordered ordinally (generators do not promise a stable property order), arrays keep
    /// their order (it is contractual for parameters and enum values), and the result is re-serialized
    /// indented with LF endings so the diff on a failure is readable and platform-independent.
    /// </summary>
    private static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteNormalized(writer, document.RootElement, isRoot: true);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan).ReplaceLineEndings("\n").Trim();
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element, bool isRoot)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in element.EnumerateObject()
                .Where(property => !isRoot || !VolatileRootProperties.Contains(property.Name))
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteNormalized(writer, property.Value, isRoot: false);
            }

            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in element.EnumerateArray())
            {
                WriteNormalized(writer, item, isRoot: false);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    private static bool IsUpdatingBaseline() =>
        string.Equals(Environment.GetEnvironmentVariable(UpdateBaselineVariable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Locates the baseline next to this source file rather than next to the built test assembly, so the
    /// regeneration path writes the file a developer actually commits.
    /// </summary>
    /// <param name="callerFilePath">Supplied by the compiler; never passed explicitly.</param>
    private static string GetBaselinePath([CallerFilePath] string callerFilePath = "") =>
        Path.Combine(Path.GetDirectoryName(callerFilePath)!, "openapi-baseline.v1.json");
}

/// <summary>
/// Probe controller declaring a <c>ProblemDetails</c> failure response, so the baseline covers the error
/// schema the framework's generated contract emits for every consumer, not just the success shapes.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/baseline-probe")]
public sealed class ProblemDetailsProbeController : ControllerBase
{
    /// <summary>Returns the supplied identifier, or a ProblemDetails failure when it is not positive.</summary>
    /// <param name="id">The identifier to look up.</param>
    [HttpGet("{id}")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Get(int id) =>
        id > 0 ? Ok(id.ToString(CultureInfo.InvariantCulture)) : NotFound();
}
