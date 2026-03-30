using MMCA.Common.Application.Interfaces;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Notifications.PushNotifications;
using Riok.Mapperly.Abstractions;

namespace MMCA.Common.Application.Notifications.PushNotifications.DTOs;

/// <summary>
/// Maps <see cref="PushNotification"/> domain entities to <see cref="PushNotificationDTO"/> objects.
/// </summary>
[Mapper]
public sealed partial class PushNotificationDTOMapper
    : IEntityDTOMapper<PushNotification, PushNotificationDTO, PushNotificationIdentifierType>
{
    /// <inheritdoc />
    [MapProperty(nameof(PushNotification.Status), nameof(PushNotificationDTO.Status), Use = nameof(MapStatusToString))]
    public partial PushNotificationDTO MapToDTO(PushNotification entity);

    /// <inheritdoc />
    public IReadOnlyCollection<PushNotificationDTO> MapToDTOs(IReadOnlyCollection<PushNotification> entityCollection)
    {
        ArgumentNullException.ThrowIfNull(entityCollection);
        return [.. entityCollection.Select(MapToDTO)];
    }

    private static string MapStatusToString(PushNotificationStatus status) => status.ToString();
}
