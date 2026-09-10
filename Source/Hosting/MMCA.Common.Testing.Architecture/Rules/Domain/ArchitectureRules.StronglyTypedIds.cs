namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>The open interface a strongly typed identifier implements, matched by full name.</summary>
    private const string StronglyTypedIdInterfacePrefix = "MMCA.Common.Shared.Identifiers.IStronglyTypedId";

    /// <summary>The single property a strongly typed identifier is allowed to declare.</summary>
    private const string StronglyTypedIdValueProperty = "Value";

    /// <summary>
    /// Every strongly typed identifier (ADR-115) is a <c>readonly record struct</c> carrying the
    /// wrapped primitive and nothing else.
    /// <para>
    /// The three properties are load-bearing rather than stylistic. <b>struct</b> keeps an
    /// identifier allocation-free on the hot path a primitive alias occupies today, and it is what
    /// the framework's EF value converter, JSON converter and comparer are written against.
    /// <b>readonly</b> is what makes an identifier safe to pass around after it has been stamped on
    /// an entity: a mutable identifier could be changed under a tracked entity whose key EF already
    /// recorded. <b>record</b> supplies the structural equality and <c>GetHashCode</c> the whole
    /// point of the wrapper depends on; a hand-rolled struct that forgets <c>Equals</c> compares by
    /// reference-free field layout in some paths and by <c>object.Equals</c> boxing in others.
    /// <b>No other instance state</b> keeps the wrapper a name for one primitive: a second field
    /// makes the column mapping ambiguous and turns the converter's round trip lossy.
    /// </para>
    /// <para>
    /// The rule is deliberately separate from <c>ValueObjectsAreImmutableSealedInShared</c>. An
    /// identifier is not a <c>ValueObject</c> derivative (it has no validation to fail and no
    /// <c>Result</c>-returning factory), so the value-object rule does not see it, exactly as
    /// <c>Enumeration&lt;T&gt;</c> sits outside that rule for its own reason.
    /// </para>
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    public static void StronglyTypedIdsAreReadonlyRecordStructs(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var identifiers = map.Layers.Select(l => l.Assembly).Distinct()
            .SelectMany(a => a.LoadableTypes)
            .Where(IsStronglyTypedId)
            .ToList();

        var notStructs = identifiers
            .Where(t => !t.IsValueType)
            .Select(t => $"  - {t.FullName} (a strongly typed identifier must be a struct)");

        var notReadonly = identifiers
            .Where(t => t.IsValueType && !IsReadOnlyStruct(t))
            .Select(t => $"  - {t.FullName} (a strongly typed identifier must be declared readonly)");

        var notRecords = identifiers
            .Where(t => !IsRecord(t))
            .Select(t => $"  - {t.FullName} (a strongly typed identifier must be a record, for structural equality)");

        var extraState = identifiers.SelectMany(ExtraStateViolations);

        ArchitectureAssert.NoViolations(
            notStructs.Concat(notReadonly).Concat(notRecords).Concat(extraState),
            "strongly typed identifiers must be readonly record structs whose only instance state is the wrapped Value");
    }

    /// <summary>
    /// Whether the type implements <c>IStronglyTypedId&lt;TSelf, TValue&gt;</c> for ITSELF. Matched by
    /// full name so the rule library keeps no compile dependency on the framework, and self-argument
    /// checked so a type implementing the contract on behalf of another is left to its own
    /// declaration.
    /// </summary>
    private static bool IsStronglyTypedId(Type type) =>
        type.GetInterfaces().Any(i =>
            i.IsGenericType
            && i.GetGenericTypeDefinition().FullName?.StartsWith(StronglyTypedIdInterfacePrefix, StringComparison.Ordinal) == true
            && i.GetGenericArguments()[0] == type);

    /// <summary>
    /// Whether the struct carries the compiler's <c>IsReadOnlyAttribute</c>. Matched by attribute
    /// NAME rather than type, because the attribute is emitted into the declaring assembly on some
    /// targets rather than referenced from the shared framework one.
    /// </summary>
    private static bool IsReadOnlyStruct(Type type) =>
        type.GetCustomAttributesData()
            .Any(a => string.Equals(a.AttributeType.Name, "IsReadOnlyAttribute", StringComparison.Ordinal));

    /// <summary>
    /// Whether the type is a record. Every record (class or struct) gets a compiler-generated
    /// <c>PrintMembers</c> declared on the type itself, which no hand-written struct has.
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod(
            "PrintMembers",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null;

    /// <summary>
    /// Reports any instance state beyond the single wrapped value: an extra declared instance field
    /// (the positional record struct declares exactly one, the backing field for <c>Value</c>) or a
    /// declared public instance property other than <c>Value</c>.
    /// </summary>
    private static IEnumerable<string> ExtraStateViolations(Type type)
    {
        var instanceFields = type
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .ToList();

        if (instanceFields.Count > 1)
        {
            yield return
                $"  - {type.FullName} declares {instanceFields.Count} instance fields; a strongly typed identifier holds only the wrapped value";
        }

        foreach (var property in type.DeclaredPublicProperties
            .Where(p => !string.Equals(p.Name, StronglyTypedIdValueProperty, StringComparison.Ordinal)))
        {
            yield return
                $"  - {type.FullName}.{property.Name} is extra state; a strongly typed identifier declares only Value";
        }
    }
}
