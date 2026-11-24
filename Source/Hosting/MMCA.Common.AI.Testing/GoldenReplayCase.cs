namespace MMCA.Common.AI.Testing;

/// <summary>
/// One case in a golden corpus: a stable id, a one-line note on what the case is here to pin, the
/// recorded response replayed for it, and the hash of the prompt contract it was recorded against.
/// </summary>
/// <param name="Id">The stable case id, used in the failure report.</param>
/// <param name="Description">What this case exists to pin, in one line.</param>
/// <param name="ResponsePath">
/// The recorded response file, resolved relative to <see cref="AppContext.BaseDirectory"/>, so a
/// corpus is data copied next to the test assembly rather than a string compiled into it.
/// </param>
/// <param name="PromptContractHash">
/// The <see cref="PromptContract.Hash"/> of the contract the response was recorded against.
/// <see cref="GoldenReplayTestsBase"/> fails every case whose recorded hash differs from the current
/// contract's, because a recording made under another prompt, model or system text proves nothing
/// about the one that ships.
/// </param>
public sealed record GoldenReplayCase(string Id, string Description, string ResponsePath, string PromptContractHash)
{
    /// <inheritdoc />
    public override string ToString() => Id;
}
