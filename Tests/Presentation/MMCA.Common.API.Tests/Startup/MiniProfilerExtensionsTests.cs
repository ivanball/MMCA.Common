using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMCA.Common.API.Startup;
using MMCA.Common.Application.Settings;
using Moq;
using StackExchange.Profiling;

namespace MMCA.Common.API.Tests.Startup;

/// <summary>
/// <c>AddMiniProfilerIfEnabled</c>: the flag off registers nothing, the flag on registers the profiler
/// under <c>/profiler</c>, and its result routes (a profile shows SQL text and timings) answer only
/// in the Development environment.
/// </summary>
public sealed class MiniProfilerExtensionsTests
{
    [Fact]
    public void FlagOff_RegistersNothing()
    {
        var services = new ServiceCollection();

        services.AddMiniProfilerIfEnabled(new ApplicationSettings { UseMiniProfiler = false });

        services.Should().BeEmpty();
    }

    [Fact]
    public void FlagOn_RegistersTheProfilerUnderTheProfilerRoute()
    {
        using var provider = ProviderWithProfiler(Environments.Development);

        provider.GetRequiredService<IOptions<MiniProfilerOptions>>().Value.RouteBasePath.Value.Should().Be("/profiler");
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    public void Results_AreReadableOnlyInDevelopment(string environment, bool readable)
    {
        using var provider = ProviderWithProfiler(environment);
        var options = provider.GetRequiredService<IOptions<MiniProfilerOptions>>().Value;
        var request = new DefaultHttpContext { RequestServices = provider }.Request;

        options.ResultsAuthorize.Should().NotBeNull("an unset ResultsAuthorize serves every profile to anyone");
        options.ResultsAuthorize!(request).Should().Be(readable);
        options.ResultsListAuthorize.Should().NotBeNull();
        options.ResultsListAuthorize!(request).Should().Be(readable);
    }

    private static ServiceProvider ProviderWithProfiler(string environment)
    {
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(e => e.EnvironmentName).Returns(environment);

        var services = new ServiceCollection();
        services.AddSingleton(hostEnvironment.Object);
        services.AddMiniProfilerIfEnabled(new ApplicationSettings { UseMiniProfiler = true });
        return services.BuildServiceProvider();
    }
}
