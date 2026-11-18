using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Interfaces.Navigation;
using MMCA.Common.Application.Notifications;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.Shared.Notifications.UserNotifications;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class NotificationDependencyInjectionTests
{
    [Theory]
    [InlineData(typeof(ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>>), typeof(SendPushNotificationHandler))]
    [InlineData(typeof(ICommandHandler<MarkNotificationReadCommand, Result>), typeof(MarkNotificationReadHandler))]
    [InlineData(typeof(ICommandHandler<MarkAllNotificationsReadCommand, Result>), typeof(MarkAllNotificationsReadHandler))]
    [InlineData(typeof(IQueryHandler<GetNotificationHistoryQuery, Result<PagedCollectionResult<PushNotificationDTO>>>), typeof(GetNotificationHistoryHandler))]
    [InlineData(typeof(IQueryHandler<GetMyNotificationsQuery, Result<PagedCollectionResult<UserNotificationDTO>>>), typeof(GetMyNotificationsHandler))]
    [InlineData(typeof(IQueryHandler<GetUnreadNotificationCountQuery, Result<int>>), typeof(GetUnreadNotificationCountHandler))]
    public void AddNotificationApplicationServices_RegistersTheHandler(Type serviceType, Type expectedImplementation)
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be(expectedImplementation);
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersDTOMapper()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(PushNotificationDTOMapper));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersDefaultRecipientProvider()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(INotificationRecipientProvider));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<NullNotificationRecipientProvider>();
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersNavigationPopulator()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(INavigationPopulator<PushNotification>));

        descriptor.Should().NotBeNull();
    }

    [Fact]
    public void AddNotificationApplicationServices_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddNotificationApplicationServices();

        result.Should().BeSameAs(services);
    }
}
