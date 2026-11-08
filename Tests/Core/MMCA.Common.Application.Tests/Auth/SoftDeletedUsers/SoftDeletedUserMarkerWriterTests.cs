using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Auth;
using MMCA.Common.Application.Auth.SoftDeletedUsers;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using Moq;

namespace MMCA.Common.Application.Tests.Auth.SoftDeletedUsers;

/// <summary>
/// The writer sizes the marker from the host's access-token lifetime, and its registration asks for
/// the token service only when the writer is resolved, so a host that issues no tokens still starts.
/// </summary>
public sealed class SoftDeletedUserMarkerWriterTests
{
    private readonly Mock<ICacheService> _cache = new();
    private readonly Mock<ITokenService> _tokens = new();

    [Fact]
    public async Task MarkDeletedAsync_WithARaisedLifetime_WritesAMarkerCoveringItAndForwardsTheToken()
    {
        _tokens.SetupGet(t => t.AccessTokenLifetime).Returns(TimeSpan.FromMinutes(60));
        var sut = new SoftDeletedUserMarkerWriter(_cache.Object, _tokens.Object);
        using var cts = new CancellationTokenSource();

        await sut.MarkDeletedAsync(7, cts.Token);

        _cache.Verify(c => c.SetAsync(SoftDeletedUserCache.KeyFor(7), true, TimeSpan.FromMinutes(65), cts.Token), Times.Once);
    }

    [Fact]
    public async Task MarkDeletedAsync_AtTheDefaultLifetime_KeepsTheFloor()
    {
        _tokens.SetupGet(t => t.AccessTokenLifetime).Returns(TimeSpan.FromMinutes(15));
        var sut = new SoftDeletedUserMarkerWriter(_cache.Object, _tokens.Object);

        await sut.MarkDeletedAsync(7, TestContext.Current.CancellationToken);

        _cache.Verify(
            c => c.SetAsync(SoftDeletedUserCache.KeyFor(7), true, SoftDeletedUserCache.MarkerDuration, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // A Development host validates every registration at build, so a dependency the writer's
    // constructor names would stop a host with no token service from starting at all.
    [Fact]
    public void AddApplication_OnAHostWithNoTokenService_StillBuildsWithValidationOn()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        // The writer's own registration alone: AddApplication's other services need Infrastructure.
        IServiceCollection writerOnly = new ServiceCollection();
        writerOnly.Add(services.Single(d => d.ServiceType == typeof(ISoftDeletedUserMarkerWriter)));

        var act = () =>
        {
            using var provider = writerOnly.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AddApplication_OnAHostWithNoTokenService_FailsOnlyWhenTheWriterIsResolved()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_cache.Object);
        services.AddApplication();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ISoftDeletedUserMarkerWriter>();

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{nameof(ITokenService)}*");
    }

    [Fact]
    public void AddApplication_WithItsDependenciesRegistered_ResolvesTheWriter()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_cache.Object);
        services.AddSingleton(_tokens.Object);
        services.AddApplication();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ISoftDeletedUserMarkerWriter>()
            .Should().BeOfType<SoftDeletedUserMarkerWriter>();
    }
}
