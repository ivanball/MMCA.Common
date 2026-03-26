using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Infrastructure.Services;

/// <summary>
/// Scoped service holding the correlation ID for the current request.
/// Defaults to a new GUID if not explicitly set by middleware.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    /// <inheritdoc />
    public string CorrelationId { get; private set; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public void SetCorrelationId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        CorrelationId = correlationId;
    }
}
