using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMCA.Common.Application;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.ServiceHost.Tests.Support;
using MMCA.Common.Shared.Abstractions;
using StackExchange.Profiling;

namespace MMCA.Common.ServiceHost.Tests;

/// <summary>
/// <c>ApplicationSettings:UseMiniProfiler</c> through the composed host: with the flag on,
/// <c>AddMmcaServiceHost</c> registers the profiler and wraps every handler in the profiling
/// decorator (outermost), and <c>UseMmcaServiceHostAsync</c> adds the profiler middleware, whose
/// result routes answer; with the flag off (the default) none of that is registered and the routes
/// do not exist.
/// </summary>
public sealed class ServiceHostProfilingTests
{
    private const string ResultsIndexPath = "/profiler/results-index";

    [Fact]
    public void FlagOn_RegistersTheProfiler_AndWrapsHandlersInTheProfilingDecorator()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        builder.Configuration.AddInMemoryCollection(Flag(on: true));

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options(configurePipeline: RegisterPingHandler));

        builder.Services.Should().Contain(d => d.ServiceType == typeof(IConfigureOptions<MiniProfilerOptions>));
        OutermostHandler(builder).Should().BeOfType<ProfilingCommandDecorator<ProfilingPingCommand, Result>>(
            "the profiling decorators are layered over the closed pipeline, so they are outermost");
    }

    [Fact]
    public void FlagOff_RegistersNoProfiler_AndNoProfilingDecorator()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();

        builder.AddMmcaServiceHost(ServiceHostTestHost.Options(configurePipeline: RegisterPingHandler));

        builder.Services.Should().NotContain(d => d.ServiceType == typeof(IConfigureOptions<MiniProfilerOptions>));
        OutermostHandler(builder).Should().NotBeOfType<ProfilingCommandDecorator<ProfilingPingCommand, Result>>();
    }

    [Fact]
    public async Task FlagOn_AddsTheMiddleware_SoTheResultRoutesAnswer()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        builder.Configuration.AddInMemoryCollection(Flag(on: true));
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();
        await app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri(ResultsIndexPath, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the test host runs in Development, the one environment the results answer in");

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FlagOff_AddsNoMiddleware_SoTheResultRoutesDoNotExist()
    {
        var builder = ServiceHostTestHost.CreateAppBuilder();
        var host = builder.AddMmcaServiceHost(ServiceHostTestHost.Options());
        await using var app = builder.Build();
        await app.UseMmcaServiceHostAsync(host, TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync(new Uri(ResultsIndexPath, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string?> Flag(bool on) =>
        new(StringComparer.Ordinal) { ["ApplicationSettings:UseMiniProfiler"] = on ? "true" : "false" };

    private static void RegisterPingHandler(MmcaApplicationPipelineBuilder pipeline, MMCA.Common.API.Startup.ModuleHostContext moduleHost)
    {
        ServiceHostTestHost.RegisterModules(pipeline, moduleHost);
        pipeline.Register(services => services.AddScoped<ICommandHandler<ProfilingPingCommand, Result>, ProfilingPingCommandHandler>());
    }

    private static ICommandHandler<ProfilingPingCommand, Result> OutermostHandler(WebApplicationBuilder builder)
    {
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ICommandHandler<ProfilingPingCommand, Result>>();
    }

    /// <summary>A command the tests register so the pipeline has a handler to wrap.</summary>
    public sealed record ProfilingPingCommand : ICommand<Result>;

    private sealed class ProfilingPingCommandHandler : ICommandHandler<ProfilingPingCommand, Result>
    {
        public Task<Result> HandleAsync(ProfilingPingCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }
}
