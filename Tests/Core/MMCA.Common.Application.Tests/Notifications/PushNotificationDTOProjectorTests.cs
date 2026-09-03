using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Notifications.PushNotifications;

namespace MMCA.Common.Application.Tests.Notifications;

/// <summary>
/// Pins the contract that makes projection pushdown safe to switch on: the projector MUST produce
/// exactly what the instance mapper produces for the same row. The two paths are chosen by
/// configuration (whether a projector is registered), so a divergence would make the response depend
/// on a DI detail. The enum-to-string conversion is the one place the two are written differently:
/// the mapper calls a method, the projection inlines a translatable expression.
/// </summary>
public sealed class PushNotificationDTOProjectorTests
{
    private readonly PushNotificationDTOMapper _mapper = new();
    private readonly PushNotificationDTOProjector _sut = new();

    private static PushNotification Create(
        PushNotificationStatus status,
        string? scopeKey = null,
        int id = 7)
    {
        var notification = PushNotification.Create(
            "Title",
            "Body",
            sentByUserId: 3,
            recipientCount: 12,
            scopeKey: scopeKey).Value!;

        typeof(PushNotification).GetProperty(nameof(PushNotification.Id))!.SetValue(notification, id);

        switch (status)
        {
            case PushNotificationStatus.Sent:
                notification.MarkAsSent();
                break;
            case PushNotificationStatus.Failed:
                notification.MarkAsFailed();
                break;
            case PushNotificationStatus.Pending:
            default:
                break;
        }

        return notification;
    }

    [Theory]
    [InlineData(PushNotificationStatus.Pending)]
    [InlineData(PushNotificationStatus.Sent)]
    [InlineData(PushNotificationStatus.Failed)]
    public void ProjectTo_ProducesExactlyWhatTheMapperProduces(PushNotificationStatus status)
    {
        var entity = Create(status, scopeKey: "event:2");

        var projected = _sut.ProjectTo(new[] { entity }.AsQueryable()).Single();
        var mapped = _mapper.MapToDTO(entity);

        projected.Should().BeEquivalentTo(mapped);
    }

    [Fact]
    public void ProjectTo_MatchesTheMapperForAnUnscopedNotification()
    {
        var entity = Create(PushNotificationStatus.Pending);

        var projected = _sut.ProjectTo(new[] { entity }.AsQueryable()).Single();

        projected.Should().BeEquivalentTo(_mapper.MapToDTO(entity));
        projected.ScopeKey.Should().BeNull();
    }

    [Fact]
    public void ProjectTo_RendersTheStatusAsItsEnumName()
    {
        var entity = Create(PushNotificationStatus.Sent);

        _sut.ProjectTo(new[] { entity }.AsQueryable()).Single().Status
            .Should().Be(nameof(PushNotificationStatus.Sent));
    }

    [Fact]
    public void ProjectTo_MatchesTheMapperOverAWholeCollection()
    {
        PushNotification[] entities =
        [
            Create(PushNotificationStatus.Pending, id: 1),
            Create(PushNotificationStatus.Sent, "event:1", id: 2),
            Create(PushNotificationStatus.Failed, "event:2", id: 3),
        ];

        var projected = _sut.ProjectTo(entities.AsQueryable()).ToList();

        projected.Should().BeEquivalentTo(_mapper.MapToDTOs(entities));
    }

    [Fact]
    public void ProjectTo_ReturnsAQueryable_NotAMaterializedList()
    {
        var source = new[] { Create(PushNotificationStatus.Pending) }.AsQueryable();

        var projected = _sut.ProjectTo(source);

        projected.Expression.ToString().Should().Contain(
            "Select",
            "the projection must stay composable so the provider translates it");
    }

    [Fact]
    public void ProjectTo_WithNullSource_Throws()
    {
        var act = () => _sut.ProjectTo(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TheProjector_IsAnEntityDTOProjector() =>
        _sut.Should().BeAssignableTo<
            IEntityDTOProjector<PushNotification, PushNotificationDTO, PushNotificationIdentifierType>>();
}
