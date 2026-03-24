using System.Collections.Frozen;
using System.Linq.Expressions;

namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// Immutable parameter object encapsulating all query pipeline inputs: criteria,
/// dynamic filters, sorting, pagination, field projection, and navigation options.
/// </summary>
/// <typeparam name="TEntity">The entity type being queried.</typeparam>
public sealed record EntityQueryParameters<TEntity>
{
    /// <summary>Specification-based criteria expression (e.g. authorization filters).</summary>
    public Expression<Func<TEntity, bool>>? Criteria { get; init; }

    /// <summary>Dynamic user-supplied filters as property name to (operator, value) pairs.</summary>
    public Dictionary<string, (string Operator, string Value)>? Filters { get; init; }

    /// <summary>The property name to sort by.</summary>
    public string? SortColumn { get; init; }

    /// <summary>Sort direction: "asc" or "desc".</summary>
    public string? SortDirection { get; init; }

    /// <summary>Comma-separated list of fields to include in the projection.</summary>
    public string? Fields { get; init; }

    /// <summary>1-based page number for pagination.</summary>
    public int? PageNumber { get; init; }

    /// <summary>Number of items per page.</summary>
    public int? PageSize { get; init; }

    /// <summary>Whether FK reference navigations were requested.</summary>
    public bool IncludeFKs { get; init; }

    /// <summary>Whether child collection navigations were requested.</summary>
    public bool IncludeChildren { get; init; }

    /// <summary>
    /// Maps DTO property names to entity property paths, enabling filtering/sorting
    /// on DTO names that differ from entity properties (e.g. "CategoryName" -> "Category.Name").
    /// </summary>
    public IReadOnlyDictionary<string, string> DTOToEntityPropertyMap { get; init; } = FrozenDictionary<string, string>.Empty;
}
