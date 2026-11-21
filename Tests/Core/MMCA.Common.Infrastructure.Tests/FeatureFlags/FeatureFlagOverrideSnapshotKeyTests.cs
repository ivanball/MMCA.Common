using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.Application.FeatureFlags;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Infrastructure.FeatureFlags;
using MMCA.Common.Shared.FeatureFlags;
using Moq;

namespace MMCA.Common.Infrastructure.Tests.FeatureFlags;

/// <summary>
/// Two services that share one Redis and both keep the default <c>FeatureFlags:DataSourceName</c> must
/// never share an override snapshot (Run 18 D2): with one shared key the last loader won and either
/// service's invalidation dropped the other's, so a stored Off override flapped to On.
/// </summary>
public sealed class FeatureFlagOverrideSnapshotKeyTests
{
    [Fact]
    public void Prefix_DiffersBetweenServices_ThatKeepTheSameDataSourceName()
    {
        var conference = Snapshot("ADC.Conference.Service", "Default", new Mock<ICacheService>());
        var engagement = Snapshot("ADC.Engagement.Service", "Default", new Mock<ICacheService>());

        conference.Prefix.Should().NotBe(engagement.Prefix);
        conference.Prefix.Should().NotStartWith(engagement.Prefix, "an invalidation of one must not reach the other");
        engagement.Prefix.Should().NotStartWith(conference.Prefix, "an invalidation of one must not reach the other");
    }

    [Fact]
    public void Prefix_IsStable_ForTheSameServiceAndDataSource()
    {
        var first = Snapshot("ADC.Conference.Service", "Default", new Mock<ICacheService>());
        var replica = Snapshot("ADC.Conference.Service", "Default", new Mock<ICacheService>());

        replica.Prefix.Should().Be(first.Prefix, "every replica of one service must address the same snapshot");
    }

    [Fact]
    public void Prefix_CarriesTheServiceAndTheDataSource()
        => Snapshot("ADC.Conference.Service", "Conference", new Mock<ICacheService>()).Prefix
            .Should().Be("featureflags:overrides:ADC.Conference.Service:Conference:");

    [Fact]
    public async Task GetAndInvalidate_UseTheSameBoundary()
    {
        var cache = new Mock<ICacheService>();
        string? readKey = null;
        cache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<FeatureFlagOverrideResponse[]>>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<CancellationToken, Task<FeatureFlagOverrideResponse[]>>, TimeSpan?, CancellationToken>(
                (key, _, _, _) => readKey = key)
            .ReturnsAsync([]);
        var snapshot = Snapshot("ADC.Engagement.Service", "Default", cache);

        await snapshot.GetAsync(TestContext.Current.CancellationToken);
        await snapshot.InvalidateAsync(TestContext.Current.CancellationToken);

        readKey.Should().Be(snapshot.Prefix + "all");
        cache.Verify(c => c.RemoveByPrefixAsync(snapshot.Prefix, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Prefix_FallsBackToTheEntryAssembly_WhenNoHostEnvironmentIsRegistered()
    {
        var snapshot = new FeatureFlagOverrideSnapshot(
            new Mock<ICacheService>().Object,
            new Mock<IServiceScopeFactory>().Object,
            Options.Create(new FeatureFlagOverrideSettings()));

        snapshot.Prefix.Should().StartWith(FeatureFlagOverrideSnapshot.KeyRoot).And.EndWith(":Default:");
        snapshot.Prefix.Should().NotBe(FeatureFlagOverrideSnapshot.KeyRoot + "Default:");
    }

    private static FeatureFlagOverrideSnapshot Snapshot(string applicationName, string dataSourceName, Mock<ICacheService> cache)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ApplicationName).Returns(applicationName);

        return new FeatureFlagOverrideSnapshot(
            cache.Object,
            new Mock<IServiceScopeFactory>().Object,
            Options.Create(new FeatureFlagOverrideSettings { DataSourceName = dataSourceName }),
            environment.Object);
    }
}
