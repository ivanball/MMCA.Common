using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Reflection;
using MMCA.Common.Shared.ValueObjects.Contact;

namespace MMCA.Common.Application.Services.Filtering.ValueObjects;

/// <summary>
/// Filter strategy for the Common <see cref="Email"/> value object: the address filters like the
/// string it is (CONTAINS, NOT CONTAINS, EQUALS, NOT EQUALS, STARTS WITH, ENDS WITH, IS EMPTY,
/// IS NOT EMPTY), so a grid's Email column works on an entity that stores the value object.
/// <para>
/// <b>Two translations of "the address text".</b> The value object is mapped with a value converter
/// (<c>EmailValueConverter</c>, <c>HasConversion</c> onto one string column), and EF Core cannot
/// translate member access into a converted value, so <c>e.Email.Value.Contains(x)</c> fails at query
/// time. Against a database the predicate therefore reads the column through
/// <c>(string)(object)e.Email</c>: EF Core strips the two casts and compares the provider value, the
/// stored string itself. LINQ to Objects cannot do that cast (the CLR object is an <see cref="Email"/>,
/// not a string), so an in-memory query reads <see cref="Email.Value"/> behind a null guard instead.
/// The choice is made on the query's provider, never on configuration.
/// </para>
/// <para>
/// The needle is folded to lower case, the form <see cref="Email.Create"/> stores, so a match does not
/// depend on the database collation, and it travels as a captured value so EF Core parameterizes it.
/// </para>
/// </summary>
internal sealed class EmailFilterStrategy : IFilterStrategy
{
    private static readonly MethodInfo ContainsMethod = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StartsWithMethod = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo EndsWithMethod = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo IsNullOrEmptyMethod = typeof(string).GetMethod(nameof(string.IsNullOrEmpty), [typeof(string)])!;

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedOperators { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "CONTAINS", "NOT CONTAINS", "EQUALS", "NOT EQUALS",
        "STARTS WITH", "ENDS WITH", "IS EMPTY", "IS NOT EMPTY",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <inheritdoc />
    public IQueryable<T> Apply<T>(IQueryable<T> query, string property, string op, string value)
    {
        if (!SupportedOperators.Contains(op))
        {
            return query;
        }

        var parameter = Expression.Parameter(typeof(T), "e");
        var email = BuildMemberAccess(parameter, property);
        if (email is null)
        {
            return query;
        }

        var inMemory = query.Provider is EnumerableQuery;
        var address = inMemory
            ? Expression.Property(email, nameof(Email.Value))
            : (Expression)Expression.Convert(Expression.Convert(email, typeof(object)), typeof(string));

        var predicate = op switch
        {
            "IS EMPTY" => BuildIsEmpty(email, address, inMemory),
            "IS NOT EMPTY" => Expression.Not(BuildIsEmpty(email, address, inMemory)),
            "NOT CONTAINS" => Expression.Not(BuildComparison("CONTAINS", email, address, value, inMemory)),
            "NOT EQUALS" => Expression.Not(BuildComparison("EQUALS", email, address, value, inMemory)),
            _ => BuildComparison(op, email, address, value, inMemory),
        };

        return query.Where(Expression.Lambda<Func<T, bool>>(predicate, parameter));
    }

    /// <summary>Walks a dotted property path; <see langword="null"/> when a segment does not resolve.</summary>
    private static MemberExpression? BuildMemberAccess(ParameterExpression parameter, string path)
    {
        Expression current = parameter;
        foreach (var segment in path.Split('.'))
        {
            var propertyInfo = current.Type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (propertyInfo is null)
            {
                return null;
            }

            current = Expression.Property(current, propertyInfo);
        }

        return current.Type == typeof(Email) ? (MemberExpression)current : null;
    }

    /// <summary>
    /// A database answers IS EMPTY over the column (null or empty string); in memory the value object
    /// itself is the null case, and its Value is never null once it exists.
    /// </summary>
    private static Expression BuildIsEmpty(Expression email, Expression address, bool inMemory) =>
        inMemory
            ? Expression.OrElse(
                Expression.Equal(email, Expression.Constant(null, typeof(Email))),
                Expression.Call(IsNullOrEmptyMethod, address))
            : Expression.Call(IsNullOrEmptyMethod, address);

    private static Expression BuildComparison(string op, Expression email, Expression address, string value, bool inMemory)
    {
        var needle = Expression.Property(Expression.Constant(new Needle(ToStoredCase(value.Trim()))), nameof(Needle.Value));

        Expression comparison = op switch
        {
            "CONTAINS" => Expression.Call(address, ContainsMethod, needle),
            "STARTS WITH" => Expression.Call(address, StartsWithMethod, needle),
            "ENDS WITH" => Expression.Call(address, EndsWithMethod, needle),
            _ => Expression.Equal(address, needle),
        };

        // In memory a null Email has no Value to read, so the comparison sits behind a null guard; a
        // database compares the column and treats null the way SQL does.
        return inMemory
            ? Expression.AndAlso(Expression.NotEqual(email, Expression.Constant(null, typeof(Email))), comparison)
            : comparison;
    }

    /// <summary>
    /// Folds the needle into the lower case <see cref="Email.Create"/> stores, character by character
    /// with the invariant culture, so a match is case-insensitive on every database collation.
    /// </summary>
    private static string ToStoredCase(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.ToLowerInvariant(source[i]);
            }
        });

    /// <summary>
    /// Holds the filter value so the expression reads it through a member access, which EF Core turns
    /// into a query parameter instead of inlining a literal.
    /// </summary>
    /// <param name="Value">The folded filter value.</param>
    private sealed record Needle(string Value);
}
