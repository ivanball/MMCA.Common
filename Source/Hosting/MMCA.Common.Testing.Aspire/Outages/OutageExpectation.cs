using System.Net;

namespace MMCA.Common.Testing.Aspire.Outages;

/// <summary>
/// The probe answers a service must give while one kind of dependency is down. Database checks are
/// untagged and take part in readiness; broker and cache checks are tagged optional and excluded from
/// readiness, so the service stays ready while its full health report is degraded.
/// </summary>
internal static class OutageExpectation
{
    /// <summary>The expected <c>/health</c> and <c>/health/ready</c> status codes for an outage kind.</summary>
    /// <param name="kind">The kind of dependency that is down.</param>
    /// <returns>The expected full-health and readiness status codes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined kind.</exception>
    internal static (HttpStatusCode Health, HttpStatusCode Ready) For(DependencyOutageKind kind) => kind switch
    {
        DependencyOutageKind.Database => (HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable),
        DependencyOutageKind.Broker => (HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK),
        DependencyOutageKind.Cache => (HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown dependency outage kind."),
    };
}
