using System.Text.Json;
using MMCA.Common.Shared.Exceptions;

namespace MMCA.Common.UI.Services;

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
                throw new DomainInvariantViolationException(ExtractDetailMessage(root, "A domain error occurred."));

            if (title?.Equals("Validation Exception", StringComparison.Ordinal) == true)
                throw new DomainInvariantViolationException(ExtractValidationMessage(root));

            if (title?.Equals("Operation failed", StringComparison.Ordinal) == true)
                throw new DomainInvariantViolationException(ExtractOperationFailedMessage(root));
        }
    }

    private static string ExtractDetailMessage(JsonElement root, string fallback) =>
        root.TryGetProperty("detail", out var detailElement)
            ? detailElement.GetString() ?? fallback
            : fallback;

    private static string ExtractValidationMessage(JsonElement root)
    {
        var message = ExtractDetailMessage(root, "One or more validation errors occurred.");

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

        return message;
    }

    private static string ExtractOperationFailedMessage(JsonElement root)
    {
        var message = ExtractDetailMessage(root, "An error occurred.");

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

        return message;
    }
}
