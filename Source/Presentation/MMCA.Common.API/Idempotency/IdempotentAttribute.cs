using Microsoft.AspNetCore.Mvc;

namespace MMCA.Common.API.Idempotency;

/// <summary>
/// Marks a controller action as idempotent. When applied, the <see cref="IdempotencyFilter"/> action filter
/// deduplicates requests based on the <c>Idempotency-Key</c> request header. If the header is absent,
/// the action executes normally without deduplication.
/// </summary>
/// <remarks>
/// Uses <see cref="ServiceFilterAttribute"/> so that <see cref="IdempotencyFilter"/> is resolved from DI;
/// the filter reads <see cref="Application.Interfaces.ICacheService"/> from the request's services.
/// The filter must be registered in DI (see <see cref="DependencyInjection"/>).
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute() : ServiceFilterAttribute(typeof(IdempotencyFilter));
