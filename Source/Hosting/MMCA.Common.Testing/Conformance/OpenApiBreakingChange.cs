namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// One breaking difference between a committed baseline OpenAPI document and the live one, found by
/// <see cref="OpenApiBreakingChangeDetector"/>.
/// </summary>
/// <param name="Kind">What broke.</param>
/// <param name="JsonPointer">
/// The JSON pointer (RFC 6901) of what broke: into the baseline document for something removed or
/// changed, into the live document for a property that is new there.
/// </param>
public sealed record OpenApiBreakingChange(OpenApiBreakingChangeKind Kind, string JsonPointer)
{
    /// <summary>Renders the change as one report line: the kind, a space, then the JSON pointer.</summary>
    /// <returns>For example <c>RemovedPath /paths/~1Orders</c>.</returns>
    public override string ToString() => $"{Kind} {JsonPointer}";
}
