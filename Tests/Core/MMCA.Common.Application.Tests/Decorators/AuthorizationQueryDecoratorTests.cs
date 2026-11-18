using AwesomeAssertions;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Application.UseCases.Decorators;
using MMCA.Common.Application.UseCases.Markers;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.Decorators;

public sealed class AuthorizationQueryDecoratorTests : AuthorizationDecoratorTestsBase<Result<string>>
{
    protected override string GuardedPermission => "catalog.products.read";

    protected override string UnrelatedPermission => "catalog.products.write";

    protected override string GuardedRequestName => nameof(GuardedQuery);

    protected override Result<string> SuccessResult => Result.Success("ok");

    protected override void AssertPassedThrough(Result<string> result)
    {
        base.AssertPassedThrough(result);
        result.Value.Should().Be("ok");
    }

    protected override Func<Task<T>> CreateSut<T>(RequestKind kind, Func<Task<T>> inner) => kind switch
    {
        RequestKind.Unguarded => Wrap(new UnguardedQuery(), inner),
        RequestKind.Guarded => Wrap(new GuardedQuery(), inner),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private Func<Task<T>> Wrap<TQuery, T>(TQuery query, Func<Task<T>> inner)
        where TQuery : class
    {
        var handler = new Mock<IQueryHandler<TQuery, T>>();
        handler.Setup(x => x.HandleAsync(It.IsAny<TQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => inner());

        var sut = new AuthorizationQueryDecorator<TQuery, T>(
            handler.Object, CurrentUser.Object, PermissionRegistry.Object);

        return () => sut.HandleAsync(query);
    }
}

// ── Test types ──
public sealed record UnguardedQuery;

public sealed record GuardedQuery : IRequiresPermission
{
    public string Permission => "catalog.products.read";
}
