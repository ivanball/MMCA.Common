using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.MachineClients;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Domain.Auth.MachineClients;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.MachineClients;

/// <summary>
/// The revocation marker covers the host's access-token lifetime: a host that raises it past the
/// default gets a marker that outlives every token the raised lifetime could have minted (ADR-047).
/// </summary>
public sealed class MachineClientServiceRevocationMarkerTests
{
    private const UserIdentifierType ServiceAccountId = 42;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero));
    private readonly Mock<IMachineClientStore> _store = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ITokenService> _tokens = new();

    [Fact]
    public async Task RevokeAsync_OnAHostWithARaisedAccessTokenLifetime_WritesAMarkerCoveringIt()
    {
        _tokens.SetupGet(t => t.AccessTokenLifetime).Returns(TimeSpan.FromMinutes(60));
        var client = await SeedClientAsync();

        await CreateSut().RevokeAsync(client.Id);

        _cache.Verify(
            c => c.SetAsync(SoftDeletedUserCache.KeyFor(ServiceAccountId), true, TimeSpan.FromMinutes(65), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RevokeAsync_OnAHostAtTheDefaultLifetime_KeepsTheTwentyMinuteMarker()
    {
        _tokens.SetupGet(t => t.AccessTokenLifetime).Returns(TimeSpan.FromMinutes(15));
        var client = await SeedClientAsync();

        await CreateSut().RevokeAsync(client.Id);

        _cache.Verify(
            c => c.SetAsync(SoftDeletedUserCache.KeyFor(ServiceAccountId), true, SoftDeletedUserCache.MarkerDuration, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private Task<MachineClient> SeedClientAsync()
    {
        var client = MachineClient.Create("client", ServiceAccountId, new byte[32], null, _clock.GetUtcNow().UtcDateTime).Value!;
        _store.Setup(s => s.FindAsync(client.Id, It.IsAny<CancellationToken>())).ReturnsAsync(client);
        return Task.FromResult(client);
    }

    private MachineClientService CreateSut() => new(
        _store.Object,
        Mock.Of<IServiceAccountProvisioner>(),
        _tokens.Object,
        Mock.Of<IPermissionRegistry>(),
        _cache.Object,
        Options.Create(new MachineClientSettings { Enabled = true }),
        _clock);
}
