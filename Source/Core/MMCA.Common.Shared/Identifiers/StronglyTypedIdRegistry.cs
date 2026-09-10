using System.Reflection;

namespace MMCA.Common.Shared.Identifiers;

/// <summary>
/// The strongly typed identifier types one host has opted into, discovered once at startup by
/// scanning the assemblies that declare them. Registered as a singleton by
/// <c>services.AddStronglyTypedIds(typeof(OrderId).Assembly)</c>.
/// <para>
/// The framework needs the list up front rather than discovering it from the EF model, because the
/// conversion has to be declared as a PRE-CONVENTION type mapping on
/// <c>ApplicationDbContext.ConfigureConventions</c>: at that point no entity type exists yet, and
/// waiting until the model is built would mean the provider CLR type is only known after EF has
/// already chosen a value-generation strategy for the key.
/// </para>
/// <para>
/// A host that declares no identifier types never registers this service, and every framework site
/// that reads it treats its absence as "no wrappers in use", which is the default posture: the
/// primitive identifier aliases (ADR-048/ADR-085) stay the identifier model unless a host opts out.
/// </para>
/// </summary>
public sealed class StronglyTypedIdRegistry
{
    /// <summary>
    /// Initializes a registry over every identifier type declared in <paramref name="assemblies"/>.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan. Duplicates and repeats are collapsed.</param>
    public StronglyTypedIdRegistry(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        IdentifierTypes =
        [
            .. assemblies
                .Distinct()
                .SelectMany(StronglyTypedIdTypeConverters.GetIdentifierTypes)
                .Distinct()
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Initializes a registry over an explicit list of identifier types, for a host that would
    /// rather name them than scan for them.
    /// </summary>
    /// <param name="identifierTypes">The identifier types.</param>
    /// <exception cref="ArgumentException">A listed type is not a strongly typed identifier.</exception>
    public StronglyTypedIdRegistry(IEnumerable<Type> identifierTypes)
    {
        ArgumentNullException.ThrowIfNull(identifierTypes);

        var declared = identifierTypes.Distinct().ToList();

        var offender = declared.Find(t => !StronglyTypedId.IsStronglyTypedId(t));
        if (offender is not null)
        {
            throw new ArgumentException(
                $"'{offender.FullName}' does not implement IStronglyTypedId<{offender.Name}, TValue>.",
                nameof(identifierTypes));
        }

        IdentifierTypes = [.. declared.OrderBy(t => t.FullName, StringComparer.Ordinal)];
    }

    /// <summary>Gets the identifier types this host declares, ordered by full name.</summary>
    public IReadOnlyList<Type> IdentifierTypes { get; }

    /// <summary>
    /// Gets the wrapped primitive for each identifier type, in the same order as
    /// <see cref="IdentifierTypes"/>.
    /// </summary>
    /// <returns>Identifier type paired with the primitive it wraps.</returns>
    public IEnumerable<(Type IdentifierType, Type ValueType)> Describe()
        => IdentifierTypes.Select(t => (t, StronglyTypedId.GetValueType(t)!));
}
