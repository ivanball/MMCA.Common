using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;
using MMCA.Common.Shared.Legal;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Legal;

/// <summary>
/// HTTP client for the signed-in user's Terms of Service acceptance. Registered by
/// <c>AddUIShared</c>; inert until a host renders <c>TermsAcceptanceGate</c>.
/// </summary>
/// <remarks>
/// Follows the shipped authenticated-service shape (<c>RoleAdminService</c>): the bearer token from
/// <see cref="ITokenStorageService"/>, the idempotent read retried, the response read back through
/// <see cref="ProblemDetailsResultReader"/>, and transport faults turned into failures by
/// <see cref="HttpResultExecutor"/>. The accept POST is sent once (no retry): it carries no
/// idempotency key, and a repeat is harmless but pointless.
/// </remarks>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class LegalAcceptanceUIService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), ILegalAcceptanceUIService
{
    /// <inheritdoc />
    public async Task<Result<LegalAcceptanceDTO>> GetAsync(CancellationToken cancellationToken = default) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(LegalAcceptanceRoutes.Path, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<LegalAcceptanceDTO>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<Result<LegalAcceptanceDTO>> AcceptAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsJsonAsync(
                    new Uri(LegalAcceptanceRoutes.Path, UriKind.Relative),
                    new AcceptLegalTermsRequest(version),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<LegalAcceptanceDTO>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }
}
