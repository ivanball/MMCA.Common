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
    /// <para>
    /// The three <c>SecureStoreTokenStorageService</c> statics (the refresh single-flight, the token
    /// version and the session-ended signal) are process-wide on purpose: the class is registered only
    /// by <c>AddCommonMauiTokenStorage()</c>, a MAUI head is one process with one signed-in user, and
    /// the service is scoped while the <c>AuthDelegatingHandler</c> built by
    /// <c>IHttpClientFactory</c> resolves its own instance, so per-instance state would split one
    /// device's refresh in two. No Blazor Server host registers it, so no circuit can share it.
    /// </para>
    /// </summary>
    protected override IReadOnlyList<string> AllowedStaticMembers =>
    [
        "MMCA.Common.UI.Pages.Common.ErrorMessages._localizer",
        "MMCA.Common.UI.Services.Auth.Tokens.SecureStoreTokenStorageService._hydrateInFlight",
        "MMCA.Common.UI.Services.Auth.Tokens.SecureStoreTokenStorageService._tokenVersion",
        "MMCA.Common.UI.Services.Auth.Tokens.SecureStoreTokenStorageService._processSessionEnded",
    ];

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

    // L196: the match is bounded by AddSingleton's own type and argument lists, so a scoped
    // registration chained after a singleton one is not reported as a singleton.
    [Theory]
    [InlineData("services.AddSingleton<IClock, Clock>().AddScoped<IWizardStateService, WizardStateService>();")]
    [InlineData("services.AddSingleton<IClock, Clock>()\n    .AddScoped<IWizardStateService, WizardStateService>();")]
    [InlineData("services.AddSingleton(typeof(IClock), typeof(Clock)).AddScoped(typeof(WizardStateContainer));")]
    public void SingletonScan_DoesNotReachAcrossAFluentChain(string registration) =>
        ScanOneUiFile("MMCA.Probe.UI", registration).Should().BeEmpty(
            "only the AddScoped call names the state service, and it is scoped");

    // Every shape the statement-wide match caught before stays caught.
    [Theory]
    [InlineData("services.AddSingleton<ProbeStateService>();")]
    [InlineData("services.AddSingleton<IProbe, ProbeStateService>();")]
    [InlineData("services.AddSingleton<IOptions<ProbeOptions>, ProbeStateService>();")]
    [InlineData("services.TryAddSingleton<ProbeStateContainer>();")]
    [InlineData("services.AddSingleton(typeof(ProbeStateService));")]
    [InlineData("services.AddSingleton(typeof(IProbe), typeof(ProbeStateService));")]
    [InlineData("services.AddSingleton(new ProbeStateContainer(clock));")]
    [InlineData("services.AddSingleton<IProbe>(sp => new ProbeStateService(sp.GetRequiredService<IClock>()));")]
    [InlineData("services.AddScoped<IClock, Clock>().AddSingleton<ProbeStateService>();")]
    public void SingletonScan_StillCatchesEverySingletonStateRegistrationShape(string registration) =>
        ScanOneUiFile("MMCA.Probe.UI", registration).Should().ContainSingle().Which.Should().Be("Registrations.cs:1");

    // L196: only a whole "Testing" project-name segment marks test support code, so a production UI
    // folder whose name merely contains the word is still scanned.
    [Theory]
    [InlineData("MMCA.Probe.UI/TestingHelpers", 1)]
    [InlineData("MMCA.Probe.UI/Pages/LoadTesting", 1)]
    [InlineData("MMCA.Probe.Testing.UI", 0)]
    [InlineData("MMCA.Probe.UI/Testing", 0)]
    public void SingletonScan_SkipsOnlyAWholeTestingSegment(string folder, int expectedOffenders) =>
        ScanOneUiFile(folder, "services.AddSingleton<ProbeStateService>();")
            .Should().HaveCount(expectedOffenders);

    private static IReadOnlyList<string> ScanOneUiFile(string folder, string registration)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mmca-l196-{Guid.NewGuid():N}");
        var sourceDir = Path.Combine(root, "Source");
        try
        {
            var directory = Path.Combine([sourceDir, .. folder.Split('/')]);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Registrations.cs"), registration);

            return FindSingletonStateRegistrations(sourceDir, out _);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
