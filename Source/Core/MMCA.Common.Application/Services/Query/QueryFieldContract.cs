using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;

namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// The set of field names a client may name in a sort column, a dynamic filter key or a lookup
/// name property: the response contract's own property names, never the entity's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the entity is the wrong allow-list (SEC-Common-24, SEC-Common-25, SEC-ADC-09).</b> Sorting
/// and filtering used to accept any public property of <c>TEntity</c> that the client happened to
/// name. An entity carries columns the response never does: audit fields, credential material,
/// columns a mapper redacts per role. Ordering a public list by such a column leaks its total order,
/// and a <c>starts with</c> filter over one turns a list endpoint into a character-by-character
/// oracle. Resolving against the DTO contract instead means a caller can only order and filter by
/// data the response already carries.
/// </para>
/// <para>
/// <b>Server-authored map entries are the escape hatch.</b> An entry in the query service's
/// <c>DTOToEntityPropertyMap</c> is written by the server, so it may name a navigation path
/// (<c>"Category.Name"</c>) or a Dynamic LINQ expression that no DTO property matches. Those names
/// stay accepted: the contract is consulted only for a name the map does not cover.
/// </para>
/// <para>
/// <b>Dotted paths from the client are refused outright.</b> A dotted key that the map did not
/// author walks the object graph from a client string, which is both the navigation-column oracle
/// above and the join-explosion load of <see cref="MaxNavigationDepth"/> repeated segments
/// (SEC-Store-13). Only the map may author a path.
/// </para>
/// </remarks>
public sealed class QueryFieldContract
{
    /// <summary>
    /// Maximum number of segments a navigation path may carry, counting its root: <c>"Category"</c>
    /// is one, <c>"Category.Name"</c> is two, <c>"Session.Event.Name"</c> is three. A deeper path is
    /// refused wherever it came from, including a server-authored map entry, because each extra
    /// segment is another join in the emitted SQL.
    /// </summary>
    public const int MaxNavigationDepth = 3;

    private static readonly ConcurrentDictionary<Type, FrozenSet<string>> ContractNames = new();

    private readonly FrozenSet<string> _names;

    private QueryFieldContract(FrozenSet<string> names) => _names = names;

    /// <summary>
    /// The contract of <typeparamref name="TContract"/>: its public instance property names,
    /// compared case-insensitively (the same comparison the resolvers use).
    /// </summary>
    /// <typeparam name="TContract">The response contract type, normally the DTO.</typeparam>
    /// <returns>The contract for that type; reflected once per type and cached.</returns>
    public static QueryFieldContract For<TContract>() => For(typeof(TContract));

    /// <summary>
    /// The contract of <paramref name="contractType"/>: its public instance property names,
    /// compared case-insensitively.
    /// </summary>
    /// <param name="contractType">The response contract type, normally the DTO.</param>
    /// <returns>The contract for that type; reflected once per type and cached.</returns>
    public static QueryFieldContract For(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);

        return new QueryFieldContract(ContractNames.GetOrAdd(
            contractType,
            static type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A contract limited to an explicit, server-authored list of names. Use it where the DTO
    /// declares a field the response redacts per role, so the field is never orderable or
    /// filterable even though it is declared.
    /// </summary>
    /// <param name="names">The names a client may use.</param>
    /// <returns>A contract admitting exactly those names.</returns>
    public static QueryFieldContract ForNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new QueryFieldContract(names.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The names this contract admits.</summary>
    public IReadOnlyCollection<string> Names => _names;

    /// <summary>Whether the contract declares <paramref name="name"/>.</summary>
    /// <param name="name">The client-supplied field name.</param>
    /// <returns><see langword="true"/> when the response carries that field.</returns>
    public bool Contains(string? name) => name is not null && _names.Contains(name);

    /// <summary>
    /// Whether a client-supplied key is admissible without a server-authored map entry: it must
    /// name a contract field and must not be a navigation path.
    /// </summary>
    /// <param name="clientKey">The key exactly as the client sent it.</param>
    /// <returns><see langword="true"/> when the key may be resolved against the entity.</returns>
    public bool AllowsClientKey(string? clientKey) =>
        !string.IsNullOrWhiteSpace(clientKey)
        && !clientKey.Contains('.', StringComparison.Ordinal)
        && _names.Contains(clientKey);

    /// <summary>
    /// Whether a resolved entity path stays within <see cref="MaxNavigationDepth"/>. Applied to
    /// server-authored map entries as well as to client keys, so a pathological map entry cannot
    /// emit an unbounded join chain either.
    /// </summary>
    /// <param name="entityPath">The resolved entity property path.</param>
    /// <returns><see langword="true"/> when the path is within the depth ceiling.</returns>
    public static bool IsWithinNavigationDepth(string? entityPath)
    {
        if (string.IsNullOrEmpty(entityPath))
        {
            return true;
        }

        var segments = 1;
        foreach (var character in entityPath)
        {
            if (character == '.')
            {
                segments++;
            }

            if (segments > MaxNavigationDepth)
            {
                return false;
            }
        }

        return true;
    }
}
