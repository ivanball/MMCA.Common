using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// State-management convention rules (§19), driven by the shared
/// <see cref="StateManagementConventionTestsBase"/>: the shared <c>MMCA.Common.UI</c> assembly carries
/// no mutable static state (a static member is shared across every Blazor Server circuit) and its
/// stateful services stay scoped, so the per-circuit state model is CI-enforced.
/// </summary>
public sealed class StateManagementConventionTests : StateManagementConventionTestsBase
{
    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    /// <summary>
    /// <c>ErrorMessages._localizer</c> is a write-once wiring extension point, not per-user state: the root layout
    /// configures the shared <c>IStringLocalizer</c> exactly once (idempotent), and the localizer itself
    /// resolves against the ambient UI culture per call, so no user's state leaks to another. Keeping the
    /// static message API is deliberate (every consumer call site depends on it, ADR-027).
    /// </summary>
    protected override IReadOnlyList<string> AllowedStaticMembers =>
        ["MMCA.Common.UI.Pages.Common.ErrorMessages._localizer"];

    [Fact]
    public void SingletonScan_JudgesPathsBelowTheSourceRoot_AndCatchesAWrappedRegistration()
    {
        // The checkout root itself contains a "Testing" segment: only segments below the source
        // root may exclude a file, or a repo cloned under such a directory verifies nothing.
        var sourceDir = Path.Combine(Path.GetTempPath(), $"mmca-l86-{Guid.NewGuid():N}", "Testing", "Source");
        const string registration = "services.AddSingleton<\n    ProbeStateService>();\n";
        try
        {
            Directory.CreateDirectory(Path.Combine(sourceDir, "MMCA.Probe.UI"));
            Directory.CreateDirectory(Path.Combine(sourceDir, "MMCA.Probe.Domain"));
            File.WriteAllText(Path.Combine(sourceDir, "MMCA.Probe.UI", "Registrations.cs"), registration);
            File.WriteAllText(Path.Combine(sourceDir, "MMCA.Probe.Domain", "Plain.cs"), registration);

            var offenders = FindSingletonStateRegistrations(sourceDir, out var scanned);

            offenders.Should().Equal(["Registrations.cs:1"], "only the UI project's registration is in scope");
            scanned.Should().Be(1, "the UI file is scanned and the Domain file is not");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(sourceDir))!, recursive: true);
        }
    }
}
