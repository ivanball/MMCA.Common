using AwesomeAssertions;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class PushNotificationDTOMapperTests
{
    private readonly PushNotificationDTOMapper _mapper = new();

    // ── MapToDTO ──
    [Fact]
    public void MapToDTO_WithValidEntity_MapsTitleAndBody()
    {
        PushNotification entity = CreateEntity("Alert", "Something happened");

        PushNotificationDTO dto = _mapper.MapToDTO(entity);

        dto.Title.Should().Be("Alert");
        dto.Body.Should().Be("Something happened");
    }

    [Fact]
    public void MapToDTO_WithValidEntity_MapsSentByUserIdAndRecipientCount()
    {
        PushNotification entity = CreateEntity("Title", "Body", sentByUserId: 5, recipientCount: 10);

        PushNotificationDTO dto = _mapper.MapToDTO(entity);

        dto.SentByUserId.Should().Be(5);
        dto.RecipientCount.Should().Be(10);
    }

    [Fact]
    public void MapToDTO_WithPendingEntity_MapsStatusAsString()
    {
        PushNotification entity = CreateEntity("Title", "Body");

        PushNotificationDTO dto = _mapper.MapToDTO(entity);

        dto.Status.Should().Be(nameof(PushNotificationStatus.Pending));
    }

    [Fact]
    public void MapToDTO_WithSentEntity_MapsStatusAsSent()
    {
        PushNotification entity = CreateEntity("Title", "Body");
        entity.MarkAsSent();

        PushNotificationDTO dto = _mapper.MapToDTO(entity);

        dto.Status.Should().Be(nameof(PushNotificationStatus.Sent));
    }

    [Fact]
    public void MapToDTO_WithFailedEntity_MapsStatusAsFailed()
    {
        PushNotification entity = CreateEntity("Title", "Body");
        entity.MarkAsFailed();

        PushNotificationDTO dto = _mapper.MapToDTO(entity);

        dto.Status.Should().Be(nameof(PushNotificationStatus.Failed));
    }

    // ── MapToDTOs ──
    [Fact]
    public void MapToDTOs_WithCollection_MapsAllEntities()
    {
        List<PushNotification> entities =
        [
            CreateEntity("First", "Body 1"),
            CreateEntity("Second", "Body 2"),
            CreateEntity("Third", "Body 3"),
        ];

        IReadOnlyCollection<PushNotificationDTO> dtos = _mapper.MapToDTOs(entities);

        dtos.Should().HaveCount(3);
        dtos.Select(d => d.Title).Should().ContainInOrder("First", "Second", "Third");
    }

    [Fact]
    public void MapToDTOs_WithEmptyCollection_ReturnsEmpty()
    {
        IReadOnlyCollection<PushNotificationDTO> dtos = _mapper.MapToDTOs([]);

        dtos.Should().BeEmpty();
    }

    [Fact]
    public void MapToDTOs_WithNull_ThrowsArgumentNullException()
    {
        Action act = () => _mapper.MapToDTOs(null!);

        act.Should().ThrowExactly<ArgumentNullException>();
    }

    // ── Helpers ──
    private static PushNotification CreateEntity(
        string title = "Title",
        string body = "Body",
        UserIdentifierType sentByUserId = 1,
        int recipientCount = 5)
    {
        Result<PushNotification> result = PushNotification.Create(title, body, sentByUserId, recipientCount);
        return result.Value!;
    }
}
