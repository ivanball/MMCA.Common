using AwesomeAssertions;
using MMCA.Common.Shared.Identifiers;
using Riok.Mapperly.Abstractions;

namespace MMCA.Common.Application.Tests.Mapping;

/// <summary>
/// Mapperly and strongly typed identifiers (ADR-115), both ways round.
/// <para>
/// The normal case needs nothing: a DTO implements <c>IBaseDTO&lt;TIdentifierType&gt;</c> over the
/// SAME identifier type its entity uses, so a wrapper maps to itself as a plain assignment. The
/// second case is a contract that deliberately keeps primitives on the wire, which Mapperly cannot
/// infer, and which <see cref="StronglyTypedIdMappings{TSelf, TValue}"/> answers without the
/// framework taking a Mapperly dependency: the mapper reaches it through <c>[UseStaticMapper]</c>,
/// which discovers plain public static methods by signature.
/// </para>
/// <para>
/// The mappers and their models are declared at namespace level rather than nested, because
/// Mapperly emits each mapper as a partial class and would otherwise require the test class itself
/// to be partial.
/// </para>
/// </summary>
public sealed class StronglyTypedIdMapperTests
{
    [Fact]
    public void WrapperToWrapper_MapsBothWaysWithNoHelp()
    {
        var mapper = new WrapperMapper();
        var entity = new MappedOrder { Id = MappedOrderId.From(7), ParentId = MappedOrderId.From(3), Code = "ORD-7" };

        var dto = mapper.MapToDTO(entity);
        dto.Id.Should().Be(MappedOrderId.From(7));
        dto.ParentId.Should().Be(MappedOrderId.From(3));

        var round = mapper.MapToEntity(dto);
        round.Id.Should().Be(entity.Id);
        round.ParentId.Should().Be(entity.ParentId);
        round.Code.Should().Be("ORD-7");
    }

    [Fact]
    public void WrapperToPrimitive_MapsBothWaysThroughTheShippedStaticMapper()
    {
        var mapper = new PrimitiveMapper();
        var entity = new MappedOrder { Id = MappedOrderId.From(7), ParentId = MappedOrderId.From(3), Code = "ORD-7" };

        var dto = mapper.MapToDTO(entity);
        dto.Id.Should().Be(7);
        dto.ParentId.Should().Be(3);

        var round = mapper.MapToEntity(dto);
        round.Id.Should().Be(MappedOrderId.From(7));
        round.ParentId.Should().Be(MappedOrderId.From(3));
    }

    [Fact]
    public void OptionalIdentifier_StaysUnsetInBothDirections()
    {
        var mapper = new PrimitiveMapper();

        mapper.MapToDTO(new MappedOrder { Id = MappedOrderId.From(1), ParentId = null })
            .ParentId.Should().BeNull();
        mapper.MapToEntity(new OrderPrimitiveDTO { Id = 1, ParentId = null })
            .ParentId.Should().BeNull();
    }
}

/// <summary>The canonical two-line declaration.</summary>
public readonly record struct MappedOrderId(int Value) : IStronglyTypedId<MappedOrderId, int>
{
    /// <summary>Wraps a primitive order key.</summary>
    public static MappedOrderId From(int value) => new(value);
}

/// <summary>The entity, keyed by a wrapper and holding an optional one.</summary>
public sealed class MappedOrder
{
    public MappedOrderId Id { get; set; }

    public MappedOrderId? ParentId { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>A contract that keeps the wrapper, which is the framework's own DTO shape.</summary>
public sealed class OrderWrapperDTO
{
    public MappedOrderId Id { get; set; }

    public MappedOrderId? ParentId { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>A contract that deliberately keeps primitives on the wire.</summary>
public sealed class OrderPrimitiveDTO
{
    public int Id { get; set; }

    public int? ParentId { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>Wrapper to wrapper: Mapperly needs no help at all.</summary>
[Mapper]
public sealed partial class WrapperMapper
{
    /// <summary>Maps the entity to the wrapper-carrying contract.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The contract.</returns>
    public partial OrderWrapperDTO MapToDTO(MappedOrder entity);

    /// <summary>Maps the wrapper-carrying contract back to the entity.</summary>
    /// <param name="dto">The contract.</param>
    /// <returns>The entity.</returns>
    public partial MappedOrder MapToEntity(OrderWrapperDTO dto);
}

/// <summary>Wrapper to primitive, through the framework's shipped static mapper.</summary>
[Mapper]
[UseStaticMapper(typeof(StronglyTypedIdMappings<MappedOrderId, int>))]
public sealed partial class PrimitiveMapper
{
    /// <summary>Maps the entity to the primitive-carrying contract.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The contract.</returns>
    public partial OrderPrimitiveDTO MapToDTO(MappedOrder entity);

    /// <summary>Maps the primitive-carrying contract back to the entity.</summary>
    /// <param name="dto">The contract.</param>
    /// <returns>The entity.</returns>
    public partial MappedOrder MapToEntity(OrderPrimitiveDTO dto);
}
