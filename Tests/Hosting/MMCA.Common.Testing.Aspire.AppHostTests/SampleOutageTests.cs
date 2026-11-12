using System.Net;
using Aspire.Hosting.ApplicationModel;
using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MMCA.Common.Testing.Aspire.Outages;
using MMCA.Common.Testing.Support;

namespace MMCA.Common.Testing.Aspire.AppHostTests;

/// <summary>
/// Proves the shipped outage base against a real orchestrator: a dependency container is stopped, the
/// sample service's probes answer the way the framework's health tagging says they must, and the
/// stack recovers once the container is started again.
/// <para>
/// Order-independent by construction: every scenario opens with the base's baseline (both probes 200)
/// and ends with recovery, a test that stops a resource directly starts it again in a
/// <see langword="finally"/>, and the sample service keeps its own schema because Aspire recreates a container on
/// start (the database comes back empty).
/// </para>
/// </summary>
/// <param name="fixture">The collection fixture that owns the running application.</param>
[Collection(SampleOutageCollection.Name)]
public sealed class SampleOutageTests(SampleOutageAppHostFixture fixture)
    : DependencyOutageTestBase<SampleOutageAppHostFixture>(fixture)
{
    /// <summary>The sample service endpoint that writes one outbox row and answers 202.</summary>
    private const string OutboxProbePath = "/sample/outbox";

    [Fact]
    public Task DatabaseOutage_FailsReadiness_KeepsLiveness_AndRecovers()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Skip(Fixture.SkipReason!);
        }

        var scenario = new DependencyOutageScenario(
            SampleOutageAppHostFixture.SqlResourceName,
            DependencyOutageKind.Database,
            SampleOutageAppHostFixture.ServiceResourceName);

        return RunOutageAsync(scenario, TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task BrokerOutage_KeepsReadiness_FailsHealth_AndDrainsTheOutbox()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Skip(Fixture.SkipReason!);
        }

        var scenario = new DependencyOutageScenario(
            SampleOutageAppHostFixture.BrokerResourceName,
            DependencyOutageKind.Broker,
            SampleOutageAppHostFixture.ServiceResourceName)
        {
            OutboxDatabaseResourceName = SampleOutageAppHostFixture.DatabaseResourceName,
            DuringOutage = WriteOneOutboxRowAsync,
        };

        return RunOutageAsync(scenario, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAndStart_ReportExitedThenHealthy()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Skip(Fixture.SkipReason!);
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var notifications = Application.ResourceNotifications;
        var started = false;

        try
        {
            await StopResourceAsync(SampleOutageAppHostFixture.BrokerResourceName, cancellationToken);

            notifications.TryGetCurrentState(SampleOutageAppHostFixture.BrokerResourceName, out var stopped)
                .Should().BeTrue("the broker is a resource the orchestrator tracks");
            stopped!.Snapshot.State.Should().NotBeNull();
            stopped.Snapshot.State!.Text.Should().BeOneOf(
                [KnownResourceStates.Exited, KnownResourceStates.Finished],
                "StopResourceAsync returns only once the resource reports a stopped state");

            await StartResourceAsync(SampleOutageAppHostFixture.BrokerResourceName, cancellationToken);
            started = true;

            notifications.TryGetCurrentState(SampleOutageAppHostFixture.BrokerResourceName, out var running)
                .Should().BeTrue();
            running!.Snapshot.HealthStatus.Should().Be(
                HealthStatus.Healthy,
                "StartResourceAsync returns only once the resource reports healthy");
        }
        finally
        {
            if (!started)
            {
                // Best effort, so a failure above cannot leave the broker down for the next test.
                try
                {
                    await StartResourceAsync(SampleOutageAppHostFixture.BrokerResourceName, CancellationToken.None);
                }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or OperationCanceledException)
                {
                    // The original failure is the one worth reporting.
                }
            }
        }
    }

    /// <summary>
    /// Runs while the broker is down: the service accepts the request and parks one integration event
    /// in its outbox, which the base then asserts is pending and, after recovery, drained.
    /// <para>
    /// Retried until the first 202 (a refused write saves nothing, so at most one row is parked): when
    /// a database outage ran just before this test, the database answers the readiness check as soon
    /// as it exists, which can be up to one schema-keeper tick before its tables do.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the row is written.</returns>
    private async Task WriteOneOutboxRowAsync(CancellationToken cancellationToken)
    {
        // DESIGN-QUESTION: 29(c) says the POST "expects 202"; after amendment A1 the readiness check can
        // pass one schema-keeper tick before the tables exist, so this retries to the first 202.
        using var client = CreateHttpClient(SampleOutageAppHostFixture.ServiceResourceName);

        var status = await TestPolling.PollUntilAsync(
            async () =>
            {
                using var response = await client.PostAsync(
                    new Uri(OutboxProbePath, UriKind.Relative),
                    content: null,
                    cancellationToken);
                return response.StatusCode;
            },
            static code => code == HttpStatusCode.Accepted,
            timeout: TimeSpan.FromSeconds(30),
            interval: TimeSpan.FromSeconds(1));

        status.Should().Be(
            HttpStatusCode.Accepted,
            "the outbox write goes to the database, which is up during a broker outage");
    }
}
