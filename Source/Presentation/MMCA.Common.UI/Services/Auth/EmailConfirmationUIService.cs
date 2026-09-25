using System.Net.Http.Json;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;

namespace MMCA.Common.UI.Services.Auth;

/// <summary>
/// Implements <see cref="IEmailConfirmationUIService"/> over the anonymous <c>auth/*</c>
/// email-confirmation endpoints (ADR-116).
/// </summary>
/// <remarks>
/// Deliberately NOT built on the entity service bases: those run the retry pipeline, and both
/// endpoints spend a single-use token or a per-address request budget, so this takes the named API
/// client straight from the factory and posts exactly once (the same shape as the anonymous calls in
/// <see cref="AuthUIService"/>). Both actions are anonymous on the server, so a bearer token the
/// client's shared handler attaches from an earlier sign-in is ignored rather than required. Nothing
/// here throws for a server answer: the response is read back through
/// <see cref="ProblemDetailsResultReader"/> with the API's own <see cref="ErrorType"/> intact.
/// </remarks>
/// <param name="httpClientFactory">Factory for the named <c>"APIClient"</c> HttpClient.</param>
public sealed class EmailConfirmationUIService(IHttpClientFactory httpClientFactory) : IEmailConfirmationUIService
{
    private const string ApiClientName = "APIClient";

    /// <inheritdoc />
    public async Task<Result> ConfirmEmailAsync(string email, string token, CancellationToken cancellationToken = default) =>
        await PostOnceAsync("auth/confirm-email", new ConfirmEmailRequest(email, token), cancellationToken);

    /// <inheritdoc />
    public async Task<Result> ResendEmailConfirmationAsync(string email, CancellationToken cancellationToken = default) =>
        await PostOnceAsync("auth/send-email-confirmation", new SendEmailConfirmationRequest(email), cancellationToken);

    private async Task<Result> PostOnceAsync<TRequest>(string relativeUrl, TRequest body, CancellationToken cancellationToken) =>
        await HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = httpClientFactory.CreateClient(ApiClientName);

                // No retry: a repeat is a second request (a spent token, a second email), never a
                // replayed response.
                using var response = await httpClient.PostAsJsonAsync(
                    new Uri(relativeUrl, UriKind.Relative), body, cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync(response, cancellationToken);
            },
            cancellationToken);
}
