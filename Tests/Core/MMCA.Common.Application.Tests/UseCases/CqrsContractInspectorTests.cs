using AwesomeAssertions;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Application.Tests.UseCases;

public sealed class CqrsContractInspectorTests
{
    [Fact]
    public void FindContractMismatches_DetectsAResultTypeMismatch()
    {
        var mismatches = CqrsContractInspector.FindContractMismatches(typeof(CqrsContractInspectorTests).Assembly);

        mismatches.Should().ContainSingle(m => m.HandlerType == typeof(DriftedMarkedCommandHandler))
            .Which.Should().Match<CqrsContractMismatch>(m =>
                m.Kind == CqrsContractMismatchKind.ResultType
                && m.RequestType == typeof(DriftedMarkedCommand)
                && m.DeclaredResultType == typeof(Result<int>)
                && m.HandlerResultType == typeof(Result<string>));
    }

    [Fact]
    public void FindContractMismatches_DescribesTheMismatchReadably()
    {
        var mismatch = CqrsContractInspector
            .FindContractMismatches(typeof(CqrsContractInspectorTests).Assembly)
            .First(m => m.HandlerType == typeof(DriftedMarkedCommandHandler));

        mismatch.Describe().Should()
            .Contain("DriftedMarkedCommand").And
            .Contain("DriftedMarkedCommandHandler");
    }

    [Fact]
    public void FindContractMismatches_IgnoresAgreeingHandlers()
    {
        var mismatches = CqrsContractInspector.FindContractMismatches(typeof(CqrsContractInspectorTests).Assembly);

        mismatches.Should().NotContain(m => m.HandlerType == typeof(AgreeingMarkedCommandHandler));
        mismatches.Should().NotContain(m => m.HandlerType == typeof(AgreeingMarkedQueryHandler));
    }

    [Fact]
    public void FindContractMismatches_IgnoresUnmarkedRequests()
    {
        var mismatches = CqrsContractInspector.FindContractMismatches(typeof(CqrsContractInspectorTests).Assembly);

        mismatches.Should().NotContain(m => m.HandlerType == typeof(UnmarkedCommandHandler));
    }

    [Fact]
    public void FindContractMismatches_DetectsACommandMarkerHandledAsAQuery()
    {
        var mismatches = CqrsContractInspector.FindContractMismatches(typeof(CqrsContractInspectorTests).Assembly);

        mismatches.Should().ContainSingle(m => m.HandlerType == typeof(WrongKindHandler))
            .Which.Kind.Should().Be(CqrsContractMismatchKind.HandlerKind);
    }

    [Fact]
    public void FindContractMismatches_OnAnAssemblyWithNoHandlers_ReturnsEmpty()
    {
        var mismatches = CqrsContractInspector.FindContractMismatches(typeof(object).Assembly);

        mismatches.Should().BeEmpty();
    }

    [Fact]
    public void FindContractMismatches_WithNullAssemblies_Throws()
    {
        Action act = () => CqrsContractInspector.FindContractMismatches(assemblies: null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FindContractMismatches_SkipsTheFrameworksOwnOpenGenericDecorators()
    {
        var mismatches = CqrsContractInspector.FindContractMismatches(typeof(ICommandHandler<,>).Assembly);

        mismatches.Should().BeEmpty(
            "the framework's decorators are open generic definitions whose request type argument is a type parameter, not a request");
    }
}

// ── Test types ──
public sealed record AgreeingMarkedCommand : ICommand<Result<int>>;

public sealed record DriftedMarkedCommand : ICommand<Result<int>>;

public sealed record AgreeingMarkedQuery : IQuery<Result<string>>;

public sealed record WrongKindRequest : ICommand<Result<string>>;

public sealed record UnmarkedCommand;

public sealed class AgreeingMarkedCommandHandler : ICommandHandler<AgreeingMarkedCommand, Result<int>>
{
    public Task<Result<int>> HandleAsync(AgreeingMarkedCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(1));
}

public sealed class DriftedMarkedCommandHandler : ICommandHandler<DriftedMarkedCommand, Result<string>>
{
    public Task<Result<string>> HandleAsync(DriftedMarkedCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success("drifted"));
}

public sealed class AgreeingMarkedQueryHandler : IQueryHandler<AgreeingMarkedQuery, Result<string>>
{
    public Task<Result<string>> HandleAsync(AgreeingMarkedQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success("ok"));
}

public sealed class WrongKindHandler : IQueryHandler<WrongKindRequest, Result<string>>
{
    public Task<Result<string>> HandleAsync(WrongKindRequest query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success("ok"));
}

public sealed class UnmarkedCommandHandler : ICommandHandler<UnmarkedCommand, Result>
{
    public Task<Result> HandleAsync(UnmarkedCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
