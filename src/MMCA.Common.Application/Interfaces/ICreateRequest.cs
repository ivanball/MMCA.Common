namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Marker interface for create request DTOs. Used as a generic constraint by
/// <see cref="IEntityRequestMapper{TEntity,TCreateRequest,TIdentifierType}"/> to
/// ensure type safety in request-to-entity mapping.
/// </summary>
public interface ICreateRequest
{
}
