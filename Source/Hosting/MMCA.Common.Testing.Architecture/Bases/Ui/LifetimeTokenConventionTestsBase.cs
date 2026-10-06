namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Component-lifetime guard, driven by
/// <see cref="ArchitectureRules.ComponentsReadTheirTokenThroughLifetimeToken"/>: no component under
/// <see cref="ScanRoot"/> reads <c>_cts.Token</c> directly, because reading <c>Token</c> off a disposed
/// source throws <see cref="ObjectDisposedException"/> and crashes the circuit after a navigation away.
/// Components read it as <c>_cts.LifetimeToken()</c> (MMCA.Common.UI) or through a <c>LifetimeToken</c>
/// property. Authored once here and re-run as a thin subclass in each repo, which supplies the scan root
/// (typically <c>Source/Modules</c>) and a minimum number of <c>.razor.cs</c> files that proves the scan
/// reached the component code-behind.
/// </summary>
public abstract class LifetimeTokenConventionTestsBase
{
    /// <summary>The directory scanned recursively, typically the repo's <c>Source/Modules</c>.</summary>
    protected abstract string ScanRoot { get; }

    /// <summary>The fewest <c>.razor.cs</c> files the scan must reach, so a moved root cannot pass vacuously.</summary>
    protected abstract int MinimumCodeBehindFiles { get; }

    /// <summary>
    /// Whether plain <c>.cs</c> files under <see cref="ScanRoot"/> are scanned as well as
    /// <c>.razor</c> and <c>.razor.cs</c>, for component bases written as plain classes. Off by default.
    /// </summary>
    protected virtual bool ScanAllCodeFiles => false;

    [Fact]
    public void Components_ReadTheirTokenOnlyThroughLifetimeToken() =>
        ArchitectureRules.ComponentsReadTheirTokenThroughLifetimeToken(ScanRoot, MinimumCodeBehindFiles, ScanAllCodeFiles);
}
