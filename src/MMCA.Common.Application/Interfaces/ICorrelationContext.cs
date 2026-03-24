namespace MMCA.Common.Application.Interfaces;

/// <summary>
/// Provides access to the current request's correlation ID for distributed tracing.
/// Set by middleware from the <c>X-Correlation-ID</c> header (or generated automatically)
/// and propagated through the handler pipeline via structured logging scopes.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>Gets the correlation ID for the current scope.</summary>
    string CorrelationId { get; }

    /// <summary>Sets the correlation ID for the current scope.</summary>
    /// <param name="correlationId">The correlation ID to set.</param>
    void SetCorrelationId(string correlationId);
}
