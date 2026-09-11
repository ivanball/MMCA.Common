namespace MMCA.Common.AI.Chat;

/// <summary>
/// An optional, provider-supplied input-size estimator. <see cref="BoundedChatClient"/> asks the
/// client pipeline for one (<c>GetService&lt;IAiTokenEstimator&gt;()</c>) before enforcing
/// <see cref="AiSettings.PerCallInputTokenBudget"/>, and falls back to a character heuristic when no
/// client in the pipeline offers one.
/// <para>
/// It is deliberately declared here rather than taken as a dependency on a tokenizer library: the
/// budget check is a guardrail, not a billing figure, and a package whose whole point is to keep the
/// model dependency small should not acquire a second one to count characters.
/// </para>
/// </summary>
public interface IAiTokenEstimator
{
    /// <summary>Estimates how many input tokens a piece of prompt text will cost.</summary>
    /// <param name="text">The text about to be sent.</param>
    /// <returns>An estimated token count.</returns>
    int EstimateTokenCount(string text);
}
