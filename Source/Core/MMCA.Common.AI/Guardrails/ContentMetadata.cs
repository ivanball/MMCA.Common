using Microsoft.Extensions.AI;

namespace MMCA.Common.AI.Guardrails;

/// <summary>
/// The metadata a guardrail keeps when it rebuilds a content item to change its text, shared by
/// <see cref="PiiRedactionGuardrail"/> and <see cref="ContentPolicyGuardrail"/> so the two cannot drift.
/// </summary>
internal static class ContentMetadata
{
    /// <summary>
    /// Copies <see cref="AIContent.AdditionalProperties"/> and <see cref="AIContent.Annotations"/> from
    /// the item being replaced onto its rebuilt copy. <see cref="AIContent.RawRepresentation"/> is NOT
    /// copied: the provider's original object still holds the unredacted text, and the OpenAI
    /// adapters send a raw representation verbatim in place of the item, which would undo the
    /// redaction.
    /// </summary>
    /// <typeparam name="TContent">The rebuilt item's type.</typeparam>
    /// <param name="source">The item being replaced.</param>
    /// <param name="rebuilt">The rebuilt item carrying the redacted text.</param>
    /// <returns><paramref name="rebuilt"/>, with the source's metadata.</returns>
    internal static TContent WithMetadataOf<TContent>(AIContent source, TContent rebuilt)
        where TContent : AIContent
    {
        rebuilt.AdditionalProperties = source.AdditionalProperties;
        rebuilt.Annotations = source.Annotations;
        return rebuilt;
    }
}
