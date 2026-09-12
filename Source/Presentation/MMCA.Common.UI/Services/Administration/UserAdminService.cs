using System.Globalization;
using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// HTTP client for the <c>Admin/Users</c> resource (ADR-116), generic over the app's own
/// administration DTO. Registered by <c>AddUserAdministrationUI</c>.
/// </summary>
/// <remarks>
/// Nothing here throws for a server answer: the response is read back through
/// <see cref="ProblemDetailsResultReader"/> with the API's own <see cref="ErrorType"/> intact, and
/// transport faults become failures through <see cref="HttpResultExecutor"/>. Only the caller's own
/// cancellation still propagates. It follows the shape of the shipped authenticated services rather
/// than the generic entity-service base, because these endpoints are not CRUD over an entity
/// resource.
/// </remarks>
/// <typeparam name="TUserDto">The app's administration-facing user DTO.</typeparam>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class UserAdminService<TUserDto>(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IUserAdminUIService<TUserDto>
{
    private const string Endpoint = "Admin/Users";

    /// <inheritdoc />
    public async Task<Result<(IReadOnlyList<TUserDto> Items, int TotalItems)>> GetPagedAsync(
        int pageNumber,
        int pageSize,
        string? searchTerm,
        string? role,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"pageNumber={pageNumber}"),
            string.Create(CultureInfo.InvariantCulture, $"pageSize={pageSize}"),
        };

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            queryParams.Add($"role={Uri.EscapeDataString(role)}");
        }

        var url = $"{Endpoint}/paged?{string.Join("&", queryParams)}";

        var page = await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<PagedCollectionResult<TUserDto>>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);

        return page.Map<(IReadOnlyList<TUserDto> Items, int TotalItems)>(
            value => ([.. value.Items], value.PaginationMetadata.TotalItemCount));
    }

    /// <inheritdoc />
    public async Task<Result<TUserDto>> GetAsync(
        UserIdentifierType userId,
        CancellationToken cancellationToken = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{userId}");

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<TUserDto>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> LockAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        PostLockChangeAsync(userId, "lock", cancellationToken);

    /// <inheritdoc />
    public Task<Result> UnlockAsync(UserIdentifierType userId, CancellationToken cancellationToken = default) =>
        PostLockChangeAsync(userId, "unlock", cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetRoleAsync(
        UserIdentifierType userId,
        string role,
        CancellationToken cancellationToken = default) =>
        SetRolesAsync(userId, [role], cancellationToken);

    /// <inheritdoc />
    public async Task<Result> SetRolesAsync(
        UserIdentifierType userId,
        IReadOnlyList<string> roles,
        CancellationToken cancellationToken = default)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{userId}/roles");

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.PutAsJsonAsync(
                        new Uri(url, UriKind.Relative),
                        new SetUserRolesRequest(roles),
                        cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
    }

    private async Task<Result> PostLockChangeAsync(
        UserIdentifierType userId,
        string action,
        CancellationToken cancellationToken)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{Endpoint}/{userId}/{action}");

        return await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();

                // No retry: the endpoint is declared non-idempotent, so a retried POST is a second
                // request rather than a replayed response. Both actions are idempotent in the
                // domain, but the client should not decide that on the endpoint's behalf.
                using var response = await httpClient.PostAsync(
                    new Uri(url, UriKind.Relative),
                    content: null,
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
    }
}
