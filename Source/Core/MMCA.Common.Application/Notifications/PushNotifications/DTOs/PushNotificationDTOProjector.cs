using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Notifications.PushNotifications;
using Riok.Mapperly.Abstractions;

namespace MMCA.Common.Application.Notifications.PushNotifications.DTOs;

/// <summary>
/// Mapperly-generated server-side projection from <see cref="PushNotification"/> to
/// <see cref="PushNotificationDTO"/>. It is the framework's worked example of a projection: the
/// query returns the DTO's columns instead of whole notification rows that are mapped afterwards.
/// </summary>
/// <remarks>
/// The entity's <c>Status</c> is an enum and the DTO's is a string. The instance mapper renders it
/// with a custom method (<c>Use = nameof(MapStatusToString)</c>), which a projection cannot call: a
/// projection is an expression tree the database has to translate. Mapperly's enum-to-string mapping
/// is expressible, so it is inlined here as a conditional over the known members (a SQL
/// <c>CASE</c>), producing the same strings as <c>PushNotificationStatus.ToString()</c>. The
/// equivalence with the instance mapper is pinned by a test.
/// </remarks>
[Mapper]
internal static partial class PushNotificationDTOProjection
{
    /// <summary>Projects a notification queryable to a DTO queryable, server-side.</summary>
    /// <param name="source">The entity queryable.</param>
    /// <returns>The projected DTO queryable.</returns>
    internal static partial IQueryable<PushNotificationDTO> ProjectToDTO(IQueryable<PushNotification> source);
}

/// <summary>
/// The <see cref="IEntityDTOProjector{TEntity, TEntityDTO, TIdentifierType}"/> wrapper around
/// <see cref="PushNotificationDTOProjection"/>, registered by
/// <c>AddNotificationApplicationServices</c> so notification list reads use projection pushdown.
/// </summary>
public sealed class PushNotificationDTOProjector
    : IEntityDTOProjector<PushNotification, PushNotificationDTO, PushNotificationIdentifierType>
{
    /// <inheritdoc />
    public IQueryable<PushNotificationDTO> ProjectTo(IQueryable<PushNotification> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return PushNotificationDTOProjection.ProjectToDTO(source);
    }
}
