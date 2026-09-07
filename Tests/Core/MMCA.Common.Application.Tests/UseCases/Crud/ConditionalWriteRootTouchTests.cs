using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure.Persistence;
using MMCA.Common.Application.Interfaces.Mapping;
using MMCA.Common.Application.UseCases.Crud;
using MMCA.Common.Shared.Abstractions;
using Moq;

namespace MMCA.Common.Application.Tests.UseCases.Crud;

/// <summary>
/// SEC-Common-77: an If-Match precondition must be evaluated even when the applier changed only
/// child rows, which leaves the aggregate root Unchanged and used to emit no root UPDATE, and so no
/// concurrency predicate at all. The handler now touches the root on every conditional write.
/// </summary>
public sealed class ConditionalWriteRootTouchTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IRepository<OrderAggregate, int>> _repository = new();
    private readonly Mock<IEntityUpdateApplier<OrderAggregate, OrderUpdateRequest, int>> _applier = new();
    private readonly Mock<IEntityDTOMapper<OrderAggregate, OrderDTO, int>> _dtoMapper = new();

    public ConditionalWriteRootTouchTests()
    {
        _unitOfWork.Setup(u => u.GetRepository<OrderAggregate, int>()).Returns(_repository.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _dtoMapper.Setup(m => m.MapToDTO(It.IsAny<OrderAggregate>()))
            .Returns((OrderAggregate e) => new OrderDTO { Id = e.Id, Name = e.Name });
        _repository.Setup(r => r.GetByIdAsync(
                It.IsAny<int>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderAggregate { Id = 3 });
        _applier.Setup(a => a.ApplyAsync(
                It.IsAny<OrderAggregate>(),
                It.IsAny<OrderUpdateRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Success());
    }

    private UpdateEntityHandler<OrderAggregate, OrderDTO, int, OrderUpdateRequest> CreateSut() =>
        new(_unitOfWork.Object, _applier.Object, _dtoMapper.Object);

    [Fact]
    public async Task ConditionalWrite_TouchesTheRootBeforeSaving()
    {
        var sut = CreateSut();

        var result = await sut.HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(
            3,
            new OrderUpdateRequest("renamed"),
            [9, 8, 7]));

        result.IsSuccess.Should().BeTrue();
        _repository.Verify(r => r.TouchConcurrencyToken(It.Is<OrderAggregate>(e => e.Id == 3)), Times.Once);
    }

    [Fact]
    public async Task UnconditionalWrite_DoesNotTouchTheRoot()
    {
        var sut = CreateSut();

        await sut.HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(
            3,
            new OrderUpdateRequest("renamed"),
            RowVersion: []));

        _repository.Verify(r => r.TouchConcurrencyToken(It.IsAny<OrderAggregate>()), Times.Never);
    }

    [Fact]
    public async Task ConditionalWrite_TouchesTheRootBeforeSaveChanges()
    {
        var order = new List<string>();
        _repository.Setup(r => r.TouchConcurrencyToken(It.IsAny<OrderAggregate>()))
            .Callback(() => order.Add("touch"));
        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("save"))
            .ReturnsAsync(1);

        var sut = CreateSut();

        await sut.HandleAsync(new UpdateEntityCommand<OrderAggregate, OrderUpdateRequest, int>(
            3,
            new OrderUpdateRequest("renamed"),
            [1]));

        order.Should().Equal("touch", "save");
    }
}
