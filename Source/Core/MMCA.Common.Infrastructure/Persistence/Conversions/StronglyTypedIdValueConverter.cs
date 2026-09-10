using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MMCA.Common.Shared.Identifiers;

namespace MMCA.Common.Infrastructure.Persistence.Conversions;

/// <summary>
/// EF Core value converter that stores a strongly typed identifier as the primitive it wraps and
/// rebuilds the wrapper on read, so the backing column is the same <see langword="int"/>, <c>bigint</c>,
/// <c>uniqueidentifier</c> or <c>nvarchar</c> it was before the wrapper existed. Adopting a wrapper
/// is therefore not a schema change and not a migration.
/// <para>
/// A host does not apply this converter property by property:
/// <c>services.AddStronglyTypedIds(typeof(OrderId).Assembly)</c> registers every declared identifier
/// as a pre-convention type mapping on <c>ApplicationDbContext.ConfigureConventions</c>, which
/// reaches every property of that type on every engine. Pre-convention is the load-bearing detail:
/// the provider CLR type is known before EF picks a value-generation strategy, so a wrapped
/// <see langword="int"/> key still maps to a SQL Server IDENTITY column and a wrapped <c>Guid</c> key still
/// takes its client-side generator.
/// </para>
/// <para>
/// Both legs go through a static helper rather than an inline lambda body, because a static abstract
/// interface member (<c>TSelf.From</c>) cannot be invoked inside an expression tree.
/// </para>
/// </summary>
/// <typeparam name="TSelf">The identifier type.</typeparam>
/// <typeparam name="TValue">The wrapped primitive, which becomes the column type.</typeparam>
public sealed class StronglyTypedIdValueConverter<TSelf, TValue> : ValueConverter<TSelf, TValue>
    where TSelf : struct, IStronglyTypedId<TSelf, TValue>
    where TValue : notnull, IEquatable<TValue>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StronglyTypedIdValueConverter{TSelf, TValue}"/> class.
    /// </summary>
    public StronglyTypedIdValueConverter()
        : base(id => Unwrap(id), value => Wrap(value))
    {
    }

    private static TValue Unwrap(TSelf identifier) => identifier.Value;

    private static TSelf Wrap(TValue value) => TSelf.From(value);
}

/// <summary>
/// Nullable counterpart of <see cref="StronglyTypedIdValueConverter{TSelf, TValue}"/>, for a value
/// type primitive. EF Core already lifts a non-nullable struct converter over
/// <see cref="Nullable{T}"/> on its own, so this exists for the explicit
/// <c>HasConversion(new NullableStronglyTypedIdValueConverter&lt;OrderId, int&gt;())</c> call site
/// rather than because the automatic path is missing. Both legs pass <see langword="null"/>
/// straight through, so an unset optional identifier stays a NULL column rather than collapsing
/// onto the wrapper's default.
/// </summary>
/// <typeparam name="TSelf">The identifier type.</typeparam>
/// <typeparam name="TValue">The wrapped primitive, which must itself be a value type.</typeparam>
public sealed class NullableStronglyTypedIdValueConverter<TSelf, TValue> : ValueConverter<TSelf?, TValue?>
    where TSelf : struct, IStronglyTypedId<TSelf, TValue>
    where TValue : struct, IEquatable<TValue>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NullableStronglyTypedIdValueConverter{TSelf, TValue}"/> class.
    /// </summary>
    public NullableStronglyTypedIdValueConverter()
        : base(id => Unwrap(id), value => Wrap(value))
    {
    }

    private static TValue? Unwrap(TSelf? identifier) => identifier?.Value;

    private static TSelf? Wrap(TValue? value) => value is null ? null : TSelf.From(value.Value);
}

/// <summary>
/// Snapshot and equality semantics for a strongly typed identifier. A record struct is immutable and
/// already carries structural equality, so the snapshot leg is the identity function and equality
/// runs through <see cref="EqualityComparer{T}.Default"/>, which resolves to the compiler-generated
/// <see cref="IEquatable{T}"/> implementation rather than boxing.
/// </summary>
/// <typeparam name="TSelf">The identifier type.</typeparam>
public sealed class StronglyTypedIdValueComparer<TSelf> : ValueComparer<TSelf>
    where TSelf : struct
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StronglyTypedIdValueComparer{TSelf}"/> class.
    /// </summary>
    public StronglyTypedIdValueComparer()
        : base(
            (left, right) => EqualityComparer<TSelf>.Default.Equals(left, right),
            identifier => identifier.GetHashCode(),
            identifier => identifier)
    {
    }
}
