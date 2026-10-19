using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using MMCA.Common.API.Pagination;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.API.Tests.Pagination;

/// <summary>
/// Pins the <c>X-Pagination</c> header contract: the name every paged endpoint writes, and the body
/// as <see cref="JsonSerializerOptions.Web"/> JSON of the page's metadata, which is what the paged
/// entity endpoint wrote inline before the helper existed and what consumer controllers copy.
/// </summary>
public sealed class PaginationHeaderExtensionsTests
{
    [Fact]
    public void HeaderName_IsXPagination() =>
        PaginationHeaderExtensions.HeaderName.Should().Be("X-Pagination");

    [Fact]
    public void AppendPaginationHeader_WritesTheMetadataAsWebJson()
    {
        var metadata = new PaginationMetadata(totalItemCount: 57, pageSize: 25, currentPage: 2);
        var context = new DefaultHttpContext();

        context.Response.AppendPaginationHeader(metadata);

        context.Response.Headers["X-Pagination"].ToString()
            .Should().Be(JsonSerializer.Serialize(metadata, JsonSerializerOptions.Web));
    }

    [Fact]
    public void AppendPaginationHeader_UsesCamelCasePropertyNames()
    {
        var metadata = new PaginationMetadata(totalItemCount: 57, pageSize: 25, currentPage: 2);
        var context = new DefaultHttpContext();

        context.Response.AppendPaginationHeader(metadata);

        string header = context.Response.Headers["X-Pagination"].ToString();
        header.Should().Contain("\"totalItemCount\":57");
        header.Should().Contain("\"pageSize\":25");
        header.Should().Contain("\"currentPage\":2");
    }

    [Fact]
    public void AppendPaginationHeader_RoundTripsThroughTheWebOptions()
    {
        var metadata = new PaginationMetadata(totalItemCount: 3, pageSize: 10, currentPage: 1);
        var context = new DefaultHttpContext();

        context.Response.AppendPaginationHeader(metadata);

        PaginationMetadata? read = JsonSerializer.Deserialize<PaginationMetadata>(
            context.Response.Headers["X-Pagination"].ToString(),
            JsonSerializerOptions.Web);
        read.Should().Be(metadata);
    }

    [Fact]
    public void AppendPaginationHeader_WithNullMetadata_Throws()
    {
        var context = new DefaultHttpContext();

        Action act = () => context.Response.AppendPaginationHeader(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
