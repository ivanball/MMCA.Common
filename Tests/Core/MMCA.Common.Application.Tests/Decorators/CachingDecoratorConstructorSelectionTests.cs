using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

/// <summary>
/// Both caching decorators carry a second, logger-less constructor kept only for source
/// compatibility with consumers that build the decorator by hand. These tests pin the thing that
/// makes that overload safe: when the container builds the decorator it must still choose the
/// logger-bearing constructor. If selection ever flipped, the decorators would keep working and
/// every test would stay green while production silently stopped reporting cache failures.
/// </summary>
public sealed class CachingDecoratorConstructorSelectionTests
{
    [Fact]
    public void CommandDecorator_ResolvedFromContainer_UsesLoggerBearingConstructor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<ICacheService>().Object);
        services.AddScoped<ICommandHandler<CtorProbeCommand, Result>, CtorProbeCommandHandler>();
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(CachingCommandDecorator<,>));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<ICommandHandler<CtorProbeCommand, Result>>();

        CapturedLogger(resolved).Should().NotBeOfType<NullLogger<CachingCommandDecorator<CtorProbeCommand, Result>>>(
            "the container must pick the 3-argument constructor, or cache-invalidation warnings are silently discarded");
    }

    [Fact]
    public void QueryDecorator_ResolvedFromContainer_UsesLoggerBearingConstructor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<ICacheService>().Object);
        services.AddScoped<IQueryHandler<CtorProbeQuery, Result<string>>, CtorProbeQueryHandler>();
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(CachingQueryDecorator<,>));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IQueryHandler<CtorProbeQuery, Result<string>>>();

        CapturedLogger(resolved).Should().NotBeOfType<NullLogger<CachingQueryDecorator<CtorProbeQuery, Result<string>>>>(
            "the container must pick the 3-argument constructor, or cache-populate warnings are silently discarded");
    }

    [Fact]
    public void LoggerLessConstructors_RemainAvailableForDirectConstruction()
    {
        // The source-compatibility contract itself: a consumer pinned to the released package
        // constructs these with two arguments, and that must keep compiling and working.
        var command = new CachingCommandDecorator<CtorProbeCommand, Result>(
            new CtorProbeCommandHandler(), new Mock<ICacheService>().Object);
        var query = new CachingQueryDecorator<CtorProbeQuery, Result<string>>(
            new CtorProbeQueryHandler(), new Mock<ICacheService>().Object);

        command.Should().NotBeNull();
        query.Should().NotBeNull();
    }

    /// <summary>
    /// Reads the logger the decorator captured, via the compiler-generated primary-constructor field.
    /// </summary>
    private static object? CapturedLogger(object decorator) =>
        decorator.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.GetValue(decorator))
            .FirstOrDefault(value => value is ILogger);
}

// ── Test types (must be public for Moq DynamicProxy and DI activation) ──
public sealed record CtorProbeCommand : ICacheInvalidating
{
    public string CachePrefix => "ctor-probe";
}

public sealed class CtorProbeCommandHandler : ICommandHandler<CtorProbeCommand, Result>
{
    public Task<Result> HandleAsync(CtorProbeCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

public sealed record CtorProbeQuery : IQueryCacheable
{
    public string CacheKey => "ctor-probe-key";

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed class CtorProbeQueryHandler : IQueryHandler<CtorProbeQuery, Result<string>>
{
    public Task<Result<string>> HandleAsync(CtorProbeQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success("probe"));
}
