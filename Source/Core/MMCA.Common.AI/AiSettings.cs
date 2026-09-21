using System.ComponentModel.DataAnnotations;

namespace MMCA.Common.AI;

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
/// <para>
/// Nothing here names a vendor. <see cref="Provider"/> is matched against the
/// <see cref="Providers.IAiProviderFactory.Name"/> of whatever factories the host registered (one
/// per adapter package, <c>MMCA.Common.AI.Anthropic</c> and <c>MMCA.Common.AI.OpenAI</c> ship one
/// each), and the rest of the section reads identically for every provider.
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

    /// <summary>
    /// The name of the provider whose client this host builds when <see cref="Enabled"/>, e.g.
    /// <c>Anthropic</c> or <c>OpenAI</c>. Required when <see cref="Enabled"/>. Matched
    /// case-insensitively against the registered <see cref="Providers.IAiProviderFactory"/> names,
    /// so the value is only valid when the host referenced the matching adapter package and called
    /// its registration method; an unknown name fails at startup naming the registered ones.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// The model id, e.g. <c>claude-haiku-4-5</c> or <c>gpt-5</c>. Required when
    /// <see cref="Enabled"/>: a model is part of the prompt contract (it is hashed into
    /// <see cref="PromptContract.Hash"/>), so an implicit provider default would silently change
    /// evaluated behavior on the provider's schedule rather than on a reviewed version bump. It is
    /// also a bound: <see cref="Chat.BoundedChatClient"/> refuses a request that names a different
    /// model, so no adapter can be asked for one the configuration did not pin.
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
    /// An optional base address for the provider's API. <see langword="null"/> (the default) uses
    /// the provider's public endpoint. Set it to route through an AI gateway, a regional endpoint or
    /// an OpenAI-compatible server; each adapter passes it to its SDK's base-address option.
    /// </summary>
    public Uri? Endpoint { get; init; }

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
    /// Whether this host must register at least one guardrail before it may talk to a model.
    /// <see langword="true"/> by default.
    /// <para>
    /// A guardrail is a policy the feature cannot bypass: it runs inside the composition root, on
    /// every call, whatever the calling code believes it is doing. Defaulting this to
    /// <see langword="true"/> makes "we shipped a model call and nobody inspects it" a startup
    /// failure rather than a finding, and the message names the one-line fix
    /// (<c>AddPiiRedactionGuardrail()</c>). A host that deliberately wants none sets it false, which
    /// is a reviewable line in a configuration file rather than an absence nobody can see.
    /// </para>
    /// </summary>
    public bool RequireGuardrail { get; init; } = true;

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

        if (string.IsNullOrWhiteSpace(Provider))
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(Provider)} is required when {SectionName}:{nameof(Enabled)} is true "
                + "(the name of a registered provider, e.g. Anthropic or OpenAI).",
                [nameof(Provider)]);
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

        if (Endpoint is { IsAbsoluteUri: false })
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(Endpoint)} must be an absolute URI when set.",
                [nameof(Endpoint)]);
        }

        if (Timeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult(
                $"{SectionName}:{nameof(Timeout)} must be greater than zero.",
                [nameof(Timeout)]);
        }
    }
}
