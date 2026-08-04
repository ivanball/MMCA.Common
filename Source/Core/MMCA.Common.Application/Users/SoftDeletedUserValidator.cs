using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Domain.Entities;

namespace MMCA.Common.Application.Users;

/// <summary>
/// Identity-module implementation of <see cref="ISoftDeletedUserValidator"/> (BR-133): checks whether
/// a user row exists AND is soft-deleted, in a single query that bypasses the global soft-delete
/// query filter.
/// </summary>
/// <remarks>
/// Closed over the app's <c>User</c> aggregate at registration
/// (<c>services.TryAddScoped&lt;ISoftDeletedUserValidator, SoftDeletedUserValidator&lt;User&gt;&gt;()</c>),
/// so no per-app subclass is needed. The predicate is expressed against <typeparamref name="TUser"/>
/// and closes over the concrete entity type at run time, so EF translation is what it was before the
/// hoist.
/// </remarks>
/// <typeparam name="TUser">The app's <c>User</c> aggregate.</typeparam>
public sealed class SoftDeletedUserValidator<TUser>(IUnitOfWork unitOfWork) : ISoftDeletedUserValidator
    where TUser : AuditableAggregateRootEntity<UserIdentifierType>
{
    /// <inheritdoc />
    public async Task<bool> IsUserSoftDeletedAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.GetRepository<TUser, UserIdentifierType>();

        // Single query: check if user exists AND is soft-deleted, bypassing the global query filter.
        return await repository.ExistsAsync(
            u => u.Id == userId && u.IsDeleted,
            ignoreQueryFilters: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
