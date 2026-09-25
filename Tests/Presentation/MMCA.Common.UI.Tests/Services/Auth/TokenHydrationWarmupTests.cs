using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.UI.Services.Auth.Tokens;
using Moq;

namespace MMCA.Common.UI.Tests.Services.Auth;

/// <summary>
/// Tests for <see cref="TokenHydrationWarmup"/>: it reads the access token once through the READ
/// path and never lets a failure escape, because boot must behave the same whether the warm-up
/// succeeds, fails or finds no session.
/// </summary>
public sealed class TokenHydrationWarmupTests
{
    [Fact]
    public async Task WarmAsync_ReadsTheAccessTokenOnce()
    {
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ReturnsAsync("token");

        await TokenHydrationWarmup.WarmAsync(Build(storage.Object));

        storage.Verify(s => s.GetAccessTokenAsync(), Times.Once);
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WarmAsync_WhenHydrationThrows_DoesNotFault()
    {
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ThrowsAsync(new InvalidOperationException("interop unavailable"));

        var act = () => TokenHydrationWarmup.WarmAsync(Build(storage.Object));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WarmAsync_WithNoLoggingRegistered_StillSwallowsTheFailure()
    {
        var storage = new Mock<ITokenStorageService>();
        storage.Setup(s => s.GetAccessTokenAsync()).ThrowsAsync(new HttpRequestException("offline"));
        await using var provider = new ServiceCollection().AddSingleton(storage.Object).BuildServiceProvider();

        var act = () => TokenHydrationWarmup.WarmAsync(provider);

        await act.Should().NotThrowAsync();
    }

    private static ServiceProvider Build(ITokenStorageService storage) =>
        new ServiceCollection().AddLogging().AddSingleton(storage).BuildServiceProvider();
}
