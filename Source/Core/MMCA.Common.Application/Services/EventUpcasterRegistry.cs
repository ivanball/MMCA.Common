using System.Collections.Concurrent;
using System.Reflection;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Application.Services;

/// <summary>
/// Default <see cref="IEventUpcasterRegistry"/>: indexes every registered <see cref="IEventUpcaster"/>
/// by its source contract, precomputes the terminal type of each chain, and preserves the event
/// envelope across hops.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation is constructor-time.</b> Two upcasters claiming the same source, an upcaster mapping
/// a type onto itself, or a chain that forms a cycle are all programming errors, so they throw
/// <see cref="InvalidOperationException"/> naming the offenders rather than returning a
/// <c>Result</c> (the permission-registry precedent). The chain graph is static once DI is built, so
/// terminal types are resolved once here instead of per message.
/// </para>
/// <para>
/// <b>Envelope preservation is the registry's job, not the author's.</b> After each hop
/// <c>MessageId</c> and <c>DateOccurred</c> are stamped from the pre-hop instance onto the upcasted
/// one through cached <see cref="PropertyInfo"/> handles (both are <c>init</c>-only on
/// <c>BaseDomainEvent</c>, which reflection can still set). That keeps consumer-side inbox
/// deduplication keyed on the id the producer published, by construction: upcasters map payload
/// fields only, and one that copies the envelope itself simply gets the same values written twice.
/// </para>
/// </remarks>
public sealed class EventUpcasterRegistry : IEventUpcasterRegistry
{
    /// <summary>
    /// Caches the writable envelope properties per upcast target type. Keyed by the runtime type of
    /// the instance an upcaster produced, so a chain pays the reflection lookup once per contract.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, (PropertyInfo? MessageId, PropertyInfo? DateOccurred)> EnvelopeProperties = new();

    private readonly Dictionary<Type, IEventUpcaster> _bySourceType;
    private readonly Dictionary<Type, Type> _terminalTypes;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventUpcasterRegistry"/> class and validates the
    /// registration graph.
    /// </summary>
    /// <param name="upcasters">Every upcaster registered through <c>AddEventUpcaster</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when two upcasters claim the same source type, an upcaster maps a type onto itself, or
    /// the chain forms a cycle. The message names the offending types.
    /// </exception>
    public EventUpcasterRegistry(IEnumerable<IEventUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);

        _bySourceType = [];
        var offenders = new List<string>();

        foreach (var upcaster in upcasters)
        {
            if (upcaster.SourceType == upcaster.TargetType)
            {
                offenders.Add($"{Describe(upcaster)} maps {Describe(upcaster.SourceType)} onto itself");
                continue;
            }

            if (_bySourceType.TryGetValue(upcaster.SourceType, out var existing))
            {
                offenders.Add($"{Describe(upcaster.SourceType)} is claimed by both {Describe(existing)} and {Describe(upcaster)}");
                continue;
            }

            _bySourceType.Add(upcaster.SourceType, upcaster);
        }

        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid event upcaster registration: " + string.Join("; ", offenders)
                + ". Exactly one upcaster may claim a source contract, and it must produce a different one.");
        }

        _terminalTypes = BuildTerminalTypes(_bySourceType);
    }

    /// <inheritdoc />
    public bool HasUpcasterFor(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return _bySourceType.ContainsKey(eventType);
    }

    /// <inheritdoc />
    public Type ResolveTerminalType(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return _terminalTypes.TryGetValue(eventType, out var terminalType) ? terminalType : eventType;
    }

    /// <inheritdoc />
    public IIntegrationEvent UpcastToTerminal(IIntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var current = integrationEvent;
        var currentType = integrationEvent.GetType();

        // The walk advances by the upcaster's DECLARED target type, not the runtime type of what it
        // returned, so the constructor's acyclicity check is what bounds this loop.
        while (_bySourceType.TryGetValue(currentType, out var upcaster))
        {
            var upcasted = upcaster.Upcast(current)
                ?? throw new InvalidOperationException(
                    $"{Describe(upcaster)} returned null upcasting {Describe(currentType)}; an upcaster must always produce an instance.");

            PreserveEnvelope(current, upcasted);

            current = upcasted;
            currentType = upcaster.TargetType;
        }

        return current;
    }

    /// <summary>
    /// Resolves the terminal type of every registered chain up front, failing on a cycle. The graph is
    /// functional (the constructor already rejected duplicate sources), so a repeated type on a walk
    /// is a cycle rather than a diamond.
    /// </summary>
    /// <param name="bySourceType">The validated source-to-upcaster index.</param>
    /// <returns>A source-type to terminal-type map.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a chain forms a cycle.</exception>
    private static Dictionary<Type, Type> BuildTerminalTypes(Dictionary<Type, IEventUpcaster> bySourceType)
    {
        var terminalTypes = new Dictionary<Type, Type>();

        foreach (var sourceType in bySourceType.Keys)
        {
            var visited = new HashSet<Type> { sourceType };
            var chain = new List<Type> { sourceType };
            var currentType = sourceType;

            while (bySourceType.TryGetValue(currentType, out var upcaster))
            {
                currentType = upcaster.TargetType;
                chain.Add(currentType);

                if (!visited.Add(currentType))
                {
                    throw new InvalidOperationException(
                        "Invalid event upcaster registration: the chain forms a cycle ("
                        + string.Join(" -> ", chain.Select(Describe))
                        + "). Upcasting must move forward to a newer contract and terminate.");
                }
            }

            terminalTypes.Add(sourceType, currentType);
        }

        return terminalTypes;
    }

    /// <summary>
    /// Stamps the envelope of <paramref name="source"/> onto <paramref name="target"/> so the upcasted
    /// instance keeps the identity the producer published.
    /// </summary>
    /// <param name="source">The pre-hop instance.</param>
    /// <param name="target">The instance the upcaster produced.</param>
    private static void PreserveEnvelope(IIntegrationEvent source, IIntegrationEvent target)
    {
        var (messageId, dateOccurred) = EnvelopeProperties.GetOrAdd(
            target.GetType(),
            static type => (
                Writable(type.GetProperty(nameof(IDomainEvent.MessageId), BindingFlags.Public | BindingFlags.Instance)),
                Writable(type.GetProperty(nameof(IDomainEvent.DateOccurred), BindingFlags.Public | BindingFlags.Instance))));

        messageId?.SetValue(target, source.MessageId);
        dateOccurred?.SetValue(target, source.DateOccurred);
    }

    private static PropertyInfo? Writable(PropertyInfo? property) =>
        property is { CanWrite: true } ? property : null;

    private static string Describe(IEventUpcaster upcaster) => Describe(upcaster.GetType());

    private static string Describe(Type type) => type.FullName ?? type.Name;
}
