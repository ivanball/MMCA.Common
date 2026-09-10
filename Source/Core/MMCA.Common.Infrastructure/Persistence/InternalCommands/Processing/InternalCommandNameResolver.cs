using System.Collections.Concurrent;
using System.Reflection;
using MMCA.Common.Application.InternalCommands;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands.Processing;

/// <summary>
/// One cached lookup of the name an internal command is STORED under. It is the job-queue
/// counterpart of <c>EventNameResolver</c> and follows the same two rules: a command that declares
/// <see cref="InternalCommandNameAttribute"/> is stored under that name, which no rename, namespace
/// move, or assembly move changes; a command without the attribute is stored under its
/// assembly-qualified name.
/// <para>
/// A sibling rather than a shared generic helper on purpose: the two resolvers answer for different
/// attributes and the outbox one also owns the inbox's short-name variant, so folding them together
/// would mean reworking the outbox to serve a feature it does not participate in.
/// </para>
/// </summary>
internal static class InternalCommandNameResolver
{
    /// <summary>
    /// Caches the declared name per command type. <see langword="null"/> (no attribute) is cached
    /// too, so the common unannotated case pays one reflection lookup per type per process rather
    /// than one per scheduled row.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, string?> DeclaredNameCache = new();

    /// <summary>
    /// Returns the <see cref="InternalCommandNameAttribute"/> name declared on
    /// <paramref name="commandType"/>, or <see langword="null"/> when the type does not opt in. The
    /// attribute is not inherited, so a derived command does not silently borrow its base's identity.
    /// </summary>
    /// <param name="commandType">The command type to inspect.</param>
    /// <returns>The declared name, or <see langword="null"/>.</returns>
    internal static string? GetDeclaredName(Type commandType) =>
        DeclaredNameCache.GetOrAdd(
            commandType,
            static type => type.GetCustomAttribute<InternalCommandNameAttribute>(inherit: false)?.Name);

    /// <summary>
    /// Returns the name a queued row stores for <paramref name="commandType"/>: the declared name
    /// when present, otherwise the assembly-qualified name (falling back the same way the outbox
    /// does, for the exotic types that have no assembly-qualified or full name).
    /// </summary>
    /// <param name="commandType">The command type being persisted.</param>
    /// <returns>The stored command-type string.</returns>
    internal static string GetStorageName(Type commandType) =>
        GetDeclaredName(commandType)
        ?? commandType.AssemblyQualifiedName
        ?? commandType.FullName
        ?? commandType.Name;

    /// <summary>
    /// Reverse lookup for a stored name that is NOT a CLR type name: scans the loaded assemblies for
    /// the type declaring <paramref name="name"/> as its
    /// <see cref="InternalCommandNameAttribute"/>. Reached at most once per stored name, because the
    /// caller caches the result (see <c>InternalCommandMessage.ResolveCommandType</c>).
    /// </summary>
    /// <remarks>
    /// The query stays lazy, so the scan stops at the first match instead of materializing every
    /// loaded type, and <c>Type.IsDefined</c> comes first in the predicate because it answers without
    /// constructing the attribute: only the handful of annotated types pay for construction.
    /// </remarks>
    /// <param name="name">The stored command name to resolve.</param>
    /// <returns>The declaring type, or <see langword="null"/> when no loaded type declares that name.</returns>
    internal static Type? FindTypeByDeclaredName(string name) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .SelectMany(GetLoadableTypes)
            .FirstOrDefault(candidate =>
                candidate.IsDefined(typeof(InternalCommandNameAttribute), inherit: false)
                && string.Equals(GetDeclaredName(candidate), name, StringComparison.Ordinal));

    /// <summary>
    /// Enumerates an assembly's types, degrading to the subset that loaded when a dependency is
    /// missing. A single unloadable type must not stop the scan: the command being resolved may well
    /// live in one of the assemblies after it.
    /// </summary>
    /// <param name="assembly">The assembly to enumerate.</param>
    /// <returns>The types that could be loaded.</returns>
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
