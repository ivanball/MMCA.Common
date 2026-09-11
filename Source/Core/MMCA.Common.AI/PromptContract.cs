using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace MMCA.Common.AI;

/// <summary>
/// A versioned prompt: the four things that decide what a model will answer (its name, its version,
/// the model it was written for, and the system prompt itself), plus the <see cref="Hash"/> that
/// identifies that exact combination.
/// <para>
/// The hash is what makes a language-model dependency evaluable. A golden-replay gate records the
/// expected answers for a hash; when any of the four components changes, the hash changes, the
/// recorded answers no longer apply and the gate demands a re-evaluation instead of silently
/// approving a prompt nobody scored. Editing a prompt without bumping <see cref="Version"/> still
/// moves the hash, so the evaluation cannot be skipped by forgetting.
/// </para>
/// </summary>
/// <param name="Name">The prompt's stable identity, e.g. <c>session-scoring</c>.</param>
/// <param name="Version">The prompt's version, bumped whenever its meaning changes.</param>
/// <param name="Model">The model id this version was written and evaluated against.</param>
/// <param name="SystemPrompt">The system prompt text.</param>
public sealed record PromptContract(string Name, string Version, string Model, string SystemPrompt)
{
    /// <summary>Chat-options property key carrying <see cref="Name"/> to telemetry.</summary>
    public const string NamePropertyKey = "mmca.prompt.name";

    /// <summary>Chat-options property key carrying <see cref="Version"/> to telemetry.</summary>
    public const string VersionPropertyKey = "mmca.prompt.version";

    /// <summary>Chat-options property key carrying <see cref="Hash"/> to telemetry.</summary>
    public const string HashPropertyKey = "mmca.prompt.hash";

    /// <summary>
    /// The lowercase hex SHA-256 of <c>Name|Version|Model|SystemPrompt</c>, stable across platforms:
    /// every component has its line endings normalized to LF first, so a prompt checked out on
    /// Windows and the same prompt checked out on Linux hash identically and a CI gate cannot be
    /// tripped by <c>core.autocrlf</c>.
    /// </summary>
    /// <remarks>
    /// Computed on each read rather than cached in a field. A record's generated copy constructor
    /// copies fields verbatim, so a cached hash would survive a <c>with</c> expression and describe
    /// the prompt the copy was made FROM: exactly the drift this type exists to prevent. Hashing
    /// four short strings is cheap enough that correctness wins outright.
    /// </remarks>
    public string Hash => ComputeHash(Name, Version, Model, SystemPrompt);

    /// <summary>
    /// Builds the chat options for this contract: the model it was written for, the system prompt as
    /// instructions, and the three identity properties telemetry tags by.
    /// </summary>
    /// <returns>A fresh <see cref="ChatOptions"/> carrying this contract.</returns>
    public ChatOptions ToChatOptions() =>
        Apply(new ChatOptions { ModelId = Model, Instructions = SystemPrompt });

    /// <summary>
    /// Stamps this contract's identity onto options a caller already built, so a request that needs
    /// its own temperature or response format still reaches telemetry tagged by prompt.
    /// </summary>
    /// <param name="options">The options to stamp; mutated in place and returned.</param>
    /// <returns>The same <paramref name="options"/> instance, for chaining.</returns>
    public ChatOptions Apply(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AdditionalProperties ??= [];
        options.AdditionalProperties[NamePropertyKey] = Name;
        options.AdditionalProperties[VersionPropertyKey] = Version;
        options.AdditionalProperties[HashPropertyKey] = Hash;

        return options;
    }

    /// <summary>Reads the prompt name a <see cref="Apply(ChatOptions)"/> call stamped, if any.</summary>
    /// <param name="options">The options to read, possibly <see langword="null"/>.</param>
    /// <returns>The stamped name, or <see langword="null"/> when the options carry none.</returns>
    public static string? ReadName(ChatOptions? options) => ReadProperty(options, NamePropertyKey);

    /// <summary>Reads the prompt version a <see cref="Apply(ChatOptions)"/> call stamped, if any.</summary>
    /// <param name="options">The options to read, possibly <see langword="null"/>.</param>
    /// <returns>The stamped version, or <see langword="null"/> when the options carry none.</returns>
    public static string? ReadVersion(ChatOptions? options) => ReadProperty(options, VersionPropertyKey);

    private static string? ReadProperty(ChatOptions? options, string key) =>
        options?.AdditionalProperties is { } properties
            && properties.TryGetValue(key, out var value)
                ? value as string
                : null;

    private static string ComputeHash(string name, string version, string model, string systemPrompt)
    {
        var payload = string.Join(
            '|',
            NormalizeLineEndings(name),
            NormalizeLineEndings(version),
            NormalizeLineEndings(model),
            NormalizeLineEndings(systemPrompt));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
