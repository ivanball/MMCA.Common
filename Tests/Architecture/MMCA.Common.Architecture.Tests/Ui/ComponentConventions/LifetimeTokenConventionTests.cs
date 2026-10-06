using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Ui.ComponentConventions;

/// <summary>
/// The framework's own component-lifetime guard, driven by the shared
/// <see cref="LifetimeTokenConventionTestsBase"/>: nothing in MMCA.Common.UI reads <c>_cts.Token</c>
/// directly. The scan includes plain <c>.cs</c> files, because the framework's page bases
/// (<c>DetailPageBase</c>, <c>DataGridListPageBase</c>) and <c>LatestLoadGuard</c> are plain classes
/// that own the same kind of source. The detector cases are lifted unchanged from ADC's local test.
/// </summary>
public sealed class LifetimeTokenConventionTests : LifetimeTokenConventionTestsBase
{
    protected override string ScanRoot { get; } = Path.Combine(
        ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"), "Source", "Presentation", "MMCA.Common.UI");

    // A floor, not an equality: 17 .razor.cs files in MMCA.Common.UI when the gate landed.
    protected override int MinimumCodeBehindFiles => 15;

    protected override bool ScanAllCodeFiles => true;

    [Theory]
    [InlineData("            var result = await Service.LoadAsync(_cts.Token);", true)]
    [InlineData("            var result = await Service.LoadAsync(id, _cts!.Token);", true)]
    [InlineData("            var result = await Service.LoadAsync(LifetimeToken);", false)]
    [InlineData("    private CancellationToken LifetimeToken => _cts.IsCancellationRequested ? new CancellationToken(canceled: true) : _cts.Token;", false)]
    [InlineData("    private CancellationToken LifetimeToken => _cts is null || _cts.IsCancellationRequested ? new CancellationToken(canceled: true) : _cts.Token;", false)]
    [InlineData("            await Service.LoadAsync(_otherCts.Token);", false)]
    [InlineData("            await Service.LoadAsync(_cts.LifetimeToken());", false)]
    public void Detector_FlagsADirectReadOutsideTheLifetimeTokenProperty(string line, bool flagged) =>
        ArchitectureRules.DirectTokenReads("Sample.razor.cs", [line]).Should().HaveCount(flagged ? 1 : 0);

    [Fact]
    public void DirectRead_IsReportedWithFileLineAndTheFix() =>
        ArchitectureRules.DirectTokenReads("Pages/Sample.razor.cs", ["// header", "await LoadAsync(_cts.Token);"])
            .Should().ContainSingle().Which.Should().Be("Pages/Sample.razor.cs:2 reads _cts.Token directly; use LifetimeToken");

    [Fact]
    public void TooFewCodeBehindFiles_FailsRatherThanPassingVacuously()
    {
        var act = () => ArchitectureRules.ComponentsReadTheirTokenThroughLifetimeToken(ScanRoot, minimumCodeBehindFiles: 10_000);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("expected at least 10000");
    }

    [Fact]
    public void MissingRoot_FailsRatherThanScanningNothing()
    {
        var act = () => ArchitectureRules.ComponentsReadTheirTokenThroughLifetimeToken(Path.Combine(ScanRoot, "NoSuchFolder"), minimumCodeBehindFiles: 0);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("scan root not found");
    }
}
