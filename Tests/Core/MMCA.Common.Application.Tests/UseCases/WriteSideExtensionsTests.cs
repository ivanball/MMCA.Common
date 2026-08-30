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

// ── MutationContext ──────────────────────────────────────────────────────────
public sealed class MutationContextTests
{
    [Fact]
    public void Set_ThenTryGet_RoundTripsTheValueWithItsType()
    {
        var context = new MutationContext();

        context.Set("blob", "avatars/old.png");

        context.TryGet<string>("blob", out var value).Should().BeTrue();
        value.Should().Be("avatars/old.png");
    }

    [Fact]
    public void TryGet_WithTheWrongType_AnswersFalseRatherThanThrowing()
    {
        var context = new MutationContext();
        context.Set("count", 3);

        context.TryGet<string>("count", out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void GetOrDefault_ForAnAbsentKey_AnswersTheTypeDefault()
    {
        var context = new MutationContext();

        context.GetOrDefault<string>("missing").Should().BeNull();
        context.GetOrDefault<int>("missing").Should().Be(0);
    }

    [Fact]
    public void Set_Twice_KeepsTheLastValueAndTheKeyIsVisible()
    {
        var context = new MutationContext();

        context.Set("blob", "first");
        context.Set("blob", "second");

        context.Contains("blob").Should().BeTrue();
        context.GetOrDefault<string>("blob").Should().Be("second");
        context.Items.Should().ContainSingle();
    }

    [Fact]
    public void SkipSave_IsFalseUntilAsked_AndIdempotentAfterwards()
    {
        var context = new MutationContext();
        context.SaveSkipped.Should().BeFalse();

        context.SkipSave();
        context.SkipSave();

        context.SaveSkipped.Should().BeTrue();
    }
}

// ── Extension 1: side data carried out, and the no-op short-circuit ──────────
public sealed class MutationContextHandlerTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly List<string> _calls = [];

    public MutationContextHandlerTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("save"))
            .ReturnsAsync(1);
    }

    private void SetupLoad(OrderAggregate? entity) =>
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

    [Fact]
    public async Task PayloadFlavour_CarriesAPreMutationValueIntoTheResultAndThePostSaveHooks()
    {
        var entity = new OrderAggregate { Id = 3 };
        entity.Rename("before");
        SetupLoad(entity);
        var sut = new TestRenameOrderPayloadHandler(_unitOfWork.Object, _calls);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "after", null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("after");
        result.Value.PreviousName.Should().Be("before");
        _calls.Should().Equal("save", "log:before", "mutated:before");
    }

    [Fact]
    public async Task PayloadFlavour_WhenTheMutationFails_NeverBuildsAResult()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderPayloadHandler(_unitOfWork.Object, _calls);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "  ", null));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Order.NameRequired");
        _calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SkipSave_SucceedsWithoutSavingLoggingOrRunningThePostCommitHook()
    {
        var entity = new OrderAggregate { Id = 3 };
        entity.Rename("current");
        SetupLoad(entity);
        var sut = new TestRenameOrderPayloadHandler(_unitOfWork.Object, _calls);

        // The command asks for the name the aggregate already has: an idempotent no-op.
        var result = await sut.HandleAsync(new RenameOrderCommand(3, "current", null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("current");
        _calls.Should().BeEmpty();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SkipSave_OnTheBareResultFlavour_SkipsTheSaveAndTheLog()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestSkippingRenameHandler(_unitOfWork.Object, _calls);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "renamed", null));

        result.IsSuccess.Should().BeTrue();
        _calls.Should().BeEmpty();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ContextFreeOverride_StillReceivesEveryHookThroughTheForwardingDefaults()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderHandler(_unitOfWork.Object, _calls);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "renamed", [1]));

        result.IsSuccess.Should().BeTrue();
        _calls.Should().Equal("mutate", "save", "log", "mutated");
        _repository.Verify(
            r => r.SetOriginalRowVersion(It.IsAny<OrderAggregate>(), It.Is<byte[]>(v => v[0] == 1)),
            Times.Once);
    }

    [Fact]
    public async Task AHandlerThatOverridesNeitherMutateOverload_FailsLoudly()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestNoMutationHandler(_unitOfWork.Object);

        Func<Task> act = () => sut.HandleAsync(new RenameOrderCommand(3, "renamed", null));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MutateAsync*");
    }
}

// ── Extension 2: attempt-scope parity with the create workflow ───────────────
public sealed class MutateAttemptScopeTests
{
    private readonly Mock<IUnitOfWork> _firstUnitOfWork = new();
    private readonly Mock<IUnitOfWork> _retryUnitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();

    public MutateAttemptScopeTests()
    {
        _firstUnitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _retryUnitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OrderAggregate { Id = 3 });
    }

    [Fact]
    public async Task ARetryRunsTheWholeWorkflowAgainstTheFreshScopesUnitOfWork()
    {
        // The first attempt loses a unique-key race; the ambient context still tracks it, so the
        // retry has to run against a clean one.
        _firstUnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("duplicate key"));
        _retryUnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = new TestRetryingRenameHandler(_firstUnitOfWork.Object, _retryUnitOfWork.Object);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "renamed", null));

        result.IsSuccess.Should().BeTrue();
        sut.Attempts.Should().Be(2);
        _firstUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retryUnitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _retryUnitOfWork.Verify(u => u.GetRepository<OrderAggregate, int>(), Times.Once);
    }

    [Fact]
    public async Task WithNoCollisionTheFirstAttemptStandsAndTheRetryScopeIsNeverTouched()
    {
        _firstUnitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var sut = new TestRetryingRenameHandler(_firstUnitOfWork.Object, _retryUnitOfWork.Object);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "renamed", null));

        result.IsSuccess.Should().BeTrue();
        sut.Attempts.Should().Be(1);
        _retryUnitOfWork.Verify(u => u.GetRepository<OrderAggregate, int>(), Times.Never);
    }
}

// ── Extension 3: delete with eager loading and a pre-delete hook ─────────────
public sealed class DeleteEntityHandlerExtensionTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly List<string> _calls = [];

    public DeleteEntityHandlerExtensionTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private void SetupIncludeLoad(OrderAggregate? entity) =>
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

    [Fact]
    public async Task ASubclassWithIncludes_LoadsThemTrackedBeforeTheCascade()
    {
        var entity = new OrderAggregate { Id = 1 };
        SetupIncludeLoad(entity);
        var sut = new TestDeleteOrderHandler(_unitOfWork.Object, Result.Success(), _calls);

        var result = await sut.HandleAsync(new DeleteEntityCommand<OrderAggregate, int>(1));

        result.IsSuccess.Should().BeTrue();
        entity.IsDeleted.Should().BeTrue();
        _repository.Verify(
            r => r.GetByIdAsync(
                1,
                It.Is<IEnumerable<string>>(i => i.Contains(nameof(OrderAggregate.Lines))),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _calls.Should().Equal("deleting", "log");
    }

    [Fact]
    public async Task AFailingPreDeleteHook_StopsTheDeleteAndTheSave()
    {
        var entity = new OrderAggregate { Id = 1 };
        SetupIncludeLoad(entity);
        var refusal = Result.Failure(Error.Conflict("Order.HasLines", "The order still has lines."));
        var sut = new TestDeleteOrderHandler(_unitOfWork.Object, refusal, _calls);

        var result = await sut.HandleAsync(new DeleteEntityCommand<OrderAggregate, int>(1));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Order.HasLines");
        entity.IsDeleted.Should().BeFalse();
        _calls.Should().Equal("deleting");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ThePreDeleteHookNeverRunsWhenTheAggregateIsGone()
    {
        SetupIncludeLoad(null);
        var sut = new TestDeleteOrderHandler(_unitOfWork.Object, Result.Success(), _calls);

        var result = await sut.HandleAsync(new DeleteEntityCommand<OrderAggregate, int>(1));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.NotFound);
        _calls.Should().BeEmpty();
    }

    // The default is unchanged: no includes means the same bare by-id query this handler has
    // always issued, so an existing consumer sees the identical behavior.
    [Fact]
    public async Task WithNoIncludes_TheDefaultStillIssuesTheBareByIdQuery()
    {
        _repository.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderAggregate { Id = 1 });
        var sut = new DeleteEntityHandler<OrderAggregate, int>(_unitOfWork.Object);

        await sut.HandleAsync(new DeleteEntityCommand<OrderAggregate, int>(1));

        _repository.Verify(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(
            r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

// ── Extension 4: two verbs over one request DTO ─────────────────────────────
public sealed class VerbDiscriminatedUpdateTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>> _dtoMapper = new();

    public VerbDiscriminatedUpdateTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OrderAggregate { Id = 3 });
        _dtoMapper.Setup(m => m.MapToDTO(It.IsAny<OrderAggregate>()))
            .Returns((OrderAggregate e) => new OrderDTO { Id = e.Id, Name = e.Name });
    }

    [Fact]
    public async Task EachVerbRunsItsOwnApplierOverTheSameRequestType()
    {
        var increase = new IncreaseOrderApplier();
        var decrease = new DecreaseOrderApplier();
        var request = new OrderUpdateRequest("ignored");

        var increaseResult = await new UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest, IncreaseOrderApplier>(
                _unitOfWork.Object, increase, _dtoMapper.Object)
            .HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>(3, request, [1]));

        var decreaseResult = await new UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest, DecreaseOrderApplier>(
                _unitOfWork.Object, decrease, _dtoMapper.Object)
            .HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, DecreaseOrderApplier>(3, request, [1]));

        increaseResult.Value!.Name.Should().Be("increased");
        decreaseResult.Value!.Name.Should().Be("decreased");
        increase.Calls.Should().Be(1);
        decrease.Calls.Should().Be(1);
    }

    [Fact]
    public async Task TheVerbStampsTheRowVersionLikeTheUndiscriminatedCommand()
    {
        await new UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest, IncreaseOrderApplier>(
                _unitOfWork.Object, new IncreaseOrderApplier(), _dtoMapper.Object)
            .HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>(
                3, new OrderUpdateRequest("x"), [4, 5]));

        _repository.Verify(
            r => r.SetOriginalRowVersion(
                It.IsAny<OrderAggregate>(),
                It.Is<byte[]>(v => v.SequenceEqual(new byte[] { 4, 5 }))),
            Times.Once);
    }

    [Fact]
    public void TheTwoVerbsAreDistinctCommandTypesOverOneRequestType()
    {
        var request = new OrderUpdateRequest("x");
        var increase = new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>(3, request, [1]);
        var decrease = new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, DecreaseOrderApplier>(3, request, [1]);

        increase.GetType().Should().NotBe(decrease.GetType());
        increase.ApplierType.Should().Be<IncreaseOrderApplier>();
        decrease.ApplierType.Should().Be<DecreaseOrderApplier>();
        increase.Should().BeAssignableTo<ICommandWithRequest<OrderUpdateRequest>>();
    }

    [Fact]
    public void TheVerbCommandKeepsTheAggregateCachePrefixAndItsOptOut()
    {
        var command = new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>(
            1, new OrderUpdateRequest("x"), [1]);

        command.CachePrefix.Should().Be(typeof(OrderAggregate).FullName + ":");
        (command with { CachePrefix = string.Empty }).CachePrefix.Should().BeEmpty();
    }
}

// ── Extension 5: a derived command reaching a command-aware applier ──────────
public sealed class DerivedUpdateCommandTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>> _dtoMapper = new();
    private readonly OwnerOrderApplier _applier = new();

    public DerivedUpdateCommandTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OrderAggregate { Id = 3 });
        _dtoMapper.Setup(m => m.MapToDTO(It.IsAny<OrderAggregate>()))
            .Returns((OrderAggregate e) => new OrderDTO { Id = e.Id, Name = e.Name });
    }

    private UpdateEntityCommandHandler<RenameOrderByOwnerCommand, OrderAggregate, OrderDTO, int, OrderUpdateRequest> CreateSut() =>
        new(_unitOfWork.Object, _applier, _dtoMapper.Object);

    [Fact]
    public async Task TheApplierSeesTheServerDerivedFlagTheRequestDoesNotCarry()
    {
        var result = await CreateSut().HandleAsync(
            new RenameOrderByOwnerCommand(3, new OrderUpdateRequest("renamed"), CallerIsOwner: true, RowVersion: [1]));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("renamed");
        _applier.LastCallerIsOwner.Should().BeTrue();
    }

    [Fact]
    public async Task TheApplierCanRefuseOnThatFlagAndNothingIsSaved()
    {
        var result = await CreateSut().HandleAsync(
            new RenameOrderByOwnerCommand(3, new OrderUpdateRequest("renamed"), CallerIsOwner: false, RowVersion: [1]));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Order.NotTheOwner");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheApplierCanWriteSideDataAndShortCircuitThroughTheSharedContext()
    {
        var result = await CreateSut().HandleAsync(
            new RenameOrderByOwnerCommand(3, new OrderUpdateRequest(OwnerOrderApplier.NoOpName), CallerIsOwner: true, RowVersion: [1]));

        result.IsSuccess.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheInheritedRowVersionIsStillStampedOnTheRoot()
    {
        await CreateSut().HandleAsync(new RenameOrderByOwnerCommand(
            3, new OrderUpdateRequest("renamed"), CallerIsOwner: true, RowVersion: [7]));

        _repository.Verify(
            r => r.SetOriginalRowVersion(
                It.IsAny<OrderAggregate>(),
                It.Is<byte[]>(v => v[0] == 7)),
            Times.Once);
    }

    [Fact]
    public void TheDerivedCommandInheritsTheValidatorBridgeAndTheCachePrefix()
    {
        var command = new RenameOrderByOwnerCommand(3, new OrderUpdateRequest("x"), CallerIsOwner: true, RowVersion: [1]);

        command.Should().BeAssignableTo<ICommandWithRequest<OrderUpdateRequest>>();
        command.Should().BeAssignableTo<UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>>();
        command.CachePrefix.Should().Be(typeof(OrderAggregate).FullName + ":");
    }
}

// ── Registration helpers ─────────────────────────────────────────────────────
public sealed class WriteSideRegistrationTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => new Mock<IUnitOfWork>().Object);
        services.AddScoped(_ => new Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>>().Object);
        services.AddScoped<IncreaseOrderApplier>();
        services.AddScoped<DecreaseOrderApplier>();
        services.AddScoped<
            IEntityUpdateCommandApplier<OrderAggregate, OrderUpdateRequest, int, RenameOrderByOwnerCommand>,
            OwnerOrderApplier>();

        return services;
    }

    [Fact]
    public void AddEntityUpdateVerb_RegistersOneResolvableHandlerPerVerb()
    {
        var services = BaseServices();
        services.AddEntityUpdateVerb<OrderAggregate, OrderDTO, int, OrderUpdateRequest, IncreaseOrderApplier>();
        services.AddEntityUpdateVerb<OrderAggregate, OrderDTO, int, OrderUpdateRequest, DecreaseOrderApplier>();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICommandHandler<
                UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>, Result<OrderDTO>>>()
            .Should().BeOfType<UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest, IncreaseOrderApplier>>();
        provider.GetRequiredService<ICommandHandler<
                UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, DecreaseOrderApplier>, Result<OrderDTO>>>()
            .Should().BeOfType<UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest, DecreaseOrderApplier>>();
    }

    [Fact]
    public void AddEntityUpdateVerb_BridgesTheVerbCommandToItsRequestValidators()
    {
        var services = BaseServices();
        services.AddEntityUpdateVerb<OrderAggregate, OrderDTO, int, OrderUpdateRequest, IncreaseOrderApplier>();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IValidator<
                UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>>>()
            .Should().BeOfType<CommandRequestValidator<
                UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>, OrderUpdateRequest>>();
    }

    [Fact]
    public void AddEntityUpdate_RegistersAResolvableHandlerForTheDerivedCommand()
    {
        var services = BaseServices();
        services.AddEntityUpdate<RenameOrderByOwnerCommand, OrderAggregate, OrderDTO, int, OrderUpdateRequest>();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICommandHandler<RenameOrderByOwnerCommand, Result<OrderDTO>>>()
            .Should().BeOfType<UpdateEntityCommandHandler<
                RenameOrderByOwnerCommand, OrderAggregate, OrderDTO, int, OrderUpdateRequest>>();
    }

    [Fact]
    public void AddEntityUpdate_BridgesTheDerivedCommandToItsRequestValidators()
    {
        var services = BaseServices();
        services.AddEntityUpdate<RenameOrderByOwnerCommand, OrderAggregate, OrderDTO, int, OrderUpdateRequest>();

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IValidator<RenameOrderByOwnerCommand>>()
            .Should().BeOfType<CommandRequestValidator<RenameOrderByOwnerCommand, OrderUpdateRequest>>();
    }

    // TryAdd semantics, exactly like AddEntityCrud: a verb that outgrows the generic handler
    // registers its own first and keeps the registration line for the others.
    [Fact]
    public void AddEntityUpdateVerb_LeavesAnAlreadyRegisteredHandlerAlone()
    {
        var services = BaseServices();
        services.AddScoped<
            ICommandHandler<UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>, Result<OrderDTO>>,
            CustomIncreaseOrderHandler>();

        services.AddEntityUpdateVerb<OrderAggregate, OrderDTO, int, OrderUpdateRequest, IncreaseOrderApplier>();

        ServiceDescriptor descriptor = services.Single(d => d.ServiceType == typeof(
            ICommandHandler<UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>, Result<OrderDTO>>));
        descriptor.ImplementationType.Should().Be<CustomIncreaseOrderHandler>();
    }

    [Fact]
    public void AddEntityUpdateVerb_AfterTheDecoratorsHaveRun_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplicationDecorators();

        Action act = () => services
            .AddEntityUpdateVerb<OrderAggregate, OrderDTO, int, OrderUpdateRequest, IncreaseOrderApplier>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddEntityUpdateVerb*");
    }

    [Fact]
    public void AddEntityUpdate_AfterTheDecoratorsHaveRun_Throws()
    {
        var services = new ServiceCollection();
        services.AddApplicationDecorators();

        Action act = () => services
            .AddEntityUpdate<RenameOrderByOwnerCommand, OrderAggregate, OrderDTO, int, OrderUpdateRequest>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddEntityUpdate*");
    }
}

// ── Test doubles (public so Moq's DynamicProxy can see them) ─────────────────
public sealed record RenameOrderResult(string Name, string? PreviousName);

public sealed record RenameOrderByOwnerCommand(
    int Id,
    OrderUpdateRequest Request,
    bool CallerIsOwner,
    byte[] RowVersion)
    : UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(Id, Request, RowVersion);

public sealed class TestRenameOrderPayloadHandler(IUnitOfWork unitOfWork, List<string> calls)
    : MutateEntityPayloadHandlerBase<RenameOrderCommand, OrderAggregate, int, RenameOrderResult>(unitOfWork)
{
    public const string PreviousNameKey = "previousName";

    protected override int EntityId(RenameOrderCommand command) => command.Id;

    protected override byte[]? RowVersion(RenameOrderCommand command) => command.RowVersion;

    protected override Task<Result> MutateAsync(
        OrderAggregate entity,
        RenameOrderCommand command,
        MutationContext context,
        CancellationToken cancellationToken)
    {
        // Derived while the aggregate is loaded and before it is mutated.
        context.Set(PreviousNameKey, entity.Name);

        if (string.Equals(entity.Name, command.Name, StringComparison.Ordinal))
        {
            context.SkipSave();
            return Task.FromResult(Result.Success());
        }

        return Task.FromResult(entity.Rename(command.Name));
    }

    protected override void LogMutated(OrderAggregate entity, RenameOrderCommand command, MutationContext context) =>
        calls.Add("log:" + context.GetOrDefault<string>(PreviousNameKey));

    protected override Task OnMutatedAsync(
        OrderAggregate entity,
        RenameOrderCommand command,
        MutationContext context,
        CancellationToken cancellationToken)
    {
        calls.Add("mutated:" + context.GetOrDefault<string>(PreviousNameKey));
        return Task.CompletedTask;
    }

    protected override Result<RenameOrderResult> BuildResult(
        OrderAggregate entity,
        RenameOrderCommand command,
        MutationContext context) =>
        Result.Success(new RenameOrderResult(entity.Name, context.GetOrDefault<string>(PreviousNameKey)));
}

public sealed class TestSkippingRenameHandler(IUnitOfWork unitOfWork, List<string> calls)
    : MutateEntityHandlerBase<RenameOrderCommand, OrderAggregate, int>(unitOfWork)
{
    protected override int EntityId(RenameOrderCommand command) => command.Id;

    protected override Task<Result> MutateAsync(
        OrderAggregate entity,
        RenameOrderCommand command,
        MutationContext context,
        CancellationToken cancellationToken)
    {
        context.SkipSave();
        return Task.FromResult(Result.Success());
    }

    protected override void LogMutated(OrderAggregate entity, RenameOrderCommand command) => calls.Add("log");
}

public sealed class TestNoMutationHandler(IUnitOfWork unitOfWork)
    : MutateEntityHandlerBase<RenameOrderCommand, OrderAggregate, int>(unitOfWork)
{
    protected override int EntityId(RenameOrderCommand command) => command.Id;
}

public sealed class TestRetryingRenameHandler(IUnitOfWork unitOfWork, IUnitOfWork retryUnitOfWork)
    : MutateEntityHandlerBase<RenameOrderCommand, OrderAggregate, int>(unitOfWork)
{
    public int Attempts { get; private set; }

    protected override int EntityId(RenameOrderCommand command) => command.Id;

    protected override Task<Result> MutateAsync(
        OrderAggregate entity,
        RenameOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(entity.Rename(command.Name));

    public override async Task<Result> HandleAsync(
        RenameOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var attemptUnitOfWork = attempt == 0 ? UnitOfWork : retryUnitOfWork;
            Attempts++;

            try
            {
                var result = await MutateCoreAsync(attemptUnitOfWork, command, cancellationToken)
                    .ConfigureAwait(false);

                return result.IsFailure ? Result.Failure(result.Errors) : Result.Success();
            }
            catch (InvalidOperationException) when (attempt == 0)
            {
                // A unique-key collision: retry the whole workflow in a fresh scope.
            }
        }

        return Result.Failure(Error.Conflict("Order.RetriesExhausted", "The write kept colliding."));
    }
}

public sealed class TestDeleteOrderHandler(IUnitOfWork unitOfWork, Result refusal, List<string> calls)
    : DeleteEntityHandler<OrderAggregate, int>(unitOfWork)
{
    protected override IEnumerable<string> Includes => [nameof(OrderAggregate.Lines)];

    protected override Task<Result> OnDeletingAsync(
        OrderAggregate entity,
        DeleteEntityCommand<OrderAggregate, int> command,
        CancellationToken cancellationToken)
    {
        calls.Add("deleting");
        return Task.FromResult(refusal);
    }

    protected override void LogDeleted(OrderAggregate entity, DeleteEntityCommand<OrderAggregate, int> command) =>
        calls.Add("log");
}

public sealed class IncreaseOrderApplier : IEntityUpdateApplier<OrderAggregate, OrderUpdateRequest, int>
{
    public int Calls { get; private set; }

    public Task<Result> ApplyAsync(
        OrderAggregate entity,
        OrderUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        Calls++;
        return Task.FromResult(entity.Rename("increased"));
    }
}

public sealed class DecreaseOrderApplier : IEntityUpdateApplier<OrderAggregate, OrderUpdateRequest, int>
{
    public int Calls { get; private set; }

    public Task<Result> ApplyAsync(
        OrderAggregate entity,
        OrderUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        Calls++;
        return Task.FromResult(entity.Rename("decreased"));
    }
}

public sealed class OwnerOrderApplier
    : IEntityUpdateCommandApplier<OrderAggregate, OrderUpdateRequest, int, RenameOrderByOwnerCommand>
{
    public const string NoOpName = "unchanged";

    public bool? LastCallerIsOwner { get; private set; }

    public Task<Result> ApplyAsync(
        OrderAggregate entity,
        RenameOrderByOwnerCommand command,
        MutationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);

        LastCallerIsOwner = command.CallerIsOwner;

        if (!command.CallerIsOwner)
            return Task.FromResult(Result.Failure(Error.Forbidden("Order.NotTheOwner", "Only the owner may rename.")));

        if (string.Equals(command.Request.Name, NoOpName, StringComparison.Ordinal))
        {
            context.SkipSave();
            return Task.FromResult(Result.Success());
        }

        return Task.FromResult(entity.Rename(command.Request.Name));
    }
}

public sealed class CustomIncreaseOrderHandler
    : ICommandHandler<UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier>, Result<OrderDTO>>
{
    public Task<Result<OrderDTO>> HandleAsync(
        UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int, IncreaseOrderApplier> command,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success(new OrderDTO { Id = 1 }));
}
