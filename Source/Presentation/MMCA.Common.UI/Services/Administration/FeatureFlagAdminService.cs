using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.FeatureFlags;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// HTTP client for the feature-flag administration endpoints (ADR-031), one flag source (API route
/// prefix) per call. Registered by <c>AddFeatureFlagAdministrationUI</c>.
/// </summary>
/// <remarks>
/// The <see cref="RoleAdminService"/> shape: nothing here throws for a server answer, the response is
/// read back through <see cref="ProblemDetailsResultReader"/> with the API's own
/// <see cref="ErrorType"/> intact, and transport faults become failures through
/// <see cref="HttpResultExecutor"/>. The PUT and DELETE are idempotent (they set or clear one row), so
/// all three calls retry.
/// </remarks>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class FeatureFlagAdminService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IFeatureFlagAdminUIService
{
    /// <inheritdoc />
    public Task<Result<IReadOnlyList<FeatureFlagStatusResponse>>> GetAllAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(source, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<IReadOnlyList<FeatureFlagStatusResponse>>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<FeatureFlagStatusResponse>> SetOverrideAsync(
        string source,
        string name,
        SetFeatureFlagOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = FlagUrl(source, name);

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.PutAsJsonAsync(new Uri(url, UriKind.Relative), request, cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<FeatureFlagStatusResponse>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> ClearOverrideAsync(string source, string name, CancellationToken cancellationToken = default)
    {
        var url = FlagUrl(source, name);

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.DeleteAsync(new Uri(url, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
    }

    private static string FlagUrl(string source, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return $"{source.TrimEnd('/')}/{Uri.EscapeDataString(name)}";
    }
}
