using System.Diagnostics;
using MMCA.Common.Application.Interfaces;
using MMCA.Common.Application.Interfaces.Infrastructure.Auth;
using MMCA.Common.Infrastructure.Context;

namespace MMCA.Common.Infrastructure.Persistence.InternalCommands;

/// <summary>
/// Snapshots the ambient request context a scheduled command must carry onto its row: the scheduling
/// principal, the tenant and the correlation and trace identifiers, which the
/// <c>InternalCommandProcessor</c> restores around the deferred execution.
/// </summary>
/// <remarks>
/// Scoped, like the three request-scoped services it reads: the snapshot must describe THIS request's
/// caller. Roles are flattened through the shared <see cref="AmbientOrigin"/> helper, which the
/// outbox capture uses too, so the two hops store the same shape.
/// </remarks>
/// <param name="currentUserService">Supplies the principal captured on the row.</param>
/// <param name="tenantContext">Supplies the tenant captured on the row.</param>
/// <param name="correlationContext">Supplies the correlation id captured on the row.</param>
internal sealed class InternalCommandOriginCapture(
    ICurrentUserService currentUserService,
    ITenantContext tenantContext,
    ICorrelationContext correlationContext)
{
    /// <summary>Captures the origin of the work being scheduled right now.</summary>
    /// <returns>The identifiers to store on the row.</returns>
    public InternalCommandOrigin Capture()
    {
        var activity = Activity.Current;

        return new InternalCommandOrigin(
            currentUserService.UserId,
            AmbientOrigin.FlattenRoles(currentUserService.Roles),
            tenantContext.TenantId,
            correlationContext.CorrelationId,
            activity?.TraceId.ToString(),
            activity?.SpanId.ToString());
    }
}
