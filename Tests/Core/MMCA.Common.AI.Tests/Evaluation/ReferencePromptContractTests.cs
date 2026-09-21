using MMCA.Common.AI.Testing;

namespace MMCA.Common.AI.Tests.Evaluation;

/// <summary>
/// The framework's own prompt-change protocol, running on every pull request: the reference
/// contract is pinned by hash in <c>Evaluation/Golden/prompt-versions.json</c>, so editing its
/// system prompt, its model or its name without bumping the version fails the build with the
/// bump-or-revert message instead of silently invalidating the recorded answers beside it.
/// </summary>
public sealed class ReferencePromptContractTests : PromptContractPinTestsBase
{
    /// <inheritdoc />
    protected override IEnumerable<PromptContract> Contracts => [ReferencePrompts.Contract];

    /// <inheritdoc />
    protected override string PinFilePath => "Evaluation/Golden/prompt-versions.json";
}
