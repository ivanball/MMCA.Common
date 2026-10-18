using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using MMCA.Common.Testing.Conformance;
using MMCA.Common.Testing.Fixtures;
using Xunit;

namespace MMCA.Common.Testing.Tests.Conformance;

/// <summary>
/// Covers the opt-in baseline-diff gate of <see cref="OpenApiContractTestsBase{TFixture}"/> end to end:
/// a stub fixture serves a small live document over HTTP, so the gate runs exactly as it does in a
/// consumer's subclass. Every case lives in this one class on purpose: the refresh case sets a
/// process-wide environment variable, and xUnit runs the methods of one class serially.
/// </summary>
public sealed class OpenApiContractTestsBaseBaselineTests
{
    private const string LiveDocument =
        "{"
        + """  "openapi": "3.1.1","""
        + """  "info": { "title": "Fixture | v1", "version": "1.0.0" },"""
        + """  "paths": {"""
        + """    "/Orders": {"""
        + """      "get": { "responses": { "200": { "description": "OK" } } }"""
        + "    },"
        + """    "/Orders/{id}": {"""
        + """      "get": { "responses": { "200": { "description": "OK" }, "404": { "description": "Not Found" } } }"""
        + "    }"
        + "  }"
        + "}";

    [Fact]
    public void Base_DeclaresTheBaselineGate_AsAFact() =>
        typeof(OpenApiContractTestsBase<>)
            .GetMethod(nameof(OpenApiContractTestsBase<>.OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline))!
            .GetCustomAttributes(typeof(FactAttribute), inherit: false)
            .Should().ContainSingle("the baseline gate must run in every consumer's subclass that opts in");

    [Fact]
    public async Task NotOptedIn_TheBaselineGatePasses_WithoutFetchingTheDocument()
    {
        var fixture = new StubFixture(LiveDocument);
        await using var sut = new ProbeContractTests(fixture, baselinePath: null);

        await sut.OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline();

        fixture.Requests.Should().Be(0, "a consumer that does not opt in keeps exactly the behavior it had");
    }

    [Fact]
    public async Task NotOptedIn_TheExistingGatesStillRunAgainstTheLiveDocument()
    {
        var fixture = new StubFixture(LiveDocument);
        await using var sut = new ProbeContractTests(fixture, baselinePath: null);

        await sut.OpenApiDocument_IsServed_AsWellFormedOpenApiDescribingTheApiSurface();
        await sut.OpenApiDocument_DescribesEveryCorePublicResource();

        fixture.Requests.Should().Be(2);
    }

    [Fact]
    public async Task OptedIn_AnIdenticalBaseline_Passes()
    {
        using var directory = new TempDirectory();
        var baselinePath = directory.Write("baseline.json", LiveDocument);
        await using var sut = new ProbeContractTests(new StubFixture(LiveDocument), baselinePath);

        var act = sut.OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline;

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OptedIn_AnAdditiveChange_Passes()
    {
        using var directory = new TempDirectory();
        var live = JsonNode.Parse(LiveDocument)!;
        live["paths"]!["/Customers"] = JsonNode.Parse("""{ "get": { "responses": { "200": { "description": "OK" } } } }""");
        var baselinePath = directory.Write("baseline.json", LiveDocument);
        await using var sut = new ProbeContractTests(new StubFixture(live.ToJsonString()), baselinePath);

        var act = sut.OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline;

        await act.Should().NotThrowAsync("a new path breaks no existing client");
    }

    [Fact]
    public async Task OptedIn_ABreakingChange_FailsNamingKindPointerAndTheRefreshPath()
    {
        using var directory = new TempDirectory();
        var live = JsonNode.Parse(LiveDocument)!;
        live["paths"]!.AsObject().Remove("/Orders/{id}");
        var baselinePath = directory.Write("baseline.json", LiveDocument);
        await using var sut = new ProbeContractTests(new StubFixture(live.ToJsonString()), baselinePath);

        var act = sut.OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline;

        var failure = await act.Should().ThrowAsync<Exception>();
        failure.Which.Message.Should()
            .Contain("RemovedPath /paths/~1Orders~1{id}")
            .And.Contain(OpenApiContractTestsBase<StubFixture>.RefreshBaselineEnvironmentVariable + "=1")
            .And.Contain(baselinePath);
    }

    [Fact]
    public async Task OptedIn_AMissingBaseline_FailsNamingTheRefreshPath()
    {
        using var directory = new TempDirectory();
        var baselinePath = Path.Combine(directory.Path, "missing.json");
        await using var sut = new ProbeContractTests(new StubFixture(LiveDocument), baselinePath);

        var act = sut.OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline;

        var failure = await act.Should().ThrowAsync<Exception>();
        failure.Which.Message.Should()
            .Contain(baselinePath)
            .And.Contain(OpenApiContractTestsBase<StubFixture>.RefreshBaselineEnvironmentVariable + "=1");
        File.Exists(baselinePath).Should().BeFalse("only the explicit refresh path writes a baseline");
    }

    [Fact]
    public async Task OptedIn_WithTheRefreshVariableSet_RewritesTheBaselineAndStillFails()
    {
        using var directory = new TempDirectory();
        var live = JsonNode.Parse(LiveDocument)!;
        live["paths"]!.AsObject().Remove("/Orders/{id}");
        var baselinePath = directory.Write("baseline.json", LiveDocument);
        await using var sut = new ProbeContractTests(new StubFixture(live.ToJsonString()), baselinePath);

        var variable = OpenApiContractTestsBase<StubFixture>.RefreshBaselineEnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "1");
        try
        {
            var act = sut.OpenApiDocument_HasNoBreakingChangesAgainstTheBaseline;

            var failure = await act.Should().ThrowAsync<Exception>(
                "a regenerating run must never pass, or CI could silently accept a breaking change");
            failure.Which.Message.Should().Contain("baseline rewritten").And.Contain("commit");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }

        var rewritten = JsonNode.Parse(await File.ReadAllTextAsync(baselinePath))!;
        rewritten["paths"]!.AsObject().ContainsKey("/Orders/{id}").Should().BeFalse(
            "the refresh writes the live document as the new baseline");
        rewritten["paths"]!.AsObject().ContainsKey("/Orders").Should().BeTrue();
    }

    /// <summary>A consumer-shaped subclass whose baseline path is supplied per case.</summary>
    private sealed class ProbeContractTests(StubFixture fixture, string? baselinePath)
        : OpenApiContractTestsBase<StubFixture>(fixture)
    {
        protected override int MinimumPathCount => 2;

        protected override IReadOnlyList<string> CorePublicResources => ["/Orders"];

        protected override string? BaselineDocumentPath => baselinePath;
    }

    /// <summary>Serves the given document for every request and counts the requests.</summary>
    private sealed class StubFixture(string document) : IIntegrationTestFixture
    {
        public int Requests { get; private set; }

        public HttpClient CreateClient() =>
            new(new StubHandler(this)) { BaseAddress = new Uri("http://localhost") };

        public Task ResetDatabaseAsync() => Task.CompletedTask;

        private sealed class StubHandler(StubFixture owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(owner.Document, Encoding.UTF8, "application/json"),
                });
            }
        }

        private string Document => document;
    }

    /// <summary>A throwaway directory for baseline files, deleted on dispose.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mmca-openapi-baseline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
