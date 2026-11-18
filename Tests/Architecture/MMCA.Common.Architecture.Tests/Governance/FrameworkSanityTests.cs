using MMCA.Common.Application.Interfaces.Infrastructure.Notifications;
using MMCA.Common.Application.Messaging;
using MMCA.Common.Infrastructure.Auth;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// Framework-specific placement sanity that the shared rule library does not generalize: MMCA.Common
/// defines the <c>IMessageBus</c> / <c>IJwksProvider</c> / <c>ILiveChannelPublisher</c> abstractions,
/// so their home layers are checked here directly. (The Grpc transport's layer rules live in
/// <c>LayerDependencyTestsBase</c>.)
/// </summary>
public sealed class FrameworkSanityTests
{
    private static Assembly Application => typeof(Common.Application.Services.DomainEventDispatcher).Assembly;

    private static Assembly Infrastructure => typeof(Common.Infrastructure.Persistence.DbContexts.ApplicationDbContext).Assembly;

    [Fact]
    public void IMessageBus_LivesInApplicationLayer() =>
        typeof(IMessageBus).Assembly.Should().BeSameAs(Application,
            "the message-bus abstraction must remain in Application so consumers depend on it through Application");

    [Fact]
    public void IJwksProvider_LivesInInfrastructureLayer() =>
        typeof(IJwksProvider).Assembly.Should().BeSameAs(Infrastructure,
            "the JWKS provider handles crypto/PEM material, so it belongs in Infrastructure");

    [Fact]
    public void ILiveChannelPublisher_LivesInApplicationLayer() =>
        typeof(ILiveChannelPublisher).Assembly.Should().BeSameAs(Application,
            "the live-channel publish abstraction must remain in Application (beside IPushNotificationSender) so application code stays transport-free");
}
