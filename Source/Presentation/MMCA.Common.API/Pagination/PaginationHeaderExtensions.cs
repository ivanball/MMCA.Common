using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Pagination;

/// <summary>
/// Writes the <c>X-Pagination</c> response header the framework's paged entity endpoint emits, so a
/// controller that serves a page outside <c>EntityControllerBase</c> writes the identical header
/// instead of re-declaring the name and the serializer settings.
/// </summary>
public static class PaginationHeaderExtensions
{
    /// <summary>
    /// The response header carrying a page's <see cref="PaginationMetadata"/> as JSON.
    /// </summary>
    public const string HeaderName = "X-Pagination";

    extension(HttpResponse response)
    {
        /// <summary>
        /// Appends the <see cref="HeaderName"/> header, serializing <paramref name="metadata"/> with
        /// <see cref="JsonSerializerOptions.Web"/> (camelCase property names), which is the shape every
        /// paged endpoint in the framework emits.
        /// </summary>
        /// <param name="metadata">The page's pagination metadata.</param>
        public void AppendPaginationHeader(PaginationMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(metadata);

            response.Headers.Append(HeaderName, JsonSerializer.Serialize(metadata, JsonSerializerOptions.Web));
        }
    }
}
