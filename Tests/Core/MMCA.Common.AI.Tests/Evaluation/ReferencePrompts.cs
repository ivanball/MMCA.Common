using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Tests.Evaluation;

/// <summary>
/// The framework's own reference prompt. It exists so this repo runs the evaluation harness it
/// ships: the golden gate and the prompt pin are exercised here, on every pull request, against a
/// contract nobody deploys. A consumer's real contract lives next to the service that calls a
/// model; this one is the worked example and the regression test for the bases themselves.
/// </summary>
internal static class ReferencePrompts
{
    /// <summary>The reference contract's stable name.</summary>
    public const string Name = "reference-summarize";

    /// <summary>The reference contract's version. Bumping it means recording a new hash.</summary>
    public const string Version = "1";

    /// <summary>
    /// The model the reference contract is written for. Deliberately not a real model id: nothing
    /// in this project may reach a provider.
    /// </summary>
    public const string Model = "reference-model";

    /// <summary>The reference system prompt.</summary>
    public const string SystemPrompt =
        "You summarize the text you are given in a single sentence. Answer with the sentence only.";

    /// <summary>Gets the reference contract the pin test and the golden gate both key on.</summary>
    public static PromptContract Contract { get; } = new(Name, Version, Model, SystemPrompt);

    /// <summary>
    /// Builds the chat options every reference request carries: the pinned model, the system prompt
    /// and the three identity properties the contract stamps.
    /// </summary>
    /// <returns>Fresh options for one request.</returns>
    public static ChatOptions ForRequest() => Contract.ToChatOptions();
}
