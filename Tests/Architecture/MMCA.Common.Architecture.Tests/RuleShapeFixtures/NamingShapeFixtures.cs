using System.Linq.Expressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MMCA.Common.Architecture.Tests.ControllerConventionFixtures;
using MMCA.Common.Domain.DomainEvents;
using MMCA.Common.Domain.Specifications;
using MMCA.Common.Shared.DTOs;

namespace MMCA.Common.Architecture.Tests.RuleShapeFixtures;

/// <summary>Compliant validator: "Validator" suffix.</summary>
public sealed class ShapeOrderValidator : AbstractValidator<ShapePlaceOrderCommand>;

/// <summary>Compliant validator: "Rules" suffix.</summary>
public sealed class ShapeOrderRules : AbstractValidator<ShapeRenameOrderRequest>;

/// <summary>Violation: a validator with neither accepted suffix.</summary>
public sealed class ShapeOrderChecks : AbstractValidator<ShapeCancelOrderMessage>;

/// <summary>Compliant DTO: "DTO" suffix.</summary>
public sealed record ShapeOrderDTO : IBaseDTO<int>
{
    public required int Id { get; init; }
}

/// <summary>Compliant DTO: "Lookup" suffix.</summary>
public sealed record ShapeOrderLookup : IBaseDTO<int>
{
    public required int Id { get; init; }
}

/// <summary>Violation: a DTO contract with neither accepted suffix.</summary>
public sealed record ShapeOrderModel : IBaseDTO<int>
{
    public required int Id { get; init; }
}

/// <summary>Not a DTO contract, so the suffix rule does not read it.</summary>
public sealed record ShapeOrderSummary(int Total);

/// <summary>Violation: sealed, but declared outside a <c>*.DomainEvents</c> namespace.</summary>
public sealed record ShapeOrderMisfiled : BaseDomainEvent;

/// <summary>Compliant: a static invariants class.</summary>
public static class ShapeOrderInvariants
{
    public const int MaxLines = 10;
}

/// <summary>Violation: sealed but instantiable.</summary>
public sealed class ShapeSealedInvariants
{
    public int MaxLines { get; } = 10;
}

/// <summary>Violation: abstract but inheritable.</summary>
public abstract class ShapeAbstractInvariants
{
    public abstract int MaxLines { get; }
}

/// <summary>Not a class, so the invariants rule does not read it despite the suffix.</summary>
public interface IShapeInvariants
{
    int MaxLines { get; }
}

/// <summary>Compliant EF configuration: sealed, "Configuration" suffix.</summary>
public sealed class ShapeOrderConfiguration : IEntityTypeConfiguration<ControllerConventionFixtureEntity>
{
    public void Configure(EntityTypeBuilder<ControllerConventionFixtureEntity> builder) =>
        builder.HasKey(e => e.Id);
}

/// <summary>Violation: an EF configuration that is not sealed.</summary>
public class ShapeOpenOrderConfiguration : IEntityTypeConfiguration<ControllerConventionFixtureEntity>
{
    public void Configure(EntityTypeBuilder<ControllerConventionFixtureEntity> builder) =>
        builder.HasKey(e => e.Id);
}

/// <summary>Violation: a sealed EF configuration without the "Configuration" suffix.</summary>
public sealed class ShapeOrderMapping : IEntityTypeConfiguration<ControllerConventionFixtureEntity>
{
    public void Configure(EntityTypeBuilder<ControllerConventionFixtureEntity> builder) =>
        builder.HasKey(e => e.Id);
}

/// <summary>Compliant specification: sealed, "Specification" suffix.</summary>
public sealed class ShapeActiveOrdersSpecification : Specification<ControllerConventionFixtureEntity, int>
{
    public override Expression<Func<ControllerConventionFixtureEntity, bool>> Criteria => e => e.Id > 0;
}

/// <summary>Violation: a specification that is not sealed.</summary>
public class ShapeOpenOrdersSpecification : Specification<ControllerConventionFixtureEntity, int>
{
    public override Expression<Func<ControllerConventionFixtureEntity, bool>> Criteria => e => e.Id > 0;
}

/// <summary>Violation: a sealed specification without the "Specification" suffix.</summary>
public sealed class ShapeActiveOrdersFilter : Specification<ControllerConventionFixtureEntity, int>
{
    public override Expression<Func<ControllerConventionFixtureEntity, bool>> Criteria => e => e.Id > 0;
}

/// <summary>
/// Stand-ins for the framework repository contracts. The rule matches the generic interface by its
/// simple name only (no compile reference to the framework), so a same-named contract is exactly
/// what it keys on, without implementing the framework's full repository surface.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The identifier type.</typeparam>
public interface IRepository<TEntity, in TIdentifierType>
{
    TEntity? Find(TIdentifierType id);
}

/// <summary>Stand-in read repository contract (see <see cref="IRepository{TEntity, TIdentifierType}"/>).</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The identifier type.</typeparam>
public interface IReadRepository<TEntity, in TIdentifierType>
{
    TEntity? Find(TIdentifierType id);
}

/// <summary>Stand-in write repository contract (see <see cref="IRepository{TEntity, TIdentifierType}"/>).</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TIdentifierType">The identifier type.</typeparam>
public interface IWriteRepository<TEntity, in TIdentifierType>
{
    TEntity? Find(TIdentifierType id);
}

/// <summary>Compliant: "Repository" suffix.</summary>
public sealed class ShapeOrderRepository : IRepository<ShapeOrderSummary, int>
{
    public ShapeOrderSummary? Find(int id) => null;
}

/// <summary>Compliant: a repository decorator, "Decorator" suffix.</summary>
public sealed class ShapeOrderCachingDecorator : IReadRepository<ShapeOrderSummary, int>
{
    public ShapeOrderSummary? Find(int id) => null;
}

/// <summary>Violation through IRepository: no accepted suffix.</summary>
public sealed class ShapeOrderVault : IRepository<ShapeOrderSummary, int>
{
    public ShapeOrderSummary? Find(int id) => null;
}

/// <summary>Violation through IReadRepository: no accepted suffix.</summary>
public sealed class ShapeOrderReader : IReadRepository<ShapeOrderSummary, int>
{
    public ShapeOrderSummary? Find(int id) => null;
}

/// <summary>Violation through IWriteRepository: no accepted suffix.</summary>
public sealed class ShapeOrderStore : IWriteRepository<ShapeOrderSummary, int>
{
    public ShapeOrderSummary? Find(int id) => null;
}

/// <summary>Implements no repository contract, so the suffix rule does not read it.</summary>
public sealed class ShapeOrderGateway
{
    public ShapeOrderSummary? Find(int id) => new(id);
}
