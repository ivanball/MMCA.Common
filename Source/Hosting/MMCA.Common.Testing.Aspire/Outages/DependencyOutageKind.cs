namespace MMCA.Common.Testing.Aspire.Outages;

/// <summary>
/// The kind of dependency an outage scenario takes away, which decides the probe answers the service
/// must give while it is down.
/// </summary>
public enum DependencyOutageKind
{
    /// <summary>
    /// A database. Its health check is untagged, so it takes part in readiness: <c>/health</c> and
    /// <c>/health/ready</c> both turn 503. A database outage only recovers when the stopped resource
    /// keeps its files across a stop and start (a data volume or a persistent lifetime) or the service
    /// rebuilds its own schema, because Aspire recreates the container on start.
    /// </summary>
    Database = 0,

    /// <summary>
    /// A message broker. Its health check is tagged optional and excluded from readiness:
    /// <c>/health</c> turns 503 while <c>/health/ready</c> stays 200.
    /// </summary>
    Broker = 1,

    /// <summary>
    /// A distributed cache. Its health check is tagged optional and excluded from readiness:
    /// <c>/health</c> turns 503 while <c>/health/ready</c> stays 200.
    /// </summary>
    Cache = 2,
}
