namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// Ambient name of the CQRS use case the current flow is executing, so the persistence layer can
/// stamp every statement it generates with the code path that asked for it.
/// <para>
/// The value flows with the <see cref="ExecutionContext"/> (an <see cref="AsyncLocal{T}"/>), which is
/// what lets a repository several awaits below the decorator pipeline read it without every
/// signature between the two growing a parameter. The logging decorator opens the scope; the EF
/// repositories read it and pass it to <c>TagWith</c>, turning an anonymous statement in a DBA's
/// query store into <c>handler:GetActiveSpeakersQuery spec:ActiveSpeakersSpec</c>.
/// </para>
/// <para>
/// <b>Diagnostics only.</b> Nothing branches on this value: it decorates a SQL comment and nothing
/// else, so a missing scope costs a less specific comment rather than a behavior change. It lives in
/// the Application layer rather than Infrastructure because both ends need it and Application is the
/// highest layer Infrastructure is allowed to see.
/// </para>
/// </summary>
public static class QueryTagScope
{
    private static readonly AsyncLocal<string?> AmbientHandler = new();

    /// <summary>
    /// Gets the name carried by the innermost open scope, or <see langword="null"/> when no scope is
    /// open (a background service, a seeder, a directly-constructed repository in a test).
    /// </summary>
    public static string? Current => AmbientHandler.Value;

    /// <summary>
    /// Opens an ambient scope naming the use case being executed. Dispose restores whatever was
    /// ambient before, so nested scopes (an internal command executed inside another handler) unwind
    /// correctly.
    /// </summary>
    /// <param name="handlerName">
    /// The use-case name to publish. A <see langword="null"/>, empty or whitespace name leaves the
    /// current value untouched, so a caller never has to guard the call site.
    /// </param>
    /// <returns>A handle that restores the previous ambient value when disposed.</returns>
    public static IDisposable Begin(string? handlerName)
    {
        var previous = AmbientHandler.Value;

        if (!string.IsNullOrWhiteSpace(handlerName))
        {
            AmbientHandler.Value = handlerName;
        }

        // The overwhelmingly common case is the outermost scope of a request, whose "previous" is
        // null: that one is served by a single shared instance, so opening a scope allocates nothing
        // at all on the hot path. Only a genuinely nested scope pays for an object.
        return previous is null ? OutermostScope : new AmbientScope(previous);
    }

    /// <summary>
    /// The shared handle returned when nothing was ambient before: disposing it clears the value,
    /// which is exactly what restoring a null previous value means.
    /// </summary>
    private static readonly IDisposable OutermostScope = new AmbientScope(null);

    private sealed class AmbientScope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientHandler.Value = previous;
    }
}
