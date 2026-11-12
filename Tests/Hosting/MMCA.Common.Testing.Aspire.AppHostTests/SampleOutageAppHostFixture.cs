using MMCA.Common.Testing.Aspire.Fixtures;

namespace MMCA.Common.Testing.Aspire.AppHostTests;

/// <summary>
/// Boots the sample AppHost in its outage scenario: a SQL Server container with one database, a
/// RabbitMQ broker, and the sample service wired to both with a real outbox. The default scenario
/// (<see cref="SampleAppHostFixture"/>) stays container-free; this one needs a container runtime,
/// which the inherited <see cref="RequiredEnvironment"/> (opt-in plus Docker) already demands.
/// </summary>
public sealed class SampleOutageAppHostFixture
    : AppHostFixtureBase<Projects.MMCA_Common_Testing_Aspire_AppHostTests_SampleAppHost>
{
    /// <summary>The SQL Server container resource.</summary>
    public const string SqlResourceName = "sql";

    /// <summary>The database resource on <see cref="SqlResourceName"/> that holds the service's outbox.</summary>
    public const string DatabaseResourceName = "sampledb";

    /// <summary>The RabbitMQ broker resource.</summary>
    public const string BrokerResourceName = "rabbitmq";

    /// <summary>The sample service resource in the outage scenario.</summary>
    public const string ServiceResourceName = "sample-outage";

    /// <summary>The switch the sample AppHost reads to declare the outage topology instead of the default one.</summary>
    protected override IReadOnlyList<string> AppHostArguments => ["--SampleAppHost:Outage=true"];

    /// <summary>
    /// Five minutes each: a cold agent pulls the SQL Server and RabbitMQ images before the service
    /// can start.
    /// </summary>
    protected override AppHostReadinessBudget Budget { get; } =
        new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

    /// <summary>
    /// Only the service. Its health check is liveness (<c>/alive</c>), never readiness, so this wait
    /// cannot deadlock on the database the outage tests take away; the containers are awaited
    /// transitively through the service's own references.
    /// </summary>
    protected override IReadOnlyCollection<string>? ResourcesToAwait => [ServiceResourceName];
}
