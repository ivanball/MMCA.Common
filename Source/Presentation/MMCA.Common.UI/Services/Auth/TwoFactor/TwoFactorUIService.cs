using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Auth.TwoFactor;

/// <summary>
/// HTTP client for the <c>auth/two-factor</c> endpoints. Registered by <c>AddTwoFactorUI()</c>.
/// </summary>
/// <remarks>
/// The shipped authenticated-service shape (<c>MachineClientAdminService</c>): the bearer comes from
/// the component's own <see cref="ITokenStorageService"/> (so its owner is tracked and a 401 renews
/// the right session), the response is read back through <see cref="ProblemDetailsResultReader"/> with
/// the API's own error codes intact, and transport faults become failures through
/// <see cref="HttpResultExecutor"/>. Only the status read is retried; no POST is, because each one
/// either mints a secret, spends a code or replaces the recovery codes. Neither the secret nor a
/// recovery code is logged or cached here: they live only in the returned value.
/// </remarks>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class TwoFactorUIService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), ITwoFactorUIService
{
    private const string Endpoint = "auth/two-factor";

    /// <inheritdoc />
    public Task<Result<TwoFactorStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default) =>
        HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(Endpoint, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<TwoFactorStatusResponse>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<TwoFactorSetupResponse>> BeginEnrollmentAsync(CancellationToken cancellationToken = default) =>
        HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsync(
                    new Uri($"{Endpoint}/enrollment", UriKind.Relative), content: null, cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<TwoFactorSetupResponse>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<TwoFactorRecoveryCodesResponse>> ConfirmEnrollmentAsync(string code, CancellationToken cancellationToken = default) =>
        PostCodeAsync($"{Endpoint}/enrollment/confirm", code, cancellationToken);

    /// <inheritdoc />
    public Task<Result<TwoFactorRecoveryCodesResponse>> RegenerateRecoveryCodesAsync(string code, CancellationToken cancellationToken = default) =>
        PostCodeAsync($"{Endpoint}/recovery-codes", code, cancellationToken);

    /// <inheritdoc />
    public Task<Result> DisableAsync(string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsJsonAsync(
                    new Uri($"{Endpoint}/disable", UriKind.Relative),
                    new TwoFactorCodeRequest(code.Trim()),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>One code-proved POST that answers with the recovery codes.</summary>
    /// <param name="relativeUrl">The action route.</param>
    /// <param name="code">The code that proves the action.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The recovery codes, or the API's own refusal.</returns>
    private Task<Result<TwoFactorRecoveryCodesResponse>> PostCodeAsync(string relativeUrl, string code, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await httpClient.PostAsJsonAsync(
                    new Uri(relativeUrl, UriKind.Relative),
                    new TwoFactorCodeRequest(code.Trim()),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<TwoFactorRecoveryCodesResponse>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }
}
