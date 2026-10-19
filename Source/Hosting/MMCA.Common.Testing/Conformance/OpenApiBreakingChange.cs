namespace MMCA.Common.Testing.Conformance;

/// <summary>
/// One breaking difference between a committed baseline OpenAPI document and the live one, found by
/// <see cref="OpenApiBreakingChangeDetector"/>.
/// </summary>
/// <param name="Kind">What broke.</param>
/// <param name="JsonPointer">
/// The JSON pointer (RFC 6901) of what broke: into the baseline document for something removed or
/// changed, into the live document for something new or newly required there.
/// </param>
public sealed record OpenApiBreakingChange(OpenApiBreakingChangeKind Kind, string JsonPointer)
{
    /// <summary>
    /// A human-readable name for what broke, when the pointer alone does not say it: parameter and
    /// request-body findings name the method, the path and the parameter or body, for example
    /// <c>GET /Orders: query parameter 'page'</c>. <see langword="null"/> for the other kinds.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Renders the change as one report line: the kind, a space, the JSON pointer, then the
    /// <see cref="Detail"/> in parentheses when there is one.
    /// </summary>
    /// <returns>For example <c>RemovedPath /paths/~1Orders</c>.</returns>
    public override string ToString() => Detail is null ? $"{Kind} {JsonPointer}" : $"{Kind} {JsonPointer} ({Detail})";
}
