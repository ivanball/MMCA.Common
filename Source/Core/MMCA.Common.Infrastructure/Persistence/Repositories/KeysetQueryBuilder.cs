using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using MMCA.Common.Domain.Interfaces;

namespace MMCA.Common.Infrastructure.Persistence.Repositories;

/// <summary>
/// Builds the ordering and the seek predicate of a keyset ("seek") page.
/// <para>
/// A keyset page is ordered by <c>(sortKey, Id)</c> and fetched with a composite comparison against
/// the last row of the previous page, so the database seeks straight to the boundary instead of
/// counting past every skipped row. The tie-break on <c>Id</c> is what makes the order total: without
/// it two rows sharing a sort value can swap places between pages and be returned twice or never.
/// </para>
/// <para>
/// Exactly one sort key is supported, by design. Multi-key keyset paging needs a comparison whose
/// size grows quadratically with the key count, and every provider translates it differently.
/// </para>
/// </summary>
internal static class KeysetQueryBuilder
{
    /// <summary>
    /// Resolves the sort property named by a keyset request, or <see langword="null"/> when the page
    /// is keyed by <c>Id</c> alone.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="sortColumn">The requested sort column, or <see langword="null"/>.</param>
    /// <param name="property">The resolved property when the method returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="false"/> when <paramref name="sortColumn"/> names something that is not a
    /// public instance property of the entity: the caller turns that into a validation failure.
    /// </returns>
    internal static bool TryResolveSortProperty<TEntity>(string? sortColumn, out PropertyInfo? property)
    {
        property = null;

        if (string.IsNullOrWhiteSpace(sortColumn))
            return true;

        property = typeof(TEntity).GetProperty(
            sortColumn,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        return property is not null;
    }

    /// <summary>
    /// Orders a queryable by <c>(sortKey, Id)</c>, or by <c>Id</c> alone when there is no sort key.
    /// The identifier tie-break always ascends, which is what the seek predicate assumes.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
    /// <param name="query">The queryable to order.</param>
    /// <param name="sortProperty">The sort property, or <see langword="null"/> for id-only ordering.</param>
    /// <param name="descending">Whether the sort key (or the id, when there is no sort key) descends.</param>
    /// <returns>The ordered queryable.</returns>
    internal static IQueryable<TEntity> ApplyOrdering<TEntity, TIdentifierType>(
        IQueryable<TEntity> query,
        PropertyInfo? sortProperty,
        bool descending)
        where TEntity : class, IBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var idSelector = Expression.Lambda(Expression.Property(parameter, nameof(IBaseEntity<>.Id)), parameter);

        if (sortProperty is null)
            return SpecificationEvaluator.ApplyOrderingStep(query, idSelector, descending, isFirst: true);

        var sortSelector = Expression.Lambda(Expression.Property(parameter, sortProperty), parameter);
        query = SpecificationEvaluator.ApplyOrderingStep(query, sortSelector, descending, isFirst: true);
        return SpecificationEvaluator.ApplyOrderingStep(query, idSelector, descending: false, isFirst: false);
    }

    /// <summary>
    /// Builds the seek predicate that selects the rows strictly after the cursor's boundary row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With no sort key the predicate is simply <c>Id &gt; lastId</c> (or <c>&lt;</c> when the page
    /// descends). With a sort key it is the classic composite comparison
    /// <c>sort &gt; v OR (sort == v AND Id &gt; lastId)</c>, reversed to <c>&lt;</c> on the sort half
    /// for a descending page while the identifier half always ascends.
    /// </para>
    /// <para>
    /// A boundary row whose sort key is <see langword="null"/> is handled explicitly, because SQL
    /// comparisons against NULL are unknown and would silently drop rows. Ascending, nulls sort
    /// first, so everything after the boundary is either a later null or any non-null value; that is
    /// what the predicate says. Descending, nulls sort last, so the remaining rows are the later
    /// nulls only.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TIdentifierType">The entity's identifier type.</typeparam>
    /// <param name="sortProperty">The sort property, or <see langword="null"/> for id-only paging.</param>
    /// <param name="sortValue">The boundary row's sort value (already converted), or <see langword="null"/>.</param>
    /// <param name="lastId">The boundary row's identifier.</param>
    /// <param name="descending">Whether the page descends.</param>
    /// <returns>The seek predicate.</returns>
    internal static Expression<Func<TEntity, bool>> BuildSeekPredicate<TEntity, TIdentifierType>(
        PropertyInfo? sortProperty,
        object? sortValue,
        TIdentifierType lastId,
        bool descending)
        where TEntity : class, IBaseEntity<TIdentifierType>
        where TIdentifierType : notnull
    {
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var idAccess = Expression.Property(parameter, nameof(IBaseEntity<>.Id));
        var idConstant = Expression.Constant(lastId, typeof(TIdentifierType));

        if (sortProperty is null)
        {
            // Id-only paging: the identifier follows the requested direction, since it IS the sort key.
            var idOnly = Compare(idAccess, idConstant, greaterThan: !descending);
            return Expression.Lambda<Func<TEntity, bool>>(idOnly, parameter);
        }

        var sortAccess = Expression.Property(parameter, sortProperty);
        var idTieBreak = Compare(idAccess, idConstant, greaterThan: true);
        var isNullable = !sortProperty.PropertyType.IsValueType
            || Nullable.GetUnderlyingType(sortProperty.PropertyType) is not null;

        if (sortValue is null)
        {
            if (!isNullable)
            {
                // A non-nullable key can never have produced a null boundary; degrade to the id seek.
                return Expression.Lambda<Func<TEntity, bool>>(idTieBreak, parameter);
            }

            var isNull = Expression.Equal(sortAccess, Expression.Constant(null, sortProperty.PropertyType));

            Expression nullBoundary = descending
                ? Expression.AndAlso(isNull, idTieBreak)
                : Expression.OrElse(
                    Expression.NotEqual(sortAccess, Expression.Constant(null, sortProperty.PropertyType)),
                    Expression.AndAlso(isNull, idTieBreak));

            return Expression.Lambda<Func<TEntity, bool>>(nullBoundary, parameter);
        }

        var sortConstant = Expression.Constant(sortValue, sortProperty.PropertyType);
        Expression body = Expression.OrElse(
            Compare(sortAccess, sortConstant, greaterThan: !descending),
            Expression.AndAlso(Expression.Equal(sortAccess, sortConstant), idTieBreak));

        if (isNullable && descending)
        {
            // Descending puts nulls last, and "null < v" is unknown, so they need saying explicitly.
            body = Expression.OrElse(
                Expression.Equal(sortAccess, Expression.Constant(null, sortProperty.PropertyType)),
                body);
        }

        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    /// <summary>
    /// Renders a value for a cursor with invariant culture, using the round-trip format for the
    /// date and time types so a cursor never loses sub-second precision and re-seeks onto the wrong
    /// row.
    /// </summary>
    /// <param name="value">The value to render.</param>
    /// <returns>The rendered value, or <see langword="null"/> when the value is null.</returns>
    internal static string? ToInvariantString(object? value) => value switch
    {
        null => null,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Parses a cursor segment back into the target type with invariant culture.
    /// </summary>
    /// <param name="targetType">The type to convert to.</param>
    /// <param name="text">The rendered value.</param>
    /// <param name="value">The converted value when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the segment is not a valid value of that type.</returns>
    internal static bool TryFromInvariantString(Type targetType, string text, out object? value)
    {
        value = null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying == typeof(string))
        {
            value = text;
            return true;
        }

        var converter = TypeDescriptor.GetConverter(underlying);
        if (!converter.CanConvertFrom(typeof(string)))
            return false;

        try
        {
            value = converter.ConvertFromInvariantString(text);
            return value is not null;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a greater-than / less-than comparison that the providers can translate.
    /// </summary>
    /// <remarks>
    /// <see cref="Expression.GreaterThan(Expression, Expression)"/> only exists for types with the
    /// operator, which excludes <see cref="string"/>. Strings therefore compare through
    /// <see cref="string.Compare(string, string)"/>, which EF Core translates to a native
    /// <c>&gt;</c> / <c>&lt;</c>. Anything else with no operator but an
    /// <see cref="IComparable{T}"/> implementation (a <see cref="Guid"/> key, for instance) compares
    /// through <c>CompareTo</c>, whose translation is provider-specific.
    /// </remarks>
    private static BinaryExpression Compare(Expression left, Expression right, bool greaterThan)
    {
        var type = Nullable.GetUnderlyingType(left.Type) ?? left.Type;

        if (type == typeof(string))
        {
            var compare = Expression.Call(StringCompareMethod, left, right);
            return greaterThan
                ? Expression.GreaterThan(compare, ZeroConstant)
                : Expression.LessThan(compare, ZeroConstant);
        }

        if (SupportsRelationalOperator(type))
        {
            return greaterThan
                ? Expression.GreaterThan(left, right)
                : Expression.LessThan(left, right);
        }

        var comparableInterface = typeof(IComparable<>).MakeGenericType(type);
        if (!comparableInterface.IsAssignableFrom(type))
        {
            throw new NotSupportedException(
                $"Keyset paging cannot order by '{left.Type.Name}': the type supports neither a relational operator nor IComparable<T>.");
        }

        var compareTo = Expression.Call(
            Expression.Convert(left, type),
            comparableInterface.GetMethod(nameof(IComparable<>.CompareTo))!,
            Expression.Convert(right, type));

        return greaterThan
            ? Expression.GreaterThan(compareTo, ZeroConstant)
            : Expression.LessThan(compareTo, ZeroConstant);
    }

    private static bool SupportsRelationalOperator(Type type)
        => type.IsPrimitive
            || type.IsEnum
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan)
            || type.GetMethod("op_GreaterThan", BindingFlags.Public | BindingFlags.Static) is not null;

    private static readonly MethodInfo StringCompareMethod =
        typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;

    private static readonly ConstantExpression ZeroConstant = Expression.Constant(0);
}
