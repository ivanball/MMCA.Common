using MMCA.Common.Application.Interfaces;

namespace MMCA.Common.Infrastructure.Context;

/// <summary>
/// Scoped service holding the correlation ID for the current request.
/// Defaults to the correlation id of the enclosing background hop when one is being delivered, and
/// otherwise to a new GUID, until middleware or a restore sets it explicitly.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    /// <inheritdoc />
    public string CorrelationId { get; private set; } = AmbientOrigin.Current?.CorrelationId ?? Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public void SetCorrelationId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        CorrelationId = correlationId;
    }
}
