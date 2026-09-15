using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Runs the shipped alert-to-runbook pairing gate (rubric section 13) over the framework's own
/// deployment sample, <c>samples/deployment</c>. The sample is the artifact consumers copy, so a
/// runbook section that went missing there propagates into every repository that lifted it; this is
/// the gate proving its own worked example, which is also the only place in this repository where
/// the base runs against real infrastructure rather than a fixture.
/// </summary>
public sealed class SampleDeploymentObservabilityTests : ObservabilityConventionTestsBase
{
    protected override string BicepResource => "samples.deployment.main.bicep";

    protected override string RunbookResource => "samples.deployment.OPERATIONS.md";

    // The sample provisions failed-requests, server-response-time, availability and ai-token-spend.
    // Raise this with the sample, never to make a red run go green.
    protected override int MinimumAlertSpecs => 4;
}
