using System.Globalization;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Interfaces.Navigation;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Infrastructure.Persistence.Configuration.EntityTypeConfiguration;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.LoadTests.Support;

/// <summary>
/// The entity the paging scenarios read: an ordinary auditable aggregate (soft-delete filter, audit
/// columns and row version all apply) with one string column to filter on and one int column to sort
/// on. Seeded deterministically so every assertion can compute its expected rows:
/// <c>Category = "Category-{Id % 10}"</c>, <c>Quantity = Id % 1000</c>.
/// </summary>
public sealed class LoadItem : AuditableAggregateRootEntity<int>
{
    public const int CategoryCount = 10;

    public const int QuantityModulus = 1000;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public static LoadItem ForId(int id) => new()
    {
        Id = id,
        Name = string.Create(CultureInfo.InvariantCulture, $"Item {id:D6}"),
        Category = CategoryOf(id),
        Quantity = id % QuantityModulus,
    };

    public static string CategoryOf(int id) => string.Create(CultureInfo.InvariantCulture, $"Category-{id % CategoryCount}");
}

/// <summary>The list DTO; its property names are the query field contract (filter and sort columns).</summary>
public sealed record LoadItemDTO : IBaseDTO<int>
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public int Quantity { get; init; }
}

/// <summary>Plain SQLite configuration through the framework's base class; no hand-added indexes.</summary>
internal sealed class LoadItemConfiguration : EntityTypeConfigurationSqlite<LoadItem, int>;

/// <summary>The consumer-supplied mapper the query service's materialize-then-map path calls.</summary>
internal sealed class LoadItemMapper : IEntityDTOMapper<LoadItem, LoadItemDTO, int>
{
    public LoadItemDTO MapToDTO(LoadItem entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Category = entity.Category,
        Quantity = entity.Quantity,
    };
}

/// <summary><see cref="LoadItem"/> declares no navigations, so there is nothing to populate.</summary>
internal sealed class LoadItemNavigationPopulator : INavigationPopulator<LoadItem>
{
    public Task PopulateAsync(
        IReadOnlyCollection<LoadItem> entities,
        NavigationMetadata navigationMetadata,
        bool includeFKs,
        bool includeChildren,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
