using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Events;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Permissions;
using Moq;

namespace MMCA.Common.Application.Tests;

/// <summary>
/// Covers <c>AddMmcaApplicationPipeline</c>, the registration-time guard that closes the decorator
/// pipeline, and <c>VerifyDecoratorPipeline</c>.
/// </summary>
public sealed class ApplicationPipelineCompositionTests
{
    // ── The helper is exactly the manual sequence ──
    [Fact]
    public void AddMmcaApplicationPipeline_ProducesTheSameRegistrationsAsTheManualSequence()
    {
        var manual = new ServiceCollection();
        manual.AddApplication();
        manual.ScanModuleApplicationServices<PipelineMarker>();
        manual.AddApplicationDecorators();

        var viaHelper = new ServiceCollection();
        viaHelper.AddMmcaApplicationPipeline(pipeline => pipeline.ScanModule<PipelineMarker>());

        Shape(viaHelper).Should().Equal(Shape(manual));
    }

    [Fact]
    public void AddMmcaApplicationPipeline_WithAssemblyScan_MatchesTheMarkerOverload()
    {
        var byMarker = new ServiceCollection();
        byMarker.AddMmcaApplicationPipeline(pipeline => pipeline.ScanModule<PipelineMarker>());

        var byAssembly = new ServiceCollection();
        byAssembly.AddMmcaApplicationPipeline(
            pipeline => pipeline.ScanModules(typeof(PipelineMarker).Assembly));

        Shape(byAssembly).Should().Equal(Shape(byMarker));
    }

    [Fact]
    public void AddMmcaApplicationPipeline_RunsTheRegisterCallback_BeforeTheDecorators()
    {
        var services = new ServiceCollection();

        services.AddMmcaApplicationPipeline(pipeline => pipeline
            .Register(s => s.AddScoped<IQueryHandler<PipelinePingQuery, Result<string>>, PipelinePingQueryHandler>()));

        // Decorated: Scrutor rewrote the implementation-type registration into a factory.
        var descriptor = services.Last(d =>
            !d.IsKeyedService && d.ServiceType == typeof(IQueryHandler<PipelinePingQuery, Result<string>>));

        descriptor.ImplementationType.Should().BeNull();
        descriptor.ImplementationFactory.Should().NotBeNull();
    }

    [Fact]
    public void AddMmcaApplicationPipeline_WithNoModules_StillRegistersCoreServicesAndSeals()
    {
        var services = new ServiceCollection();

        services.AddMmcaApplicationPipeline();

        services.Should().Contain(d => d.ServiceType == typeof(IDomainEventDispatcher));
        Action verify = () => services.VerifyDecoratorPipeline();
        verify.Should().NotThrow();
    }

    [Fact]
    public void AddMmcaApplicationPipeline_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();

        IServiceCollection result = services.AddMmcaApplicationPipeline();

        result.Should().BeSameAs(services);
    }

    // ── Registration-time guard ──
    [Fact]
    public void ScanModuleApplicationServices_AfterDecorators_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplicationDecorators();

        Action act = () => services.ScanModuleApplicationServices<PipelineMarker>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ScanModuleApplicationServices*closed the decorator pipeline*");
    }

    [Fact]
    public void AddApplicationDecorators_CalledTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplicationDecorators();

        Action act = () => services.AddApplicationDecorators();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddApplicationDecorators*closed the decorator pipeline*");
    }

    [Fact]
    public void AddMmcaApplicationPipeline_AfterDecorators_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplicationDecorators();

        Action act = () => services.AddMmcaApplicationPipeline();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddMmcaApplicationPipeline*closed the decorator pipeline*");
    }

    [Fact]
    public void AddMmcaApplicationPipeline_CalledTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddMmcaApplicationPipeline();

        Action act = () => services.AddMmcaApplicationPipeline();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddApplicationProfiling_AfterDecorators_IsStillAllowed()
    {
        var services = new ServiceCollection();
        services.AddApplicationDecorators();

        Action act = () => services.AddApplicationProfiling();

        act.Should().NotThrow(
            "profiling is an opt-in wrapper layered on top of the closed pipeline, not a handler registration");
    }

    // ── VerifyDecoratorPipeline ──
    [Fact]
    public void VerifyDecoratorPipeline_OnACorrectlyOrderedCollection_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddScoped<ICommandHandler<PipelinePingCommand, Result>, PipelinePingCommandHandler>();
        services.AddScoped<IQueryHandler<PipelinePingQuery, Result<string>>, PipelinePingQueryHandler>();
        services.AddApplicationDecorators();

        Action act = () => services.VerifyDecoratorPipeline();

        act.Should().NotThrow();
    }

    [Fact]
    public void VerifyDecoratorPipeline_WhenAHandlerIsRegisteredAfterTheDecorators_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddScoped<ICommandHandler<PipelinePingCommand, Result>, PipelinePingCommandHandler>();
        services.AddApplicationDecorators();

        // Raw DI registration bypasses the framework's guarded surface: exactly the drift
        // VerifyDecoratorPipeline exists to catch.
        services.AddScoped<IQueryHandler<PipelinePingQuery, Result<string>>, PipelinePingQueryHandler>();

        Action act = () => services.VerifyDecoratorPipeline();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PipelinePingQueryHandler*");
    }

    [Fact]
    public void VerifyDecoratorPipeline_WhenDecoratorsWereNeverAdded_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddScoped<ICommandHandler<PipelinePingCommand, Result>, PipelinePingCommandHandler>();

        Action act = () => services.VerifyDecoratorPipeline();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*never closed*");
    }

    // ── ADR-014 query nesting, resolved from a real provider ──
    [Fact]
    public void QueryPipeline_NestsValidatingBetweenCachingAndTimeout()
    {
        var services = new ServiceCollection();
        AddDecoratorDependencies(services);
        services.AddApplication();
        services.AddScoped<IQueryHandler<PipelinePingQuery, Result<string>>, PipelinePingQueryHandler>();
        services.AddApplicationDecorators();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var outermost = scope.ServiceProvider.GetRequiredService<IQueryHandler<PipelinePingQuery, Result<string>>>();

        UnwrapChain(outermost, typeof(IQueryHandler<PipelinePingQuery, Result<string>>)).Should().Equal(
            "FeatureGateQueryDecorator",
            "AuthorizationQueryDecorator",
            "LoggingQueryDecorator",
            "CachingQueryDecorator",
            "ValidatingQueryDecorator",
            "TimeoutQueryDecorator",
            "PipelinePingQueryHandler");
    }

    [Fact]
    public void CommandPipeline_KeepsItsAdr014Nesting()
    {
        var services = new ServiceCollection();
        AddDecoratorDependencies(services);
        services.AddApplication();
        services.AddScoped<ICommandHandler<PipelinePingCommand, Result>, PipelinePingCommandHandler>();
        services.AddApplicationDecorators();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var outermost = scope.ServiceProvider.GetRequiredService<ICommandHandler<PipelinePingCommand, Result>>();

        UnwrapChain(outermost, typeof(ICommandHandler<PipelinePingCommand, Result>)).Should().Equal(
            "FeatureGateCommandDecorator",
            "AuthorizationCommandDecorator",
            "LoggingCommandDecorator",
            "CachingCommandDecorator",
            "ValidatingCommandDecorator",
            "TimeoutCommandDecorator",
            "TransactionalCommandDecorator",
            "PipelinePingCommandHandler");
    }

    private static void AddDecoratorDependencies(IServiceCollection services)
    {
        services.AddSingleton(Mock.Of<IFeatureManager>());
        services.AddScoped(_ => Mock.Of<ICurrentUserService>());
        services.AddSingleton(Mock.Of<IPermissionRegistry>());
        services.AddSingleton(Mock.Of<ICorrelationContext>());
        services.AddSingleton(Mock.Of<ICacheService>());
        services.AddScoped(_ => Mock.Of<IUnitOfWork>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    /// <summary>
    /// Shape of a registration, ignoring the closure identity of factory delegates and Scrutor's
    /// random keyed-service keys, both of which differ between two equivalent runs.
    /// </summary>
    private static List<string> Shape(IServiceCollection services) =>
        [.. services.Select(d =>
        {
            // The keyed accessors throw on a non-keyed descriptor, and vice versa.
            var implementation = d.IsKeyedService
                ? d.KeyedImplementationType?.FullName
                : d.ImplementationType?.FullName;

            return $"{d.ServiceType.FullName}|{d.Lifetime}|{implementation ?? "-"}|{d.IsKeyedService}";
        })];

    private static List<string> UnwrapChain(object outermost, Type handlerServiceType)
    {
        var chain = new List<string>();
        var current = outermost;

        while (current is not null)
        {
            var name = current.GetType().Name;
            var tick = name.IndexOf('`', StringComparison.Ordinal);
            chain.Add(tick >= 0 ? name[..tick] : name);

            var local = current;
            current = local.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.GetValue(local))
                .FirstOrDefault(v => v is not null && handlerServiceType.IsInstanceOfType(v) && !ReferenceEquals(v, local));
        }

        return chain;
    }
}

// ── Test types ──

/// <summary>Assembly marker for the module-scan comparisons.</summary>
public sealed class PipelineMarker;

public sealed record PipelinePingCommand;

public sealed record PipelinePingQuery;

public sealed class PipelinePingCommandHandler : ICommandHandler<PipelinePingCommand, Result>
{
    public Task<Result> HandleAsync(PipelinePingCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

public sealed class PipelinePingQueryHandler : IQueryHandler<PipelinePingQuery, Result<string>>
{
    public Task<Result<string>> HandleAsync(PipelinePingQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success("pong"));
}
