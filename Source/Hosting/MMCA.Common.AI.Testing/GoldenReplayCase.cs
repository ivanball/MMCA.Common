namespace MMCA.Common.AI.Testing;

/// <summary>
/// One case in a golden corpus: a stable id, a one-line note on what the case is here to pin, and
/// the recorded response replayed for it.
/// </summary>
/// <param name="Id">The stable case id, used in the failure report.</param>
/// <param name="Description">What this case exists to pin, in one line.</param>
/// <param name="ResponsePath">
/// The recorded response file, resolved relative to <see cref="AppContext.BaseDirectory"/>, so a
/// corpus is data copied next to the test assembly rather than a string compiled into it.
/// </param>
public sealed record GoldenReplayCase(string Id, string Description, string ResponsePath)
{
    /// <inheritdoc />
    public override string ToString() => Id;
}
