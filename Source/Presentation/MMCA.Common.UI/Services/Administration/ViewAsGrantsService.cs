using System.Globalization;
using System.Text;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Auth.Requests;
using MMCA.Common.Shared.Auth.Responses;
using MMCA.Common.Shared.Http;
using MMCA.Common.UI.Services.Api;
using MMCA.Common.UI.Services.Auth.Tokens;

namespace MMCA.Common.UI.Services.Administration;

/// <summary>
/// HTTP client for the <c>Admin/ViewAsGrants</c> resource, the support access log (ADR-137).
/// Registered by <c>AddSupportAccessLogUI</c>.
/// </summary>
/// <remarks>
/// Nothing here throws for a server answer: the response is read back through
/// <see cref="ProblemDetailsResultReader"/> with the API's own <see cref="ErrorType"/> intact, and
/// transport faults become failures through <see cref="HttpResultExecutor"/>. Only the caller's own
/// cancellation still propagates. Same shape as <see cref="RoleAdminService"/>.
/// </remarks>
/// <param name="httpClientFactory">Creates the API client.</param>
/// <param name="tokenStorageService">Supplies the caller's bearer token.</param>
public sealed class ViewAsGrantsService(
    IHttpClientFactory httpClientFactory,
    ITokenStorageService tokenStorageService)
    : AuthenticatedServiceBase(httpClientFactory, tokenStorageService), IViewAsGrantsUIService
{
    private const string Endpoint = "Admin/ViewAsGrants";

    /// <inheritdoc />
    public Task<Result<PagedCollectionResult<ViewAsGrantSummaryResponse>>> GetPageAsync(
        ViewAsGrantListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = BuildUrl(request);

        return HttpResultExecutor.ExecuteAsync(
            async () =>
            {
                using var httpClient = await CreateAuthenticatedClientAsync();
                using var response = await RetryPolicy.ExecuteAsync(
                    _ => httpClient.GetAsync(new Uri(url, UriKind.Relative), cancellationToken),
                    cancellationToken);

                return await ProblemDetailsResultReader.ReadAsync<PagedCollectionResult<ViewAsGrantSummaryResponse>>(
                    response, cancellationToken: cancellationToken);
            },
            cancellationToken);
    }

    /// <summary>
    /// The query string the controller binds <see cref="ViewAsGrantListRequest"/> from: the page
    /// always, each filter only when set, instants in round-trip UTC form.
    /// </summary>
    /// <param name="request">The filters and the page.</param>
    /// <returns>The relative URL.</returns>
    internal static string BuildUrl(ViewAsGrantListRequest request)
    {
        var query = new StringBuilder(Endpoint)
            .Append(CultureInfo.InvariantCulture, $"?pageNumber={request.PageNumber}&pageSize={request.PageSize}");

        Append(query, "staff", request.Staff);
        Append(query, "viewed", request.Viewed);
        Append(query, "from", request.From?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(query, "to", request.To?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(query, "status", request.Status?.ToString());

        return query.ToString();
    }

    private static void Append(StringBuilder query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value.Trim()));
        }
    }
}
