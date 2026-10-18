using MMCA.Common.Domain.Attributes;
using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Architecture.Tests.EventNameFixtures.IntegrationEvents;

/// <summary>
/// A deliberately BAD integration event for <c>EventNameConventionTests</c>: it carries no
/// <c>[EventName]</c>, so its outbox rows would store a CLR type name a rename would orphan, and the
/// rule must name it.
/// </summary>
/// <param name="Reference">A plain member.</param>
internal sealed record FixtureEventNameMissingEvent(string Reference) : BaseIntegrationEvent;

/// <summary>
/// The well-formed twin of <see cref="FixtureEventNameMissingEvent"/>: it declares its stable wire
/// identity, so the rule must leave it out of the failure message.
/// </summary>
/// <param name="Reference">A plain member.</param>
[EventName("Fixture.EventNameDeclared.v1")]
internal sealed record FixtureEventNameDeclaredEvent(string Reference) : BaseIntegrationEvent;
