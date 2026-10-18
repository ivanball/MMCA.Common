using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using MMCA.Common.Testing.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// Contract guard (rubric §9) for a service host's OpenAPI document. Each extracted service host
/// serves <c>/openapi/v1.json</c> outside Production (wired in its <c>Program.cs</c> via
/// <c>MapCommonOpenApi()</c>). This boots the real host in-process and asserts the document is
/// served, is a well-formed OpenAPI 3.x document that describes the API surface, and still exposes
/// the core public resources, so an accidental controller/route removal, or a regression that stops
/// emitting the document, fails CI instead of silently changing the published contract. By default
/// there is no committed snapshot file: the assertions run against the live document, so new
/// controllers can never leave a stale snapshot behind. Authored once here and re-run as a thin
/// subclass per host: the subclass supplies its fixture, its <see cref="MinimumPathCount"/> floor, and
/// the <see cref="CorePublicResources"/> its contract must keep describing.
/// <para>
/// <b>Opt-in baseline diff.</b> A subclass that overrides <see cref="BaselineDocumentPath"/> with the
/// path of a committed baseline document also gets
/// <see cref="OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline"/>: the live document is compared
/// with the baseline by <see cref="OpenApiBreakingChangeDetector"/> and the test fails only on a
/// breaking change, never on an additive or documentation-only one. Refreshing the baseline is a
/// deliberate step: set <see cref="RefreshBaselineEnvironmentVariable"/> to <c>1</c>, run the test once
/// (it rewrites the file and fails), then review and commit the new baseline. Without the override the
/// gate is a no-op and the subclass behaves exactly as before.
/// </para>
/// </summary>
/// <typeparam name="TFixture">The concrete fixture type implementing <see cref="IIntegrationTestFixture"/>.</typeparam>
public abstract class OpenApiContractTestsBase<TFixture> : IntegrationTestBase<TFixture>
    where TFixture : IIntegrationTestFixture
{
    /// <summary>
    /// The environment variable that turns <see cref="OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline"/>
    /// into a refresh: when it is <c>1</c>, the test rewrites the baseline from the live document and
    /// fails, so a regenerating run can never pass in CI. Shared with the framework's own OpenAPI
    /// baseline test, so one variable refreshes every OpenAPI baseline.
    /// </summary>
    public const string RefreshBaselineEnvironmentVariable = "MMCA_UPDATE_OPENAPI_BASELINE";

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    protected OpenApiContractTestsBase(TFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>The path the host serves its OpenAPI document at.</summary>
    protected virtual string OpenApiDocumentPath => "/openapi/v1.json";

    /// <summary>
    /// Minimum number of entries the document's <c>paths</c> object must contain: a coarse floor
    /// under the host's route surface, so a regression that drops a controller wholesale is caught
    /// even when the pinned <see cref="CorePublicResources"/> survive.
    /// </summary>
    protected abstract int MinimumPathCount { get; }

    /// <summary>
    /// The because-reason attached to the <see cref="MinimumPathCount"/> assertion. Override with the
    /// host's route inventory (e.g. "the Catalog service exposes its Categories/Products routes") so
    /// a failure message explains what the floor stands for.
    /// </summary>
    protected virtual string MinimumPathCountBecause => "the service must keep describing its controller route surface";

    /// <summary>
    /// Resource paths (e.g. <c>"/Events"</c>, <c>"/Orders"</c>) the document must keep describing.
    /// Presence (not exact casing) is the contract: a removed or renamed public resource must fail.
    /// </summary>
    protected abstract IReadOnlyList<string> CorePublicResources { get; }

    /// <summary>
    /// Path of the committed baseline OpenAPI JSON document this host's live document is diffed
    /// against, or <see langword="null"/> (the default) to skip the baseline diff entirely. Return an
    /// absolute path into the source tree (for example built from <c>[CallerFilePath]</c> in the
    /// subclass), so the refresh path rewrites the file a developer commits rather than a copy in the
    /// build output. A relative path is resolved against the current directory.
    /// </summary>
    protected virtual string? BaselineDocumentPath => null;

    [Fact]
    public async Task OpenApiDocument_IsServed_AsWellFormedOpenApiDescribingTheApiSurface()
    {
        using var doc = JsonDocument.Parse(await GetOpenApiJsonAsync().ConfigureAwait(false));

        doc.RootElement.GetProperty("openapi").GetString()
            .Should().StartWith("3.", "the document must be OpenAPI 3.x");
        doc.RootElement.GetProperty("info").GetProperty("title").GetString()
            .Should().NotBeNullOrWhiteSpace();
        doc.RootElement.TryGetProperty("paths", out var paths)
            .Should().BeTrue("the document must contain a paths object");
        paths.EnumerateObject().Count()
            .Should().BeGreaterThanOrEqualTo(MinimumPathCount, MinimumPathCountBecause);
    }

    [Fact]
    public async Task OpenApiDocument_DescribesEveryCorePublicResource()
    {
        CorePublicResources.Should().NotBeEmpty(
            "the subclass must pin at least one core public resource path (otherwise this guard passes vacuously)");

        using var doc = JsonDocument.Parse(await GetOpenApiJsonAsync().ConfigureAwait(false));
        var paths = doc.RootElement.GetProperty("paths");

        // Presence (not exact casing) is the contract: a removed or renamed public resource must fail here.
        var missing = CorePublicResources
            .Where(expected => !paths.EnumerateObject()
                .Any(p => string.Equals(p.Name, expected, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        missing.Should().BeEmpty(
            "the OpenAPI contract should still describe every pinned public resource; missing: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public async Task OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline()
    {
        if (BaselineDocumentPath is not { } configuredPath)
        {
            // Not opted in: no baseline, no request, no change from the behavior before this gate.
            return;
        }

        var baselinePath = Path.GetFullPath(configuredPath);
        var liveJson = await GetOpenApiJsonAsync().ConfigureAwait(false);

        if (string.Equals(Environment.GetEnvironmentVariable(RefreshBaselineEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            await File.WriteAllTextAsync(baselinePath, Indent(liveJson)).ConfigureAwait(false);

            // Failing on purpose: a run that regenerates the baseline must never count as a pass.
            const bool baselineRewritten = true;
            baselineRewritten.Should().BeFalse(
                "OpenAPI baseline rewritten at {0} because {1}=1. Review the diff and commit it, then re-run without {1}; a refresh run always fails so CI can never pass while regenerating",
                baselinePath,
                RefreshBaselineEnvironmentVariable);
            return;
        }

        File.Exists(baselinePath).Should().BeTrue(
            "the committed OpenAPI baseline is missing at {0}. {1}",
            baselinePath,
            RefreshGuidance(baselinePath));

        var baselineJson = await File.ReadAllTextAsync(baselinePath).ConfigureAwait(false);
        var changes = OpenApiBreakingChangeDetector.Detect(baselineJson, liveJson);

        changes.Count.Should().Be(
            0,
            "the live OpenAPI document must stay compatible with the committed baseline {0}, but it has {1} breaking change(s):{2}{3}{2}{4}",
            baselinePath,
            changes.Count,
            Environment.NewLine,
            string.Join(Environment.NewLine, changes.Select(change => "  " + change)),
            RefreshGuidance(baselinePath));
    }

    /// <summary>
    /// Fetches the OpenAPI document (anonymously: the document itself is unauthenticated outside
    /// Production) and asserts it is served, returning the raw JSON.
    /// </summary>
    protected async Task<string> GetOpenApiJsonAsync()
    {
        ClearAuthentication();
        using var response = await Client.GetAsync(OpenApiDocumentPath).ConfigureAwait(false);
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the service must serve its OpenAPI document at {0} outside Production",
            OpenApiDocumentPath);
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static string RefreshGuidance(string baselinePath) =>
        $"If the change is intended (a breaking change shipped deliberately, with its consumers told), refresh the baseline: set {RefreshBaselineEnvironmentVariable}=1, run this test once (it rewrites {baselinePath} and fails), then review and commit the file and re-run without the variable.";

    /// <summary>Re-serializes the live document indented, so a committed baseline diffs line by line.</summary>
    private static string Indent(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, IndentedOptions);
    }
}
