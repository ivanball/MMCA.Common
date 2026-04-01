using AwesomeAssertions;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure;
using MMCA.Common.Application.Services.Navigation;
using MMCA.Common.Domain.Entities;
using Moq;

namespace MMCA.Common.Application.Tests.Services;

public sealed class DeclarativeNavigationPopulatorTests
{
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    // ── Empty entities ──
    [Fact]
    public async Task PopulateAsync_WithEmptyEntities_DoesNotCallDescriptors()
    {
        var descriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        descriptor.Setup(d => d.PropertyName).Returns("Nav");
        descriptor.Setup(d => d.RequiresChildren).Returns(false);

        var sut = new DeclarativeNavigationPopulator<NavigationPopulatorStubEntity>(
            _unitOfWork.Object,
            [descriptor.Object]);

        var metadata = new NavigationMetadata();
        AddUnsupportedInclude(metadata, "Nav");

        await sut.PopulateAsync([], metadata, includeFKs: true, includeChildren: false, CancellationToken.None);

        descriptor.Verify(
            d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── No unsupported includes ──
    [Fact]
    public async Task PopulateAsync_WithNoUnsupportedIncludes_DoesNotCallDescriptors()
    {
        var descriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        descriptor.Setup(d => d.PropertyName).Returns("Nav");
        descriptor.Setup(d => d.RequiresChildren).Returns(false);

        var sut = new DeclarativeNavigationPopulator<NavigationPopulatorStubEntity>(
            _unitOfWork.Object,
            [descriptor.Object]);

        IReadOnlyCollection<NavigationPopulatorStubEntity> entities = [new NavigationPopulatorStubEntity { Id = 1 }];
        var metadata = new NavigationMetadata();

        await sut.PopulateAsync(entities, metadata, includeFKs: true, includeChildren: true, CancellationToken.None);

        descriptor.Verify(
            d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── FK descriptor called when includeFKs is true ──
    [Fact]
    public async Task PopulateAsync_WithFKDescriptorAndIncludeFKs_CallsLoadAsync()
    {
        var descriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        descriptor.Setup(d => d.PropertyName).Returns("Category");
        descriptor.Setup(d => d.RequiresChildren).Returns(false);
        descriptor.Setup(d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), _unitOfWork.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new DeclarativeNavigationPopulator<NavigationPopulatorStubEntity>(
            _unitOfWork.Object,
            [descriptor.Object]);

        IReadOnlyCollection<NavigationPopulatorStubEntity> entities = [new NavigationPopulatorStubEntity { Id = 1 }];
        var metadata = new NavigationMetadata();
        AddUnsupportedInclude(metadata, "Category");

        await sut.PopulateAsync(entities, metadata, includeFKs: true, includeChildren: false, CancellationToken.None);

        descriptor.Verify(
            d => d.LoadAsync(entities, _unitOfWork.Object, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── FK descriptor NOT called when includeFKs is false ──
    [Fact]
    public async Task PopulateAsync_WithFKDescriptorAndIncludeFKsFalse_DoesNotCallLoadAsync()
    {
        var descriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        descriptor.Setup(d => d.PropertyName).Returns("Category");
        descriptor.Setup(d => d.RequiresChildren).Returns(false);

        var sut = new DeclarativeNavigationPopulator<NavigationPopulatorStubEntity>(
            _unitOfWork.Object,
            [descriptor.Object]);

        IReadOnlyCollection<NavigationPopulatorStubEntity> entities = [new NavigationPopulatorStubEntity { Id = 1 }];
        var metadata = new NavigationMetadata();
        AddUnsupportedInclude(metadata, "Category");

        await sut.PopulateAsync(entities, metadata, includeFKs: false, includeChildren: true, CancellationToken.None);

        descriptor.Verify(
            d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Child descriptor called when includeChildren is true ──
    [Fact]
    public async Task PopulateAsync_WithChildDescriptorAndIncludeChildren_CallsLoadAsync()
    {
        var descriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        descriptor.Setup(d => d.PropertyName).Returns("Items");
        descriptor.Setup(d => d.RequiresChildren).Returns(true);
        descriptor.Setup(d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), _unitOfWork.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new DeclarativeNavigationPopulator<NavigationPopulatorStubEntity>(
            _unitOfWork.Object,
            [descriptor.Object]);

        IReadOnlyCollection<NavigationPopulatorStubEntity> entities = [new NavigationPopulatorStubEntity { Id = 1 }];
        var metadata = new NavigationMetadata();
        AddUnsupportedInclude(metadata, "Items");

        await sut.PopulateAsync(entities, metadata, includeFKs: false, includeChildren: true, CancellationToken.None);

        descriptor.Verify(
            d => d.LoadAsync(entities, _unitOfWork.Object, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Descriptor skipped when property not in unsupported list ──
    [Fact]
    public async Task PopulateAsync_WhenPropertyNotInUnsupportedList_SkipsDescriptor()
    {
        var descriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        descriptor.Setup(d => d.PropertyName).Returns("Category");
        descriptor.Setup(d => d.RequiresChildren).Returns(false);

        var sut = new DeclarativeNavigationPopulator<NavigationPopulatorStubEntity>(
            _unitOfWork.Object,
            [descriptor.Object]);

        IReadOnlyCollection<NavigationPopulatorStubEntity> entities = [new NavigationPopulatorStubEntity { Id = 1 }];
        var metadata = new NavigationMetadata();
        AddUnsupportedInclude(metadata, "OtherProperty");

        await sut.PopulateAsync(entities, metadata, includeFKs: true, includeChildren: true, CancellationToken.None);

        descriptor.Verify(
            d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Multiple descriptors ──
    [Fact]
    public async Task PopulateAsync_WithMultipleDescriptors_CallsOnlyMatchingOnes()
    {
        var fkDescriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        fkDescriptor.Setup(d => d.PropertyName).Returns("Category");
        fkDescriptor.Setup(d => d.RequiresChildren).Returns(false);
        fkDescriptor.Setup(d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), _unitOfWork.Object, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var childDescriptor = new Mock<INavigationDescriptor<NavigationPopulatorStubEntity>>();
        childDescriptor.Setup(d => d.PropertyName).Returns("Items");
        childDescriptor.Setup(d => d.RequiresChildren).Returns(true);

        var sut = new DeclarativeNavigationPopulator<NavigationPopulatorStubEntity>(
            _unitOfWork.Object,
            [fkDescriptor.Object, childDescriptor.Object]);

        IReadOnlyCollection<NavigationPopulatorStubEntity> entities = [new NavigationPopulatorStubEntity { Id = 1 }];
        var metadata = new NavigationMetadata();
        AddUnsupportedInclude(metadata, "Category");
        AddUnsupportedInclude(metadata, "Items");

        await sut.PopulateAsync(entities, metadata, includeFKs: true, includeChildren: false, CancellationToken.None);

        fkDescriptor.Verify(
            d => d.LoadAsync(entities, _unitOfWork.Object, It.IsAny<CancellationToken>()),
            Times.Once);
        childDescriptor.Verify(
            d => d.LoadAsync(It.IsAny<IReadOnlyCollection<NavigationPopulatorStubEntity>>(), It.IsAny<IUnitOfWork>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Helpers ──
    private static void AddUnsupportedInclude(NavigationMetadata metadata, string propertyName)
    {
        var method = typeof(NavigationMetadata).GetMethod(
            "AddUnsupported",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        method!.Invoke(metadata, [new NavigationPropertyInfo(
            propertyName, NavigationType.ForeignKey,
            typeof(NavigationPopulatorStubEntity), typeof(NavigationPopulatorStubEntity))]);
    }
}

// Must be public for Moq DynamicProxy to create proxies for INavigationDescriptor<T>
public class NavigationPopulatorStubEntity : AuditableBaseEntity<int>;
