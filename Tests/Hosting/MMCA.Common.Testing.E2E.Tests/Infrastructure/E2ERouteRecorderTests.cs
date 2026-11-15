using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Infrastructure;

namespace MMCA.Common.Testing.E2E.Tests.Infrastructure;

/// <summary>
/// The E2E route recorder logs which pages and API paths a run reaches, for the test-gap report. It
/// must never write a query string or a fragment (guest-order tokens and sign-in links ride there),
/// must ignore other origins, and must stay inert unless <c>E2E_ROUTE_LOG_DIR</c> is set. Driven
/// without a browser: the recorder takes plain URLs, and <see cref="E2ETestBase"/> forwards
/// Playwright's navigation and request events to it.
/// </summary>
[Collection(E2EEnvironmentCollection.Name)]
public sealed class E2ERouteRecorderTests : IDisposable
{
    private const string VariableName = "E2E_ROUTE_LOG_DIR";
    private static readonly Uri Origin = new("https://ui.test");

    private readonly string _directory = Directory.CreateTempSubdirectory("mmca-routelog-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable(VariableName);

    /// <summary>A guest-order token in the query string never reaches the log.</summary>
    [Fact]
    public async Task Request_IsWrittenAsItsPathOnly_WithNoQueryOrFragment()
    {
        var recorder = new E2ERouteRecorder(_directory, "GuestOrderTests.Claim", [Origin]);

        recorder.Record("request", "GET", new Uri("https://ui.test/guest-orders/5?token=abc#x"));
        await recorder.FlushAsync();

        var content = await File.ReadAllTextAsync(Directory.GetFiles(_directory).Single());
        content.Should().Contain("\"path\":\"/guest-orders/5\"")
            .And.NotContain("token", "a guest-order token in the query must never reach the log")
            .And.NotContain("#x");
    }

    /// <summary>Only the app's own origins are recorded, never a payment provider or other third party.</summary>
    [Fact]
    public async Task RequestToAnotherOrigin_IsNotWritten()
    {
        var recorder = new E2ERouteRecorder(_directory, "Checkout.Stripe", [Origin]);

        recorder.Record("request", "GET", new Uri("https://checkout.stripe.com/pay/cs_test_1"));
        recorder.Record("navigation", "GET", new Uri("https://ui.test/checkout"));
        await recorder.FlushAsync();

        var content = await File.ReadAllTextAsync(Directory.GetFiles(_directory).Single());
        content.Should().Contain("/checkout").And.NotContain("stripe.com", "only the app's own origins are recorded");
    }

    /// <summary>With the variable unset there is no recorder, so nothing can be written.</summary>
    [Fact]
    public void UnsetVariable_CreatesNoRecorderAndWritesNothing()
    {
        Environment.SetEnvironmentVariable(VariableName, null);

        var recorder = E2ERouteRecorder.FromEnvironment("Home.Renders", [Origin]);

        recorder.Should().BeNull("the recorder is opt-in, so a consumer that never sets the variable sees no change");
        Directory.GetFiles(_directory).Should().BeEmpty();
    }

    /// <summary>Setting the variable opts in.</summary>
    [Fact]
    public void SetVariable_CreatesARecorder()
    {
        Environment.SetEnvironmentVariable(VariableName, _directory);

        E2ERouteRecorder.FromEnvironment("Home.Renders", [Origin]).Should().NotBeNull();
    }

    /// <summary>Restores the variable and removes the temp folder.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VariableName, _previous);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A handle still closing on a slow file system must not fail a test that already passed.
        }
    }
}
