using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;
using MMCA.Common.Application;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Testing.Tests;

/// <summary>
/// Exercises <see cref="DecoratorPipelineOrderTestsBase{TCommand, TCommandResult, TQuery, TQueryResult}"/>
/// against MMCA.Common's own registration sequence
/// (<c>AddApplication → ScanModuleApplicationServices → AddApplicationDecorators</c>), proving the
/// resolved pipelines nest in the ADR-014 order.
/// </summary>
public sealed class DecoratorPipelineOrderTests
    : DecoratorPipelineOrderTestsBase<DecoratorPipelineOrderTests.PingCommand, Result, DecoratorPipelineOrderTests.PingQuery, Result<string>>
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        // Test doubles for the decorator constructor dependencies.
        services.AddSingleton(Mock.Of<IFeatureManager>());
        services.AddSingleton(Mock.Of<ICorrelationContext>());
        services.AddSingleton(Mock.Of<ICacheService>());
        services.AddScoped(_ => Mock.Of<IUnitOfWork>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // The framework's real registration sequence: handler scans first, decorators last.
        services.AddApplication();
        services.ScanModuleApplicationServices<PingCommandHandler>();
        services.AddApplicationDecorators();
    }

    public sealed record PingCommand;

    public sealed record PingQuery;

    public sealed class PingCommandHandler : ICommandHandler<PingCommand, Result>
    {
        public Task<Result> HandleAsync(PingCommand command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());
    }

    public sealed class PingQueryHandler : IQueryHandler<PingQuery, Result<string>>
    {
        public Task<Result<string>> HandleAsync(PingQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success("pong"));
    }
}
