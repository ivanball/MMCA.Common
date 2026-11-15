using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Architecture.Tests.RuleShapeFixtures.DomainEvents;

/// <summary>Compliant: sealed and declared in a <c>*.DomainEvents</c> namespace.</summary>
public sealed record ShapeOrderPlaced : BaseDomainEvent;

/// <summary>Violation: in a <c>*.DomainEvents</c> namespace but not sealed.</summary>
public record ShapeOrderDrafted : BaseDomainEvent;
