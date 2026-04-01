using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Notifications;
using MMCA.Common.Application.Notifications.PushNotifications.DTOs;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.GetHistory;
using MMCA.Common.Application.Notifications.PushNotifications.UseCases.Send;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetInbox;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.GetUnreadCount;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkAllRead;
using MMCA.Common.Application.Notifications.UserNotifications.UseCases.MarkRead;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Domain.Notifications.PushNotifications;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using MMCA.Common.Shared.Notifications.PushNotifications;
using MMCA.Common.Shared.Notifications.UserNotifications;

namespace MMCA.Common.Application.Tests.Notifications;

public sealed class NotificationDependencyInjectionTests
{
    [Fact]
    public void AddNotificationApplicationServices_RegistersSendPushNotificationHandler()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ICommandHandler<SendPushNotificationCommand, Result<PushNotificationDTO>>));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<SendPushNotificationHandler>();
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersMarkNotificationReadHandler()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ICommandHandler<MarkNotificationReadCommand, Result>));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<MarkNotificationReadHandler>();
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersMarkAllNotificationsReadHandler()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(ICommandHandler<MarkAllNotificationsReadCommand, Result>));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<MarkAllNotificationsReadHandler>();
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersGetNotificationHistoryHandler()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IQueryHandler<GetNotificationHistoryQuery, Result<PagedCollectionResult<PushNotificationDTO>>>));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<GetNotificationHistoryHandler>();
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersGetMyNotificationsHandler()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IQueryHandler<GetMyNotificationsQuery, Result<PagedCollectionResult<UserNotificationDTO>>>));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<GetMyNotificationsHandler>();
    }

    [Fact]
    public void AddNotificationApplicationServices_RegistersGetUnreadNotificationCountHandler()
    {
        var services = new ServiceCollection();
        services.AddNotificationApplicationServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IQueryHandler<GetUnreadNotificationCountQuery, Result<int>>));

        descriptor.Should().NotBeNull();
        descriptor!.ImplementationType.Should().Be<GetUnreadNotificationCountHandler>();
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
