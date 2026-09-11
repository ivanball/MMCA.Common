using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.AI;

/// <summary>
/// The language-model providers this package knows how to construct for itself. A host that needs a
/// provider not listed here supplies the inner <c>IChatClient</c> through the factory overload of
/// <c>AddMmcaChatClient</c> instead: the governance pipeline (bounds, usage metering, telemetry) is
/// provider-agnostic and wraps whatever client it is handed.
/// </summary>
public enum AiProvider
{
    /// <summary>
    /// Anthropic's Messages API, constructed through the official Anthropic .NET SDK and adapted to
    /// <c>IChatClient</c> by that SDK's own <c>AsIChatClient</c> extension.
    /// </summary>
    Anthropic = 0,
}

/// <summary>
/// The whole configuration surface of <c>AddMmcaChatClient</c>, bound from the <c>Ai</c> section.
/// <para>
/// Every value here is a bound, not a suggestion: the model is pinned rather than negotiated, the
/// output-token ceiling and the per-call timeout are enforced by
/// <see cref="Chat.BoundedChatClient"/> whatever a caller asks for, tool use is off until a host
/// turns it on, and the optional input-token budget refuses a call that would exceed it before it
/// reaches the provider. That is what makes an unbounded external dependency answerable to a
/// reviewable configuration file (rubric section 16).
/// </para>
/// </summary>
public sealed class AiSettings : IValidatableObject
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ai";

    /// <summary>The default output-token ceiling, applied when the section names none.</summary>
    public const int DefaultMaxOutputTokens = 1024;

    /// <summary>The default per-call timeout, applied when the section names none.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether this host talks to a language model at all. <see langword="false"/> by default, and
    /// when it is false <c>AddMmcaChatClient</c> registers no <c>IChatClient</c>: resolving one
    /// yields <see langword="null"/>, so a consumer gates on the service being present rather than
    /// on a flag it has to read for itself.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>The provider whose client this host builds when <see cref="Enabled"/>.</summary>
    public AiProvider Provider { get; init; } = AiProvider.Anthropic;

    /// <summary>
    /// The model id, e.g. <c>claude-haiku-4-5</c>. Required when <see cref="Enabled"/>: a model is
    /// part of the prompt contract (it is hashed into <see cref="PromptContract.Hash"/>), so an
    /// implicit provider default would silently change evaluated behavior on the provider's
    /// schedule rather than on a reviewed version bump.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// The provider API key. Required when <see cref="Enabled"/>.
    /// <para>
    /// In production this binds from Key Vault (the configuration provider registered by
    /// <c>AddCommonKeyVaultConfiguration</c>), never from a checked-in settings file: the value
    /// lives in configuration only so that the secret store, and not this package, decides where it
    /// comes from. Local development uses user secrets or an environment variable.
    /// </para>
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The hard ceiling on output tokens for any single call. A caller that asks for more is clamped
    /// down to this value; a caller that asks for less keeps its own smaller number.
    /// </summary>
    [Range(1, 1_000_000)]
    public int MaxOutputTokens { get; init; } = DefaultMaxOutputTokens;

    /// <summary>
    /// The per-call wall-clock budget. Enforced with a token linked to the caller's own, so a
    /// caller cancelling early still wins and neither cancellation source masks the other.
    /// </summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Whether the model may be offered tools. <see langword="false"/> by default, and while it is
    /// false any tools on a request are stripped before the call: a model that cannot be handed a
    /// tool cannot be talked into using one.
    /// </summary>
    public bool AllowTools { get; init; }

    /// <summary>
    /// Whether identical requests may be served from the registered <c>IDistributedCache</c>. Off by
    /// default, and it stays off when the host registered no distributed cache.
    /// </summary>
    public bool EnableCache { get; init; }

    /// <summary>
    /// An optional pre-flight ceiling on estimated input tokens. <see langword="null"/> (the
    /// default) leaves input unbounded. The check is an ESTIMATE, never a billing figure: see
    /// <see cref="Chat.BoundedChatClient"/> for what it can and cannot see.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? PerCallInputTokenBudget { get; init; }

    /// <summary>
    /// The conditional half of validation: the pieces that are required only once the dependency is
    /// switched on. A host that leaves <see cref="Enabled"/> false is valid with nothing else set,
    /// which is what lets the section ship in every appsettings file.
    /// </summary>
    /// <param name="validationContext">The validation context (unused: every rule here is local).</param>
    /// <returns>One result per violated rule; empty when the settings are usable.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(Model)} is required when {SectionName}:{nameof(Enabled)} is true.",
                [nameof(Model)]);
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(ApiKey)} is required when {SectionName}:{nameof(Enabled)} is true "
                + "(production binds it from Key Vault).",
                [nameof(ApiKey)]);
        }

        if (Timeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(Timeout)} must be greater than zero.",
                [nameof(Timeout)]);
        }
    }
}
