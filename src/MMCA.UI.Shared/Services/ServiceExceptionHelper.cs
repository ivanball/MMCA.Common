using System.Text.Json;
using MMCA.Common.Shared.Exceptions;

namespace MMCA.UI.Shared.Services;

/// <summary>
/// Inspects non-success HTTP responses for ProblemDetails-style error payloads produced by the
/// WebAPI (Domain Exception, Validation Exception, Operation failed) and re-throws them as
/// <see cref="DomainInvariantViolationException"/> so UI pages can display the original error message.
/// </summary>
public static class ServiceExceptionHelper
{
    /// <summary>
    /// Parses the response body for known ProblemDetails titles and throws a
    /// <see cref="DomainInvariantViolationException"/> with the extracted detail/errors message.
    /// </summary>
    public static async Task ThrowIfDomainExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Content is null)
            return;

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            // Response body is not JSON (e.g., bare 401 challenge, HTML error page).
            // Let the caller's EnsureSuccessStatusCode() handle it.
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("title", out var titleElement))
                return;

            var title = titleElement.GetString();

            if (title?.Equals("Domain Exception", StringComparison.Ordinal) == true)
            {
                var detail = root.TryGetProperty("detail", out var detailElement)
                    ? detailElement.GetString() ?? "A domain error occurred."
                    : "A domain error occurred.";

                throw new DomainInvariantViolationException(detail);
            }

            if (title?.Equals("Validation Exception", StringComparison.Ordinal) == true)
            {
                var message = root.TryGetProperty("detail", out var detailElement)
                    ? detailElement.GetString() ?? "One or more validation errors occurred."
                    : "One or more validation errors occurred.";

                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    var errorMessages = new List<string>();
                    foreach (var property in errors.EnumerateObject())
                    {
                        foreach (var error in property.Value.EnumerateArray())
                            errorMessages.Add(error.GetString() ?? string.Empty);
                    }
                    if (errorMessages.Count > 0)
                        message = string.Join(" ", errorMessages);
                }

                throw new DomainInvariantViolationException(message);
            }

            if (title?.Equals("Operation failed", StringComparison.Ordinal) == true)
            {
                var message = root.TryGetProperty("detail", out var detailElement)
                    ? detailElement.GetString() ?? "An error occurred."
                    : "An error occurred.";

                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    var errorMessages = new List<string>();
                    foreach (var error in errors.EnumerateArray())
                    {
                        if (error.TryGetProperty("message", out var msgElement))
                        {
                            var msg = msgElement.GetString();
                            if (!string.IsNullOrWhiteSpace(msg))
                                errorMessages.Add(msg);
                        }
                    }
                    if (errorMessages.Count > 0)
                        message = string.Join(" ", errorMessages);
                }

                throw new DomainInvariantViolationException(message);
            }
        }
    }
}
