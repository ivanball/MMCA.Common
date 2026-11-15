using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Playwright;
using MMCA.Common.Testing.E2E.Infrastructure;
using Moq;

namespace MMCA.Common.Testing.E2E.Tests.Infrastructure;

/// <summary>
/// The base class's own decisions, driven through mocked Playwright interfaces instead of a browser:
/// how <c>AcceptTermsIfPromptedAsync</c> reads the signed-in user's terms standing and picks its wait
/// (none when the user is current, the long wait when the gate will open, the bounded fallback when
/// the read cannot answer), and how <c>DisposeAsync</c> closes the page, the context and an optional
/// trace, including after a setup that failed part-way.
/// </summary>
/// <remarks>
/// <see cref="PlaywrightFixture.Browser"/> has a private setter because the real fixture launches a
/// browser; the harness assigns a mocked <see cref="IBrowser"/> to it through reflection, which is the
/// one step a mock cannot take on its own.
/// </remarks>
[Collection(E2EEnvironmentCollection.Name)]
public sealed class E2ETestBaseTests : IDisposable
{
    private const string TraceVariable = "E2E_TRACE";
    private const string RouteLogVariable = "E2E_ROUTE_LOG_DIR";
    private const string DefaultProbePath = "/api/Users/me/legal-acceptance";
    private const float ExpectedWaitMs = 15_000;
    private const float FallbackWaitMs = 10_000;

    private readonly string? _previousTrace = Environment.GetEnvironmentVariable(TraceVariable);
    private readonly string? _previousRouteLog = Environment.GetEnvironmentVariable(RouteLogVariable);
    private readonly string _directory = Directory.CreateTempSubdirectory("mmca-e2ebase-").FullName;

    private readonly Mock<IBrowser> _browser = new();
    private readonly Mock<IBrowserContext> _context = new();
    private readonly Mock<ITracing> _tracing = new();
    private readonly Mock<IPage> _page = new();
    private readonly Mock<ILocator> _agree = new();
    private readonly Mock<IAPIRequestContext> _api = new();
    private readonly Mock<IAPIResponse> _response = new();
    private readonly List<LocatorWaitForOptions?> _waits = [];

    /// <summary>Wires a context and a page whose terms dialog never appears, with both variables cleared.</summary>
    public E2ETestBaseTests()
    {
        Environment.SetEnvironmentVariable(TraceVariable, null);
        Environment.SetEnvironmentVariable(RouteLogVariable, null);

        _browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>())).ReturnsAsync(_context.Object);
        _context.SetupGet(c => c.Tracing).Returns(_tracing.Object);
        _context.Setup(c => c.NewPageAsync()).ReturnsAsync(_page.Object);
        _page.Setup(p => p.Locator("input[data-testid='terms-acceptance-gate-agree']", It.IsAny<PageLocatorOptions?>()))
            .Returns(_agree.Object);
        _page.SetupGet(p => p.APIRequest).Returns(_api.Object);

        // The dialog never appears in these tests: each wait ends in Playwright's timeout, which the
        // method treats as "nothing to accept". What is asserted is whether it waited, and for how long.
        _agree.Setup(l => l.WaitForAsync(It.IsAny<LocatorWaitForOptions?>()))
            .Callback<LocatorWaitForOptions?>(_waits.Add)
            .ThrowsAsync(new TimeoutException("no dialog"));
    }

    /// <summary>Restores the environment variables and removes the temp folder.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TraceVariable, _previousTrace);
        Environment.SetEnvironmentVariable(RouteLogVariable, _previousRouteLog);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing on a slow file system must not fail a test that already passed.
        }
    }

    /// <summary>A current acceptance means the gate stays closed, so there is nothing to wait for.</summary>
    [Fact]
    public async Task CurrentAcceptance_ReturnsWithoutWaitingForTheDialog()
    {
        StandingIs("""{"isCurrent":true,"currentVersion":"2026-11"}""");

        await AcceptTermsAsync();

        _waits.Should().BeEmpty("a user whose acceptance is current never sees the gate");
    }

    /// <summary>A stale acceptance of a configured version means the gate will open: wait the long budget.</summary>
    [Fact]
    public async Task StaleAcceptanceOfAConfiguredVersion_WaitsTheLongBudgetForTheDialog()
    {
        StandingIs("""{"isCurrent":false,"currentVersion":"2026-11"}""");

        await AcceptTermsAsync();

        var wait = _waits.Should().ContainSingle().Which!;
        wait.State.Should().Be(WaitForSelectorState.Visible);
        wait.Timeout.Should().Be(ExpectedWaitMs);
    }

    /// <summary>The standing is read whatever the casing of its property names.</summary>
    [Fact]
    public async Task PropertyNames_AreMatchedCaseInsensitively()
    {
        StandingIs("""{"IsCurrent":false,"CurrentVersion":"2026-11"}""");

        await AcceptTermsAsync();

        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(ExpectedWaitMs);
    }

    /// <summary>With no usable current version the gate never opens, whatever the acceptance says.</summary>
    /// <param name="body">The standing the read returns.</param>
    [Theory]
    [InlineData("""{"isCurrent":false,"currentVersion":"   "}""")]
    [InlineData("""{"isCurrent":false,"currentVersion":null}""")]
    [InlineData("""{"isCurrent":false,"currentVersion":7}""")]
    [InlineData("""{"isCurrent":false}""")]
    public async Task NoConfiguredVersion_ReturnsWithoutWaiting(string body)
    {
        StandingIs(body);

        await AcceptTermsAsync();

        _waits.Should().BeEmpty("the gate opens only when a current version is configured");
    }

    /// <summary>A body that is not a standing cannot answer, so the bounded fallback wait applies.</summary>
    /// <param name="body">The body the read returns.</param>
    [Theory]
    [InlineData("""{"currentVersion":"2026-11"}""")]
    [InlineData("""{"isCurrent":"false","currentVersion":"2026-11"}""")]
    [InlineData("""["isCurrent"]""")]
    [InlineData("\"text\"")]
    public async Task BodyThatIsNotAStanding_FallsBackToTheBoundedWait(string body)
    {
        StandingIs(body);

        await AcceptTermsAsync();

        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(FallbackWaitMs);
    }

    /// <summary>An error status cannot answer, and its body is never parsed.</summary>
    [Fact]
    public async Task ErrorStatus_FallsBackToTheBoundedWait_WithoutReadingTheBody()
    {
        _response.SetupGet(r => r.Ok).Returns(false);
        _api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<APIRequestContextOptions?>())).ReturnsAsync(_response.Object);

        await AcceptTermsAsync();

        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(FallbackWaitMs);
        _response.Verify(r => r.JsonAsync(), Times.Never);
    }

    /// <summary>An empty body cannot answer.</summary>
    [Fact]
    public async Task EmptyBody_FallsBackToTheBoundedWait()
    {
        _response.SetupGet(r => r.Ok).Returns(true);
        _response.Setup(r => r.JsonAsync()).ReturnsAsync((JsonElement?)null);
        _api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<APIRequestContextOptions?>())).ReturnsAsync(_response.Object);

        await AcceptTermsAsync();

        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(FallbackWaitMs);
    }

    /// <summary>A read that fails inside Playwright cannot answer.</summary>
    [Fact]
    public async Task ReadThatThrowsAPlaywrightError_FallsBackToTheBoundedWait()
    {
        _api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<APIRequestContextOptions?>()))
            .ThrowsAsync(new PlaywrightException("net::ERR_CONNECTION_REFUSED"));

        await AcceptTermsAsync();

        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(FallbackWaitMs);
    }

    /// <summary>A read that times out cannot answer.</summary>
    [Fact]
    public async Task ReadThatTimesOut_FallsBackToTheBoundedWait()
    {
        _api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<APIRequestContextOptions?>()))
            .ThrowsAsync(new TimeoutException("read timed out"));

        await AcceptTermsAsync();

        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(FallbackWaitMs);
    }

    /// <summary>A body that is not JSON cannot answer.</summary>
    [Fact]
    public async Task BodyThatIsNotJson_FallsBackToTheBoundedWait()
    {
        _response.SetupGet(r => r.Ok).Returns(true);
        _response.Setup(r => r.JsonAsync()).ThrowsAsync(new JsonException("not json"));
        _api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<APIRequestContextOptions?>())).ReturnsAsync(_response.Object);

        await AcceptTermsAsync();

        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(FallbackWaitMs);
    }

    /// <summary>An app with no same-origin proxy gets the bounded wait and no request at all.</summary>
    [Fact]
    public async Task NoProbePath_FallsBackToTheBoundedWait_WithoutARequest()
    {
        await using var sut = await InitializedAsync(probePath: null);

        await sut.AcceptTermsAsync();

        _api.Verify(a => a.GetAsync(It.IsAny<string>(), It.IsAny<APIRequestContextOptions?>()), Times.Never);
        _waits.Should().ContainSingle().Which!.Timeout.Should().Be(FallbackWaitMs);
    }

    /// <summary>The read targets the probe path under the base URL and does not throw on an error status.</summary>
    [Fact]
    public async Task TheStandingIsReadFromTheProbePathUnderTheBaseUrl()
    {
        StandingIs("""{"isCurrent":true,"currentVersion":"2026-11"}""");
        await using var sut = await InitializedAsync(probePath: "/proxy/Users/me/legal-acceptance");

        await sut.AcceptTermsAsync();

        var expectedUrl = new Uri(new Uri(E2ETestConfiguration.BaseUrl), "/proxy/Users/me/legal-acceptance").ToString();
        _api.Verify(
            a => a.GetAsync(expectedUrl, It.Is<APIRequestContextOptions?>(o => o != null && o.FailOnStatusCode == false)),
            Times.Once);
    }

    /// <summary>A test whose setup never ran disposes cleanly.</summary>
    [Fact]
    public async Task Dispose_WithoutInitialize_DoesNothingAndDoesNotThrow()
    {
        var sut = new Harness(FixtureWith(_browser.Object), DefaultProbePath);

        var act = async () => await sut.DisposeAsync();

        await act.Should().NotThrowAsync("a failed setup must not be masked by a teardown NullReferenceException");
    }

    /// <summary>A setup that failed before the context existed does not turn into a teardown failure.</summary>
    [Fact]
    public async Task Dispose_AfterAFailedSetup_WithTracingOn_DoesNotTouchTheMissingContext()
    {
        Environment.SetEnvironmentVariable(TraceVariable, Path.Combine(_directory, "trace.zip"));
        _browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
            .ThrowsAsync(new PlaywrightException("browser closed"));
        var sut = new Harness(FixtureWith(_browser.Object), DefaultProbePath);

        var init = async () => await sut.InitializeAsync();
        await init.Should().ThrowAsync<PlaywrightException>();
        var dispose = async () => await sut.DisposeAsync();

        await dispose.Should().NotThrowAsync();
        _tracing.Verify(t => t.StopAsync(It.IsAny<TracingStopOptions?>()), Times.Never);
    }

    /// <summary>Teardown closes the page and disposes the context, and touches no trace when tracing is off.</summary>
    [Fact]
    public async Task Dispose_ClosesThePageAndDisposesTheContext()
    {
        var sut = await InitializedAsync(DefaultProbePath);

        await sut.DisposeAsync();

        _page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        _context.Verify(c => c.DisposeAsync(), Times.Once);
        _tracing.Verify(t => t.StartAsync(It.IsAny<TracingStartOptions?>()), Times.Never, "tracing is off unless E2E_TRACE is set");
        _tracing.Verify(t => t.StopAsync(It.IsAny<TracingStopOptions?>()), Times.Never);
    }

    /// <summary>Setup opens the context on the base URL, ignoring certificate errors, with the configured timeout.</summary>
    [Fact]
    public async Task Initialize_AppliesTheConfiguredContextOptions()
    {
        await using var sut = await InitializedAsync(DefaultProbePath);

        _browser.Verify(
            b => b.NewContextAsync(It.Is<BrowserNewContextOptions?>(o =>
                o != null && o.IgnoreHTTPSErrors == true && o.BaseURL == E2ETestConfiguration.BaseUrl)),
            Times.Once);
        _context.Verify(c => c.SetDefaultTimeout(E2ETestConfiguration.DefaultTimeout), Times.Once);
    }

    /// <summary>A trace file path records a full trace and writes it to that file on teardown.</summary>
    [Fact]
    public async Task TraceFilePath_StartsTracingAndWritesTheTraceToThatFile()
    {
        var file = Path.Combine(_directory, "single.zip");
        Environment.SetEnvironmentVariable(TraceVariable, file);
        var sut = await InitializedAsync(DefaultProbePath);

        await sut.DisposeAsync();

        _tracing.Verify(
            t => t.StartAsync(It.Is<TracingStartOptions?>(o => o != null && o.Screenshots == true && o.Snapshots == true && o.Sources == true)),
            Times.Once);
        _tracing.Verify(t => t.StopAsync(It.Is<TracingStopOptions?>(o => o != null && o.Path == file)), Times.Once);
        _tracing.Verify(
            t => t.StopAsync(It.IsAny<TracingStopOptions?>()),
            Times.Once,
            "a file path is stopped once; the per-test directory handling must not run after it");
    }

    /// <summary>A trace directory keeps only failing tests' traces, so a passing test writes none.</summary>
    [Fact]
    public async Task TraceDirectory_ForAPassingTest_StopsTracingWithoutWritingAFile()
    {
        Environment.SetEnvironmentVariable(TraceVariable, _directory);
        var sut = await InitializedAsync(DefaultProbePath);

        await sut.DisposeAsync();

        _tracing.Verify(t => t.StopAsync(It.Is<TracingStopOptions?>(o => o == null || o.Path == null)), Times.Once);
        Directory.GetFiles(_directory).Should().BeEmpty("only a failing test leaves a per-test trace");
    }

    /// <summary>A trailing separator marks a directory even before it exists.</summary>
    /// <param name="separator">The trailing separator.</param>
    [Theory]
    [InlineData('/')]
    [InlineData('\\')]
    public async Task TracePathEndingInASeparator_IsADirectory_EvenBeforeItExists(char separator)
    {
        var notYetCreated = Path.Combine(_directory, "later") + separator;
        Environment.SetEnvironmentVariable(TraceVariable, notYetCreated);
        var sut = await InitializedAsync(DefaultProbePath);

        await sut.DisposeAsync();

        _tracing.Verify(t => t.StopAsync(It.Is<TracingStopOptions?>(o => o == null || o.Path == null)), Times.Once);
        _tracing.Verify(t => t.StopAsync(It.Is<TracingStopOptions?>(o => o != null && o.Path != null)), Times.Never);
    }

    private static PlaywrightFixture FixtureWith(IBrowser browser)
    {
        var fixture = new PlaywrightFixture();
        typeof(PlaywrightFixture).GetProperty(nameof(PlaywrightFixture.Browser))!.SetValue(fixture, browser);
        return fixture;
    }

    private void StandingIs(string json)
    {
        using var document = JsonDocument.Parse(json);
        _response.SetupGet(r => r.Ok).Returns(true);
        _response.Setup(r => r.JsonAsync()).ReturnsAsync(document.RootElement.Clone());
        _api.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<APIRequestContextOptions?>())).ReturnsAsync(_response.Object);
    }

    private async Task AcceptTermsAsync()
    {
        await using var sut = await InitializedAsync(DefaultProbePath);
        await sut.AcceptTermsAsync();
    }

    private async Task<Harness> InitializedAsync(string? probePath)
    {
        var sut = new Harness(FixtureWith(_browser.Object), probePath);
        await sut.InitializeAsync();
        return sut;
    }

    /// <summary>Exposes the protected members under test; it declares no tests of its own.</summary>
    private sealed class Harness(PlaywrightFixture fixture, string? probePath) : E2ETestBase(fixture)
    {
        protected override string? TermsAcceptanceProbePath => probePath;

        public Task AcceptTermsAsync() => AcceptTermsIfPromptedAsync();
    }
}
