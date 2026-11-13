using MMCA.Common.Domain.DomainEvents;

namespace MMCA.Common.Testing.Aspire.AppHostTests.SampleService;

/// <summary>
/// The integration event <c>POST /sample/outbox</c> parks in the outbox during a broker outage. It
/// carries nothing: the outage tests only count whether a row is pending and whether it drains.
/// </summary>
public sealed record SampleOutageProbed : BaseIntegrationEvent;
