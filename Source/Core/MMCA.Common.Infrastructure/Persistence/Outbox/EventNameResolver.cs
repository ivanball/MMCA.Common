using System.Collections.Concurrent;
using System.Reflection;
using MMCA.Common.Domain.Attributes;

namespace MMCA.Common.Infrastructure.Persistence.Outbox;

/// <summary>
/// One cached lookup of the name an event is STORED under, shared by the two places a stored
/// identity is written: the outbox row (<see cref="OutboxMessage.FromDomainEvent"/>) and the inbox
/// dedup key (<c>IntegrationEventConsumer</c> and <c>UpcastingIntegrationEventConsumer</c>).
/// <para>
/// An event that declares <see cref="EventNameAttribute"/> is stored under that name, which no
/// rename, namespace move, or assembly move changes. An event without the attribute keeps exactly
/// the identity it had before this type existed: the assembly-qualified name in the outbox, the
/// short type name in the inbox. That is what makes adoption opt-in and rows already in flight
/// unaffected.
/// </para>
/// </summary>
internal static class EventNameResolver
{
    /// <summary>
    /// Caches the declared name per event type. <see langword="null"/> (no attribute) is cached too,
    /// so the common unannotated case pays one reflection lookup per type per process rather than
    /// one per event instance.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, string?> DeclaredNameCache = new();

    /// <summary>
    /// Returns the <see cref="EventNameAttribute"/> name declared on <paramref name="eventType"/>,
    /// or <see langword="null"/> when the type does not opt in. The attribute is not inherited, so a
    /// derived event does not silently borrow its base's identity.
    /// </summary>
    /// <param name="eventType">The event type to inspect.</param>
    /// <returns>The declared name, or <see langword="null"/>.</returns>
    internal static string? GetDeclaredName(Type eventType) =>
        DeclaredNameCache.GetOrAdd(
            eventType,
            static type => type.GetCustomAttribute<EventNameAttribute>(inherit: false)?.Name);

    /// <summary>
    /// Returns the name an outbox row stores for <paramref name="eventType"/>: the declared name
    /// when present, otherwise the assembly-qualified name (falling back the same way the outbox
    /// always has, for the exotic types that have no assembly-qualified or full name).
    /// </summary>
    /// <param name="eventType">The event type being persisted.</param>
    /// <returns>The stored event-type string.</returns>
    internal static string GetStorageName(Type eventType) =>
        GetDeclaredName(eventType)
        ?? eventType.AssemblyQualifiedName
        ?? eventType.FullName
        ?? eventType.Name;

    /// <summary>
    /// Returns the name an inbox row stores for <paramref name="eventType"/>: the declared name when
    /// present, otherwise the short type name, which is what every existing inbox row holds.
    /// </summary>
    /// <param name="eventType">The consumed event type.</param>
    /// <returns>The inbox event-type string.</returns>
    internal static string GetInboxName(Type eventType) =>
        GetDeclaredName(eventType) ?? eventType.Name;

    /// <summary>
    /// Reverse lookup for a stored name that is NOT a CLR type name: scans the loaded assemblies for
    /// the type declaring <paramref name="name"/> as its <see cref="EventNameAttribute"/>. This is
    /// the counterpart of <see cref="GetStorageName"/> and is only ever reached once per stored name,
    /// because the caller caches the result (see <c>OutboxMessage.ResolveEventType</c>).
    /// </summary>
    /// <remarks>
    /// The query stays lazy, so the scan stops at the first match instead of materializing every
    /// loaded type, and <c>Type.IsDefined</c> comes first in the predicate because it answers
    /// without constructing the attribute: only the handful of annotated types pay for construction.
    /// </remarks>
    /// <param name="name">The stored event name to resolve.</param>
    /// <returns>The declaring type, or <see langword="null"/> when no loaded type declares that name.</returns>
    internal static Type? FindTypeByDeclaredName(string name) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .SelectMany(GetLoadableTypes)
            .FirstOrDefault(candidate =>
                candidate.IsDefined(typeof(EventNameAttribute), inherit: false)
                && string.Equals(GetDeclaredName(candidate), name, StringComparison.Ordinal));

    /// <summary>
    /// Enumerates an assembly's types, degrading to the subset that loaded when a dependency is
    /// missing. A single unloadable type must not stop the scan: the event being resolved may well
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
