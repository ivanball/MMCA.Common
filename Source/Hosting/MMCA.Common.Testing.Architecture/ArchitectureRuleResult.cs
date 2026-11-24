namespace MMCA.Common.Testing.Architecture;

/// <summary>The outcome of holding a selection of <see cref="ArchitectureTypes"/> to a condition.</summary>
public sealed class ArchitectureRuleResult
{
    internal ArchitectureRuleResult(IReadOnlyList<string> failingTypeNames) => FailingTypeNames = failingTypeNames;

    /// <summary>Gets a value indicating whether every selected type is on the right side of the condition.</summary>
    public bool IsSuccessful => FailingTypeNames.Count == 0;

    /// <summary>Gets the full names (reflection spelling, <c>Outer+Inner</c>) of the types that broke the condition, in metadata order.</summary>
    public IReadOnlyList<string> FailingTypeNames { get; }
}
