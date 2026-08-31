using AwesomeAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.UseCases;
using MMCA.Common.Application.Validation;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.UseCases;

// ── UpdateEntityHandler ──────────────────────────────────────────────────────
public sealed class UpdateEntityHandlerTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly Mock<IEntityUpdateApplier<OrderAggregate, OrderUpdateRequest, int>> _applier = new();
    private readonly Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>> _dtoMapper = new();

    /// <summary>The If-Match token every conditional update carries (ADR-035).</summary>
    private static readonly byte[] Token = [1, 2, 3];

    public UpdateEntityHandlerTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _dtoMapper.Setup(m => m.MapToDTO(It.IsAny<OrderAggregate>()))
            .Returns((OrderAggregate e) => new OrderDTO { Id = e.Id, Name = e.Name });
    }

    private UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest> CreateSut() =>
        new(_unitOfWork.Object, _applier.Object, _dtoMapper.Object);

    private void SetupLoad(OrderAggregate? entity) =>
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

    private void SetupApplier(Result result) =>
        _applier.Setup(a => a.ApplyAsync(
                It.IsAny<OrderAggregate>(),
                It.IsAny<OrderUpdateRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    [Fact]
    public async Task HandleAsync_WhenApplierSucceeds_SavesAndReturnsTheRefreshedDTO()
    {
        var entity = new OrderAggregate { Id = 3 };
        SetupLoad(entity);
        _applier.Setup(a => a.ApplyAsync(entity, It.IsAny<OrderUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => entity.Rename("renamed"));

        var request = new OrderUpdateRequest("renamed");
        var sut = CreateSut();

        var result = await sut.HandleAsync(
            new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(3, request, Token));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(3);
        result.Value.Name.Should().Be("renamed");
        _applier.Verify(a => a.ApplyAsync(entity, request, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_StampsTheCommandsRowVersionAsTheOriginal()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        SetupApplier(Result.Success());
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(
            3,
            new OrderUpdateRequest("renamed"),
            [9, 8, 7]));

        _repository.Verify(
            r => r.SetOriginalRowVersion(
                It.IsAny<OrderAggregate>(),
                It.Is<byte[]>(v => v.SequenceEqual(new byte[] { 9, 8, 7 }))),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenApplierFails_PropagatesTheFailureWithoutSavingOrMapping()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        SetupApplier(Result.Failure(Error.Validation("Order.NameRequired", "A name is required.")));
        var sut = CreateSut();

        var result = await sut.HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(
            3,
            new OrderUpdateRequest("  "),
            Token));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Order.NameRequired");
        _dtoMapper.Verify(m => m.MapToDTO(It.IsAny<OrderAggregate>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenEntityNotFound_ReturnsNotFoundNamingTheHandlerAndEntity()
    {
        SetupLoad(null);
        var sut = CreateSut();

        var result = await sut.HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(
            3,
            new OrderUpdateRequest("renamed"),
            Token));

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Type.Should().Be(ErrorType.NotFound);
        error.Source.Should().Be("UpdateEntityHandler");
        error.Target.Should().Be(nameof(OrderAggregate));
        _applier.Verify(
            a => a.ApplyAsync(It.IsAny<OrderAggregate>(), It.IsAny<OrderUpdateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_PassesTheCancellationTokenThroughToTheApplier()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        SetupLoad(new OrderAggregate { Id = 3 });
        SetupApplier(Result.Success());
        var sut = CreateSut();

        await sut.HandleAsync(
            new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(3, new OrderUpdateRequest("renamed"), Token),
            token);

        _applier.Verify(
            a => a.ApplyAsync(It.IsAny<OrderAggregate>(), It.IsAny<OrderUpdateRequest>(), token),
            Times.Once);
    }

    // ── Cache invalidation contract ──
    // The generic controller constructs this command itself, so the default prefix is the only thing
    // that makes an update evict the aggregate's cached reads.
    [Fact]
    public void CachePrefix_DefaultsToEntityFullNameConvention() =>
        new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(1, new OrderUpdateRequest("x"), Token)
            .CachePrefix.Should().Be(typeof(OrderAggregate).FullName + ":");

    [Fact]
    public void CachePrefix_CanBeOverriddenToOptOut()
    {
        var command = new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(1, new OrderUpdateRequest("x"), Token);

        (command with { CachePrefix = string.Empty }).CachePrefix.Should().BeEmpty();
    }

    // The validator auto-bridge keys off ICommandWithRequest<T>, so the command must expose its
    // request through that interface rather than as a plain property.
    [Fact]
    public void Command_ExposesItsRequestThroughICommandWithRequest()
    {
        var request = new OrderUpdateRequest("x");
        var command = new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(1, request, Token);

        command.Should().BeAssignableTo<ICommandWithRequest<OrderUpdateRequest>>()
            .Which.Request.Should().Be(request);
    }
}

// ── AddEntityCrud ────────────────────────────────────────────────────────────
public sealed class AddEntityCrudTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => new Mock<IUnitOfWork>().Object);
        services.AddScoped(_ => new Mock<IEntityRequestMapper<OrderAggregate, OrderCreateRequest, int>>().Object);
        services.AddScoped(_ => new Mock<IEntityUpdateApplier<OrderAggregate, OrderUpdateRequest, int>>().Object);
        services.AddScoped(_ => new Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>>().Object);

        services.AddEntityCrud<OrderAggregate, OrderDTO, int, OrderCreateRequest, OrderUpdateRequest>();

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddEntityCrud_RegistersAResolvableCreateHandler()
    {
        using ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<ICommandHandler<OrderCreateRequest, Result<OrderDTO>>>()
            .Should().BeOfType<CreateEntityHandler<OrderCreateRequest, OrderAggregate, int, OrderDTO>>();
    }

    [Fact]
    public void AddEntityCrud_RegistersAResolvableUpdateHandler()
    {
        using ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<ICommandHandler<
                UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>, Result<OrderDTO>>>()
            .Should().BeOfType<UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest>>();
    }

    [Fact]
    public void AddEntityCrud_RegistersAResolvableDeleteHandler()
    {
        using ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<ICommandHandler<DeleteEntityCommand<OrderAggregate, int>, Result>>()
            .Should().BeOfType<DeleteEntityHandler<OrderAggregate, int>>();
    }

    // Closed, per-entity registrations are what lets Scrutor's TryDecorate wrap them; an
    // open-generic ICommandHandler<,> entry would resolve completely undecorated.
    [Fact]
    public void AddEntityCrud_RegistersClosedHandlerServiceTypes_Scoped()
    {
        var services = new ServiceCollection();

        services.AddEntityCrud<OrderAggregate, OrderDTO, int, OrderCreateRequest, OrderUpdateRequest>();

        // Three handlers plus the update command's validator bridge.
        services.Should().HaveCount(4);
        services.Should().OnlyContain(d => !d.ServiceType.ContainsGenericParameters);
        services.Where(d => d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))
            .Should().HaveCount(3)
            .And.OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
    }

    // The generic controller constructs UpdateEntityCommand itself, so the closed generic never
    // appears in a scanned module assembly: without this bridge the request's rules never run.
    [Fact]
    public void AddEntityCrud_BridgesTheUpdateCommandToItsRequestValidators()
    {
        using ServiceProvider provider = BuildProvider();

        provider.GetRequiredService<IValidator<
                UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>>>()
            .Should().BeOfType<CommandRequestValidator<
                UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>, OrderUpdateRequest>>();
    }

    // TryAdd semantics for the bridge too: an aggregate whose update needs cross-field rules over
    // the whole command registers its own IValidator first and keeps it.
    [Fact]
    public void AddEntityCrud_LeavesAnExplicitCommandValidatorAlone()
    {
        var services = new ServiceCollection();
        services.AddTransient<
            IValidator<UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>>,
            ExplicitOrderUpdateCommandValidator>();

        services.AddEntityCrud<OrderAggregate, OrderDTO, int, OrderCreateRequest, OrderUpdateRequest>();

        ServiceDescriptor descriptor = services.Single(
            d => d.ServiceType == typeof(IValidator<UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>>));
        descriptor.ImplementationType.Should().Be<ExplicitOrderUpdateCommandValidator>();
    }

    // TryAdd semantics: an aggregate that outgrows one of the three registers its own handler first
    // and keeps the generic pair for the other two.
    [Fact]
    public void AddEntityCrud_LeavesAnAlreadyRegisteredHandlerAlone()
    {
        var services = new ServiceCollection();
        services.AddScoped<
            ICommandHandler<DeleteEntityCommand<OrderAggregate, int>, Result>,
            CustomDeleteOrderHandler>();

        services.AddEntityCrud<OrderAggregate, OrderDTO, int, OrderCreateRequest, OrderUpdateRequest>();

        ServiceDescriptor descriptor = services.Single(
            d => d.ServiceType == typeof(ICommandHandler<DeleteEntityCommand<OrderAggregate, int>, Result>));
        descriptor.ImplementationType.Should().Be<CustomDeleteOrderHandler>();
    }

    [Fact]
    public void AddEntityCrud_AfterTheDecoratorsHaveRun_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplicationDecorators();

        Action act = () => services.AddEntityCrud<OrderAggregate, OrderDTO, int, OrderCreateRequest, OrderUpdateRequest>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddEntityCrud*");
    }
}

// ── Test doubles (public so Moq's DynamicProxy can see them) ─────────────────
public sealed record OrderUpdateRequest(string Name);

public sealed class ExplicitOrderUpdateCommandValidator
    : AbstractValidator<UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>>;

public sealed class CustomDeleteOrderHandler : ICommandHandler<DeleteEntityCommand<OrderAggregate, int>, Result>
{
    public Task<Result> HandleAsync(
        DeleteEntityCommand<OrderAggregate, int> command,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}
