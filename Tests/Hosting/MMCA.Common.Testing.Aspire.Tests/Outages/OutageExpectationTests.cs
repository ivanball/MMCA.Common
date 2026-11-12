using System.Net;
using AwesomeAssertions;
using MMCA.Common.Testing.Aspire.Outages;

namespace MMCA.Common.Testing.Aspire.Tests.Outages;

/// <summary>
/// The probe answers an outage must produce, per kind of dependency. A database check is untagged,
/// so it takes part in readiness and a database outage takes the service out of rotation; broker and
/// cache checks are tagged optional and excluded from readiness, so the service keeps taking traffic
/// while its full health report says it is degraded.
/// </summary>
public sealed class OutageExpectationTests
{
    [Fact]
    public void Database_FailsReadiness()
    {
        var expectation = OutageExpectation.For(DependencyOutageKind.Database);

        expectation.Health.Should().Be(HttpStatusCode.ServiceUnavailable);
        expectation.Ready.Should().Be(
            HttpStatusCode.ServiceUnavailable,
            "the database check is untagged, so it is part of readiness");
    }

    [Fact]
    public void Broker_KeepsReadiness_FailsHealth()
    {
        var expectation = OutageExpectation.For(DependencyOutageKind.Broker);

        expectation.Health.Should().Be(HttpStatusCode.ServiceUnavailable);
        expectation.Ready.Should().Be(
            HttpStatusCode.OK,
            "the broker check is tagged optional and excluded from readiness");
    }

    [Fact]
    public void Cache_KeepsReadiness_FailsHealth()
    {
        var expectation = OutageExpectation.For(DependencyOutageKind.Cache);

        expectation.Health.Should().Be(HttpStatusCode.ServiceUnavailable);
        expectation.Ready.Should().Be(
            HttpStatusCode.OK,
            "the cache check is tagged optional and excluded from readiness");
    }

    [Fact]
    public void EveryKind_HasAnExpectation()
    {
        foreach (var kind in Enum.GetValues<DependencyOutageKind>())
        {
            var act = () => OutageExpectation.For(kind);

            act.Should().NotThrow("kind {0} must have an expectation", kind);
            act().Health.Should().Be(
                HttpStatusCode.ServiceUnavailable,
                "every dependency outage shows in the full health report ({0})",
                kind);
        }
    }
}
