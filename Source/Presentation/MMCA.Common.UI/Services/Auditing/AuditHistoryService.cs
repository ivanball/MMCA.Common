using System.Globalization;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auditing;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Auditing;

/// <summary>
/// HTTP client for an audit-history endpoint. Registered by <c>AddAuditHistoryUI</c>.
/// </summary>
/// <remarks>
/// The <c>FeatureFlagAdminService</c> shape: nothing here throws for a server answer, a non-2xx
/// response is read back through <see cref="ProblemDetailsResultReader"/> with the API's own
/// <see cref="ErrorType"/> intact, transport faults become failures through
/// <see cref="HttpResultExecutor"/>, and the GET retries through the shared retry policy.
/// </remarks>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class AuditHistoryService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IAuditHistoryUIService
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<AuditHistoryEntryDTO>>> GetPageAsync(
        string routePrefix,
        string entityAlias,
        string entityKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{routePrefix.TrimEnd('/')}/{Uri.EscapeDataString(entityAlias)}/{Uri.EscapeDataString(entityKey)}?page={page}&pageSize={pageSize}");

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<IReadOnlyList<AuditHistoryEntryDTO>>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }
}
