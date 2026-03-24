using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Application.Services.Query;

/// <summary>
/// Discovers navigation properties on entity types and classifies them as supported
/// (EF Core .Include()) or unsupported (manual loading) based on data source compatibility.
/// </summary>
public interface INavigationMetadataProvider
{
    /// <summary>
    /// Builds navigation metadata for the given entity type, optionally including
    /// FK references and/or child collections.
    /// </summary>
    /// <typeparam name="TEntity">The entity type to inspect.</typeparam>
    /// <param name="includeFKs">Whether to include FK reference navigations.</param>
    /// <param name="includeChildren">Whether to include child collection navigations.</param>
    /// <returns>Metadata categorizing each navigation as supported or unsupported.</returns>
    NavigationMetadata BuildIncludes<TEntity>(bool includeFKs, bool includeChildren);
}
