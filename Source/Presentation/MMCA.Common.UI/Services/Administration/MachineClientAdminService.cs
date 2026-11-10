using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.MachineClients;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// HTTP client for the <c>Admin/MachineClients</c> resource (ADR-140). Registered by
/// <c>AddMachineClientAdministrationUI</c>.
/// </summary>
/// <remarks>
/// The <see cref="RoleAdminService"/> shape: nothing here throws for a server answer, the response is
/// read back through <see cref="ProblemDetailsResultReader"/> with the API's own
/// <see cref="ErrorType"/> intact, and transport faults become failures through
/// <see cref="HttpResultExecutor"/>. Only the list is retried: a create or a rotate returns a secret
/// that exists nowhere else, and a blind retry would mint a second one.
/// </remarks>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class MachineClientAdminService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IMachineClientAdminUIService
{
    private const string Endpoint = "Admin/MachineClients";

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<MachineClientResponse>>> GetAllAsync(CancellationToken cancellationToken = default) =>
        HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(Endpoint, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<IReadOnlyList<MachineClientResponse>>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<MachineClientSecretResponse>> CreateAsync(
        CreateMachineClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsJsonAsync(new Uri(Endpoint, UriKind.Relative), request, cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<MachineClientSecretResponse>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<MachineClientSecretResponse>> RotateAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        PostAsync<MachineClientSecretResponse>(clientId, "rotate", cancellationToken);

    /// <inheritdoc />
    public Task<Result<MachineClientResponse>> PromoteAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        PostAsync<MachineClientResponse>(clientId, "promote", cancellationToken);

    /// <inheritdoc />
    public Task<Result<MachineClientResponse>> RevokeAsync(Guid clientId, CancellationToken cancellationToken = default) =>
        PostAsync<MachineClientResponse>(clientId, "revoke", cancellationToken);

    /// <summary>One bodiless action POST on one client, read back as a <see cref="Result{T}"/>.</summary>
    /// <typeparam name="TResponse">The expected response body.</typeparam>
    /// <param name="clientId">The client.</param>
    /// <param name="action">The action segment (<c>rotate</c>, <c>promote</c> or <c>revoke</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The body, or the API's own refusal.</returns>
    private Task<Result<TResponse>> PostAsync<TResponse>(Guid clientId, string action, CancellationToken cancellationToken) =>
        HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsync(
                    new Uri($"{Endpoint}/{clientId}/{action}", UriKind.Relative), content: null, cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<TResponse>(response, cancellationToken: cancellationToken);
            },
            cancellationToken);
}
