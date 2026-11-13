using Aspire.Hosting.Testing;
using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Fixtures;
using MMCA.Common.Testing.Aspire.Outages;

namespace MMCA.Common.Testing.Aspire.Tests.Outages;

/// <summary>
/// The parts of <see cref="DependencyOutageTestBase{TFixture}"/> that hold before any resource is
/// touched. The fixture here is never started, so its <c>Application</c> throws
/// <see cref="InvalidOperationException"/>: an <see cref="ArgumentException"/> from
/// <c>RunOutageAsync</c> therefore proves the scenario was refused BEFORE any command ran.
/// </summary>
public sealed class DependencyOutageTestBaseTests
{
    [Theory]
    [InlineData(DependencyOutageKind.Database)]
    [InlineData(DependencyOutageKind.Cache)]
    public async Task OutboxOnANonBrokerOutage_Throws(DependencyOutageKind kind)
    {
        await using var fixture = new NeverStartedFixture();
        var outage = new OutageProbe(fixture);
        var scenario = new DependencyOutageScenario("dependency", kind, "service")
        {
            OutboxDatabaseResourceName = "servicedb",
        };

        var act = () => outage.RunAsync(scenario, TestContext.Current.CancellationToken);

        // A stopped database cannot be counted, and a cache outage parks no outbox row, so only a
        // broker outage may ask for a drain assertion.
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AnInvalidBudget_IsRejected_BeforeAnyCommandRuns()
    {
        await using var fixture = new NeverStartedFixture();
        var outage = new OutageProbe(fixture)
        {
            BudgetOverride = new DependencyOutageBudget(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)),
        };
        var scenario = new DependencyOutageScenario("dependency", DependencyOutageKind.Broker, "service");

        var act = () => outage.RunAsync(scenario, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task OutboxTableName_DefaultsToDboOutboxMessages()
    {
        await using var fixture = new NeverStartedFixture();
        var outage = new OutageProbe(fixture);

        outage.ExposedOutboxTableName.Should().Be(
            "dbo.OutboxMessages",
            "the framework maps the outbox to dbo.OutboxMessages on SQL Server");
    }

    [Fact]
    public async Task ProbeInterval_DefaultsToOneSecond_AndTheBudgetToTheDefault()
    {
        await using var fixture = new NeverStartedFixture();
        var outage = new OutageProbe(fixture);

        outage.ExposedProbeInterval.Should().Be(TimeSpan.FromSeconds(1));
        outage.ExposedOutageBudget.Should().Be(DependencyOutageBudget.Default);
    }

    /// <summary>A fixture whose <c>InitializeAsync</c> is never called, so nothing is started.</summary>
    private sealed class NeverStartedFixture : AppHostFixtureBase
    {
        protected override Task<IDistributedApplicationTestingBuilder> CreateBuilderAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This fixture is never started.");
    }

    /// <summary>A concrete outage base exposing the protected surface under test.</summary>
    private sealed class OutageProbe(NeverStartedFixture fixture) : DependencyOutageTestBase<NeverStartedFixture>(fixture)
    {
        public DependencyOutageBudget? BudgetOverride { get; init; }

        public string ExposedOutboxTableName => OutboxTableName;

        public TimeSpan ExposedProbeInterval => ProbeInterval;

        public DependencyOutageBudget ExposedOutageBudget => OutageBudget;

        protected override DependencyOutageBudget OutageBudget => BudgetOverride ?? base.OutageBudget;

        public Task RunAsync(DependencyOutageScenario scenario, CancellationToken cancellationToken) =>
            RunOutageAsync(scenario, cancellationToken);
    }
}
