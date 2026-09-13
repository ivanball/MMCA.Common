using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// HTTP client for the <c>Admin/Roles</c> resource (ADR-116). Registered by
/// <c>AddRoleAdministrationUI</c>.
/// </summary>
/// <remarks>
/// Nothing here throws for a server answer: the response is read back through
/// <see cref="ProblemDetailsResultReader"/> with the API's own <see cref="ErrorType"/> intact, and
/// transport faults become failures through <see cref="HttpResultExecutor"/>. Only the caller's own
/// cancellation still propagates. It follows the shape of <see cref="UserAdminService{TUserDto}"/>,
/// which is the shipped authenticated-service shape rather than the generic entity-service base:
/// these endpoints are not CRUD over an entity resource.
/// </remarks>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class RoleAdminService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IRoleAdminUIService
{
    private const string Endpoint = "Admin/Roles";

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<RolePermissionsResponse>>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<RolePermissionsResponse>>(Endpoint, cancellationToken);

    /// <inheritdoc />
    public Task<Result<RolePermissionsResponse>> GetAsync(
        string role,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        return GetAsync<RolePermissionsResponse>(
            $"{Endpoint}/{Uri.EscapeDataString(role)}",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<PermissionCatalogResponse>> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        GetAsync<PermissionCatalogResponse>($"{Endpoint}/catalog", cancellationToken);

    /// <inheritdoc />
    public async Task<Result<RolePermissionsResponse>> SetStoredPermissionsAsync(
        string role,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(permissions);

        var url = $"{Endpoint}/{Uri.EscapeDataString(role)}/permissions";

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.PutAsJsonAsync(
                        new Uri(url, UriKind.Relative),
                        new SetRolePermissionsRequest(permissions),
                        cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<RolePermissionsResponse>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// The one read shape all three GETs share: authenticate, retry the idempotent request, and read
    /// the body (or the Problem Details) back as a <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="TResponse">The expected response body.</typeparam>
    /// <param name="url">The relative URL to read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body, or the API's own refusal.</returns>
    private async Task<Result<TResponse>> GetAsync<TResponse>(string url, CancellationToken cancellationToken) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<TResponse>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
}
