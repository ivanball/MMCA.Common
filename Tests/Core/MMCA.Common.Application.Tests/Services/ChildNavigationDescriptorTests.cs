using AwesomeAssertions;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Navigation;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

public sealed class ChildNavigationDescriptorTests
{
    public class OrderEntity : AuditableBaseEntity<int>
    {
        public ICollection<OrderLineEntity> Lines { get; set; } = [];
    }

    public class OrderLineEntity : AuditableBaseEntity<int>
    {
        public int OrderId { get; set; }
    }

    private static ChildNavigationDescriptor<OrderEntity, int, OrderLineEntity, int> CreateDescriptor(
        string propertyName = "Lines",
        Action<OrderEntity, List<OrderLineEntity>>? assignAction = null) =>
        new()
        {
            PropertyName = propertyName,
            ParentKeySelector = p => p.Id,
            ChildForeignKeySelector = c => c.OrderId,
            AssignAction = assignAction ?? ((_, _) => { })
        };

    [Fact]
    public void RequiresChildren_ReturnsTrue() =>
        CreateDescriptor().RequiresChildren.Should().BeTrue();

    [Fact]
    public void PropertyName_ReturnsConfiguredValue()
    {
        ChildNavigationDescriptor<OrderEntity, int, OrderLineEntity, int> descriptor = CreateDescriptor("OrderLines");

        descriptor.PropertyName.Should().Be("OrderLines");
    }

    [Fact]
    public void ImplementsINavigationDescriptor() =>
        CreateDescriptor().Should().BeAssignableTo<INavigationDescriptor<OrderEntity>>();

    [Fact]
    public void ParentKeySelector_ExtractsPrimaryKey()
    {
        var order = new OrderEntity { Id = 42 };
        ChildNavigationDescriptor<OrderEntity, int, OrderLineEntity, int> descriptor = CreateDescriptor();

        int result = descriptor.ParentKeySelector(order);

        result.Should().Be(42);
    }

    [Fact]
    public async Task LoadAsync_DelegatesToNavigationLoader()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepo = new Mock<IReadRepository<OrderLineEntity, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<OrderLineEntity, int>()).Returns(readRepo.Object);

        readRepo
            .Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<System.Linq.Expressions.Expression<Func<OrderLineEntity, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var order = new OrderEntity { Id = 1 };
        IReadOnlyCollection<OrderEntity> entities = [order];
        ChildNavigationDescriptor<OrderEntity, int, OrderLineEntity, int> descriptor = CreateDescriptor();

        await descriptor.LoadAsync(entities, unitOfWork.Object, CancellationToken.None);

        unitOfWork.Verify(u => u.GetReadRepository<OrderLineEntity, int>(), Times.Once);
    }

    [Fact]
    public async Task LoadAsync_WithEmptyParents_DoesNotQueryRepository()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepo = new Mock<IReadRepository<OrderLineEntity, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<OrderLineEntity, int>()).Returns(readRepo.Object);

        IReadOnlyCollection<OrderEntity> entities = [];
        ChildNavigationDescriptor<OrderEntity, int, OrderLineEntity, int> descriptor = CreateDescriptor();

        await descriptor.LoadAsync(entities, unitOfWork.Object, CancellationToken.None);

        readRepo.Verify(
            r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<System.Linq.Expressions.Expression<Func<OrderLineEntity, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadAsync_AssignsLoadedChildrenToParent()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepo = new Mock<IReadRepository<OrderLineEntity, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<OrderLineEntity, int>()).Returns(readRepo.Object);

        var orderLine = new OrderLineEntity { Id = 100, OrderId = 1 };
        readRepo
            .Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<System.Linq.Expressions.Expression<Func<OrderLineEntity, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([orderLine]);

        var order = new OrderEntity { Id = 1 };
        IReadOnlyCollection<OrderEntity> entities = [order];

        List<OrderLineEntity>? assignedChildren = null;
        ChildNavigationDescriptor<OrderEntity, int, OrderLineEntity, int> descriptor = CreateDescriptor(
            assignAction: (_, list) => assignedChildren = list);

        await descriptor.LoadAsync(entities, unitOfWork.Object, CancellationToken.None);

        assignedChildren.Should().NotBeNull();
        assignedChildren.Should().ContainSingle().Which.OrderId.Should().Be(1);
    }

    [Fact]
    public async Task LoadAsync_WithMultipleParents_LoadsChildrenForAll()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        var readRepo = new Mock<IReadRepository<OrderLineEntity, int>>();
        unitOfWork.Setup(u => u.GetReadRepository<OrderLineEntity, int>()).Returns(readRepo.Object);

        var line1 = new OrderLineEntity { Id = 100, OrderId = 1 };
        var line2 = new OrderLineEntity { Id = 101, OrderId = 2 };
        readRepo
            .Setup(r => r.GetAllAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<System.Linq.Expressions.Expression<Func<OrderLineEntity, bool>>>(),
                null,
                null,
                false,
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([line1, line2]);

        var order1 = new OrderEntity { Id = 1 };
        var order2 = new OrderEntity { Id = 2 };
        IReadOnlyCollection<OrderEntity> entities = [order1, order2];

        var allAssigned = new List<List<OrderLineEntity>>();
        ChildNavigationDescriptor<OrderEntity, int, OrderLineEntity, int> descriptor = new()
        {
            PropertyName = "Lines",
            ParentKeySelector = p => p.Id,
            ChildForeignKeySelector = c => c.OrderId,
            AssignAction = (_, list) => allAssigned.Add(list)
        };

        await descriptor.LoadAsync(entities, unitOfWork.Object, CancellationToken.None);

        allAssigned.Should().HaveCount(2);
    }
}
