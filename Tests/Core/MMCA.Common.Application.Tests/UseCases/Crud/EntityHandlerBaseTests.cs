using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.UseCases.Crud;
using MMCA.Common.Domain.Entities;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.DTOs;
using Moq;

namespace MMCA.Common.Application.Tests.UseCases.Crud;

// ── CreateEntityHandlerBase ──────────────────────────────────────────────────
public sealed class CreateEntityHandlerBaseTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly Mock<IEntityRequestMapper<OrderAggregate, OrderCreateRequest, int>> _requestMapper = new();
    private readonly Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>> _dtoMapper = new();

    public CreateEntityHandlerBaseTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _dtoMapper.Setup(m => m.MapToDTO(It.IsAny<OrderAggregate>()))
            .Returns((OrderAggregate e) => new OrderDTO { Id = e.Id, Name = e.Name });
    }

    private TestCreateOrderHandler CreateSut(List<string>? calls = null) =>
        new(_unitOfWork.Object, _requestMapper.Object, _dtoMapper.Object, calls ?? []);

    [Fact]
    public async Task HandleAsync_WhenMapperSucceeds_AddsSavesLogsAndReturnsDTO()
    {
        var entity = new OrderAggregate { Id = 7 };
        entity.Rename("first");
        _requestMapper.Setup(m => m.CreateEntityAsync(It.IsAny<OrderCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success(entity));

        var calls = new List<string>();
        var sut = CreateSut(calls);

        var result = await sut.HandleAsync(new OrderCreateRequest("first"));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(7);
        result.Value.Name.Should().Be("first");
        calls.Should().Equal("log", "created");
        _repository.Verify(r => r.AddAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenMapperFails_ShortCircuitsWithoutAddingOrSaving()
    {
        _requestMapper.Setup(m => m.CreateEntityAsync(It.IsAny<OrderCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Failure<OrderAggregate>(Error.InvalidEntityField));

        var sut = CreateSut();

        var result = await sut.HandleAsync(new OrderCreateRequest("first"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Type == ErrorType.Validation);
        _repository.Verify(r => r.AddAsync(It.IsAny<OrderAggregate>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenPrepareFails_NeverReachesTheRequestMapper()
    {
        var sut = new RefusingPrepareCreateOrderHandler(_unitOfWork.Object, _requestMapper.Object, _dtoMapper.Object);

        var result = await sut.HandleAsync(new OrderCreateRequest("first"));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Prepare.Refused");
        _requestMapper.Verify(
            m => m.CreateEntityAsync(It.IsAny<OrderCreateRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_PrepareCanRewriteTheRequestBeforeMapping()
    {
        OrderCreateRequest? mapped = null;
        _requestMapper.Setup(m => m.CreateEntityAsync(It.IsAny<OrderCreateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((OrderCreateRequest r, CancellationToken _) => mapped = r)
            .ReturnsAsync(Result.Success(new OrderAggregate { Id = 1 }));

        var sut = new RewritingPrepareCreateOrderHandler(_unitOfWork.Object, _requestMapper.Object, _dtoMapper.Object);

        await sut.HandleAsync(new OrderCreateRequest("first"));

        mapped!.Name.Should().Be("rewritten");
    }
}

// ── MutateEntityHandlerBase ──────────────────────────────────────────────────
public sealed class MutateEntityHandlerBaseTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>> _dtoMapper = new();
    private readonly List<string> _calls = [];

    public MutateEntityHandlerBaseTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("save"))
            .ReturnsAsync(1);
        _repository.Setup(r => r.SetOriginalRowVersion(It.IsAny<OrderAggregate>(), It.IsAny<byte[]>()))
            .Callback(() => _calls.Add("rowversion"));
        _dtoMapper.Setup(m => m.MapToDTO(It.IsAny<OrderAggregate>()))
            .Returns((OrderAggregate e) => new OrderDTO { Id = e.Id, Name = e.Name });
    }

    private void SetupLoad(OrderAggregate? entity) =>
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

    [Fact]
    public async Task HandleAsync_WhenMutationSucceeds_SavesAndReturnsSuccess()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderHandler(_unitOfWork.Object, _calls);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "renamed", [1, 2, 3]));

        result.IsSuccess.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_StampsTheRowVersionBeforeTheMutationAndSavesAfterIt()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderHandler(_unitOfWork.Object, _calls);

        await sut.HandleAsync(new RenameOrderCommand(3, "renamed", [9]));

        _calls.Should().Equal("rowversion", "mutate", "save", "log", "mutated");
        _repository.Verify(
            r => r.SetOriginalRowVersion(It.IsAny<OrderAggregate>(), It.Is<byte[]>(v => v[0] == 9)),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WhenEntityNotFound_ReturnsNotFoundNamingTheHandlerAndEntity()
    {
        SetupLoad(null);
        var sut = new TestRenameOrderHandler(_unitOfWork.Object, _calls);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "renamed", null));

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Type.Should().Be(ErrorType.NotFound);
        error.Source.Should().Be(nameof(TestRenameOrderHandler));
        error.Target.Should().Be(nameof(OrderAggregate));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_WhenMutationFails_ShortCircuitsWithoutSaving()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderHandler(_unitOfWork.Object, _calls);

        // An empty name is refused by the aggregate.
        var result = await sut.HandleAsync(new RenameOrderCommand(3, "  ", null));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Order.NameRequired");
        // No "rowversion": a command carrying no token states no precondition, so nothing is stamped.
        _calls.Should().Equal("mutate");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_LoadsWithTheDeclaredIncludesAndTracking()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderHandler(_unitOfWork.Object, _calls);

        await sut.HandleAsync(new RenameOrderCommand(3, "renamed", null));

        _repository.Verify(
            r => r.GetByIdAsync(
                3,
                It.Is<IEnumerable<string>>(i => i.Contains(nameof(OrderAggregate.Lines))),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_DTOFlavour_ReturnsTheMappedAggregateOnSuccess()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderDTOHandler(_unitOfWork.Object, _dtoMapper.Object);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, "renamed", null));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Name.Should().Be("renamed");
    }

    [Fact]
    public async Task HandleAsync_DTOFlavour_WhenMutationFails_DoesNotMapOrSave()
    {
        SetupLoad(new OrderAggregate { Id = 3 });
        var sut = new TestRenameOrderDTOHandler(_unitOfWork.Object, _dtoMapper.Object);

        var result = await sut.HandleAsync(new RenameOrderCommand(3, string.Empty, null));

        result.IsFailure.Should().BeTrue();
        _dtoMapper.Verify(m => m.MapToDTO(It.IsAny<OrderAggregate>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

// ── AddChildEntityHandlerBase / RemoveChildEntityHandlerBase ─────────────────
public sealed class ChildEntityHandlerBaseTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();

    public ChildEntityHandlerBaseTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private void SetupLoad(OrderAggregate? entity) =>
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);

    [Fact]
    public async Task AddChild_WhenAggregateAccepts_SavesAndReturnsTheChildDTO()
    {
        SetupLoad(new OrderAggregate { Id = 1 });
        var sut = new TestAddOrderLineHandler(_unitOfWork.Object);

        var result = await sut.HandleAsync(new AddOrderLineCommand(1, 42));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(42);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddChild_WhenDuplicate_ReturnsFailureAndDoesNotSave()
    {
        var order = new OrderAggregate { Id = 1 };
        order.AddLine(42);
        SetupLoad(order);
        var sut = new TestAddOrderLineHandler(_unitOfWork.Object);

        var result = await sut.HandleAsync(new AddOrderLineCommand(1, 42));

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Code == "Order.DuplicateLine");
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddChild_LoadsTheJoinCollectionSoTheDuplicateCheckSeesRealData()
    {
        SetupLoad(new OrderAggregate { Id = 1 });
        var sut = new TestAddOrderLineHandler(_unitOfWork.Object);

        await sut.HandleAsync(new AddOrderLineCommand(1, 42));

        _repository.Verify(
            r => r.GetByIdAsync(
                1,
                It.Is<IEnumerable<string>>(i => i.Contains(nameof(OrderAggregate.Lines))),
                true,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddChild_WhenParentNotFound_ReturnsNotFoundNamingTheHandlerAndParent()
    {
        SetupLoad(null);
        var sut = new TestAddOrderLineHandler(_unitOfWork.Object);

        var result = await sut.HandleAsync(new AddOrderLineCommand(1, 42));

        result.IsFailure.Should().BeTrue();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Type.Should().Be(ErrorType.NotFound);
        error.Source.Should().Be(nameof(TestAddOrderLineHandler));
        error.Target.Should().Be(nameof(OrderAggregate));
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveChild_WhenChildExists_SavesAndReturnsSuccess()
    {
        var order = new OrderAggregate { Id = 1 };
        order.AddLine(42);
        SetupLoad(order);
        var sut = new TestRemoveOrderLineHandler(_unitOfWork.Object);

        var result = await sut.HandleAsync(new RemoveOrderLineCommand(1, 42));

        result.IsSuccess.Should().BeTrue();
        order.Lines.Should().BeEmpty();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveChild_WhenChildMissing_ReturnsFailureAndDoesNotSave()
    {
        SetupLoad(new OrderAggregate { Id = 1 });
        var sut = new TestRemoveOrderLineHandler(_unitOfWork.Object);

        var result = await sut.HandleAsync(new RemoveOrderLineCommand(1, 42));

        result.IsFailure.Should().BeTrue();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}

// ── Test doubles (public so Moq's DynamicProxy can see the aggregate) ────────
public sealed class OrderAggregate : AuditableAggregateRootEntity<int>
{
    private readonly List<OrderLine> _lines = [];

    public IReadOnlyCollection<OrderLine> Lines => _lines;

    public string Name { get; private set; } = string.Empty;

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("Order.NameRequired", "A name is required."));

        Name = name;
        return Result.Success();
    }

    public Result<OrderLine> AddLine(int lineId)
    {
        if (_lines.Exists(l => l.Id == lineId))
            return Result.Failure<OrderLine>(Error.Conflict("Order.DuplicateLine", "The line is already on the order."));

        var line = new OrderLine { Id = lineId };
        _lines.Add(line);
        return Result.Success(line);
    }

    public Result RemoveLine(int lineId)
    {
        var line = _lines.Find(l => l.Id == lineId);
        if (line is null)
            return Result.Failure(Error.NotFoundError("Order.LineNotFound", "The line is not on the order."));

        _lines.Remove(line);
        return Result.Success();
    }
}

public sealed class OrderLine : AuditableBaseEntity<int>;

public sealed record OrderDTO : IBaseDTO<int>
{
    public required int Id { get; init; }

    public string Name { get; init; } = string.Empty;
}

public sealed record OrderLineDTO : IBaseDTO<int>
{
    public required int Id { get; init; }
}

public sealed record OrderCreateRequest(string Name) : ICreateRequest;

public sealed record RenameOrderCommand(int Id, string Name, byte[]? RowVersion);

public sealed record AddOrderLineCommand(int OrderId, int LineId);

public sealed record RemoveOrderLineCommand(int OrderId, int LineId);

public sealed class TestCreateOrderHandler(
    IUnitOfWork unitOfWork,
    IEntityRequestMapper<OrderAggregate, OrderCreateRequest, int> requestMapper,
    IEntityDTOMapper<OrderAggregate, OrderDTO, int> dtoMapper,
    List<string> calls)
    : CreateEntityHandlerBase<OrderCreateRequest, OrderAggregate, int, OrderDTO>(unitOfWork, requestMapper, dtoMapper)
{
    protected override void LogCreated(OrderAggregate entity) => calls.Add("log");

    protected override Task OnCreatedAsync(OrderAggregate entity, CancellationToken cancellationToken)
    {
        calls.Add("created");
        return Task.CompletedTask;
    }
}

public sealed class RefusingPrepareCreateOrderHandler(
    IUnitOfWork unitOfWork,
    IEntityRequestMapper<OrderAggregate, OrderCreateRequest, int> requestMapper,
    IEntityDTOMapper<OrderAggregate, OrderDTO, int> dtoMapper)
    : CreateEntityHandlerBase<OrderCreateRequest, OrderAggregate, int, OrderDTO>(unitOfWork, requestMapper, dtoMapper)
{
    protected override Task<Result<OrderCreateRequest>> PrepareAsync(
        IUnitOfWork attemptUnitOfWork,
        OrderCreateRequest command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure<OrderCreateRequest>(Error.Conflict("Prepare.Refused", "Refused.")));
}

public sealed class RewritingPrepareCreateOrderHandler(
    IUnitOfWork unitOfWork,
    IEntityRequestMapper<OrderAggregate, OrderCreateRequest, int> requestMapper,
    IEntityDTOMapper<OrderAggregate, OrderDTO, int> dtoMapper)
    : CreateEntityHandlerBase<OrderCreateRequest, OrderAggregate, int, OrderDTO>(unitOfWork, requestMapper, dtoMapper)
{
    protected override Task<Result<OrderCreateRequest>> PrepareAsync(
        IUnitOfWork attemptUnitOfWork,
        OrderCreateRequest command,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(command with { Name = "rewritten" }));
}

public sealed class TestRenameOrderHandler(IUnitOfWork unitOfWork, List<string> calls)
    : MutateEntityHandlerBase<RenameOrderCommand, OrderAggregate, int>(unitOfWork)
{
    protected override IEnumerable<string> Includes => [nameof(OrderAggregate.Lines)];

    protected override int EntityId(RenameOrderCommand command) => command.Id;

    protected override byte[]? RowVersion(RenameOrderCommand command) => command.RowVersion;

    protected override Task<Result> MutateAsync(
        OrderAggregate entity,
        RenameOrderCommand command,
        CancellationToken cancellationToken)
    {
        calls.Add("mutate");
        return Task.FromResult(entity.Rename(command.Name));
    }

    protected override void LogMutated(OrderAggregate entity, RenameOrderCommand command) => calls.Add("log");

    protected override Task OnMutatedAsync(
        OrderAggregate entity,
        RenameOrderCommand command,
        CancellationToken cancellationToken)
    {
        calls.Add("mutated");
        return Task.CompletedTask;
    }
}

public sealed class TestRenameOrderDTOHandler(
    IUnitOfWork unitOfWork,
    IEntityDTOMapper<OrderAggregate, OrderDTO, int> dtoMapper)
    : MutateEntityHandlerBase<RenameOrderCommand, OrderAggregate, int, OrderDTO>(unitOfWork, dtoMapper)
{
    protected override int EntityId(RenameOrderCommand command) => command.Id;

    protected override byte[]? RowVersion(RenameOrderCommand command) => command.RowVersion;

    protected override Task<Result> MutateAsync(
        OrderAggregate entity,
        RenameOrderCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(entity.Rename(command.Name));
}

public sealed class TestAddOrderLineHandler(IUnitOfWork unitOfWork)
    : AddChildEntityHandlerBase<AddOrderLineCommand, OrderAggregate, int, OrderLine, OrderLineDTO>(unitOfWork)
{
    protected override IEnumerable<string> Includes => [nameof(OrderAggregate.Lines)];

    protected override int ParentId(AddOrderLineCommand command) => command.OrderId;

    protected override Result<OrderLine> Apply(OrderAggregate parent, AddOrderLineCommand command) =>
        parent.AddLine(command.LineId);

    protected override OrderLineDTO MapChild(OrderLine child) => new() { Id = child.Id };
}

public sealed class TestRemoveOrderLineHandler(IUnitOfWork unitOfWork)
    : RemoveChildEntityHandlerBase<RemoveOrderLineCommand, OrderAggregate, int>(unitOfWork)
{
    protected override IEnumerable<string> Includes => [nameof(OrderAggregate.Lines)];

    protected override int EntityId(RemoveOrderLineCommand command) => command.OrderId;

    protected override Task<Result> MutateAsync(
        OrderAggregate entity,
        RemoveOrderLineCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(entity.RemoveLine(command.LineId));
}
