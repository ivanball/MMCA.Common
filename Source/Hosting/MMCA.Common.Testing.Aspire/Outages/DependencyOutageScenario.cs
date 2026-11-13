namespace MMCA.Common.Testing.Aspire.Outages;

/// <summary>
/// One dependency outage: which resource is stopped, what kind of dependency it is, and which service
/// resource is probed while it is down.
/// </summary>
/// <param name="DependencyResourceName">The Aspire resource that is stopped and started again.</param>
/// <param name="Kind">The kind of dependency, which decides the expected probe answers.</param>
/// <param name="ServiceResourceName">The Aspire service resource whose probes are asserted.</param>
public sealed record DependencyOutageScenario(string DependencyResourceName, DependencyOutageKind Kind, string ServiceResourceName)
{
    /// <summary>The service endpoint name to probe, or <see langword="null"/> for its single endpoint.</summary>
    public string? ServiceEndpointName { get; init; }

    /// <summary>
    /// The database resource that holds the service's outbox. When set, the scenario asserts that at
    /// least one row is pending right after <see cref="DuringOutage"/> and that the outbox drains after
    /// recovery. Only a <see cref="DependencyOutageKind.Broker"/> outage may set it: a stopped database
    /// cannot be counted.
    /// </summary>
    public string? OutboxDatabaseResourceName { get; init; }

    /// <summary>
    /// Work to run while the dependency is down (for example a request that parks an integration
    /// event in the outbox), after the outage probe answers have been asserted.
    /// </summary>
    public Func<CancellationToken, Task>? DuringOutage { get; init; }
}
