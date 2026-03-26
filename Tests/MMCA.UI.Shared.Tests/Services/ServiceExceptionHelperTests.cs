#pragma warning disable CA2000 // Dispose objects before losing scope — test doubles do not hold real resources
#pragma warning disable CA2025 // Task captures IDisposable — response is awaited inline in tests

using System.Net;
using System.Text;
using FluentAssertions;
using MMCA.Common.Shared.Exceptions;
using MMCA.UI.Shared.Services;

namespace MMCA.UI.Shared.Tests.Services;

public class ServiceExceptionHelperTests
{
    // ── Domain Exception ──
    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithDomainException_ThrowsWithDetail()
    {
        var json = """{"title":"Domain Exception","detail":"Order is already cancelled."}""";
        using var response = CreateJsonResponse(json, HttpStatusCode.BadRequest);

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Order is already cancelled.");
    }

    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithDomainException_NoDetail_UsesDefault()
    {
        var json = """{"title":"Domain Exception"}""";
        using var response = CreateJsonResponse(json, HttpStatusCode.BadRequest);

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("A domain error occurred.");
    }

    // ── Validation Exception ──
    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithValidationException_ExtractsErrors()
    {
        var json = """{"title":"Validation Exception","detail":"One or more validation errors occurred.","errors":{"Name":["Name is required.","Name must be at most 100 characters."]}}""";
        using var response = CreateJsonResponse(json, HttpStatusCode.BadRequest);

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Name is required. Name must be at most 100 characters.");
    }

    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithValidationException_NoErrors_UsesDetail()
    {
        var json = """{"title":"Validation Exception","detail":"Custom validation message."}""";
        using var response = CreateJsonResponse(json, HttpStatusCode.BadRequest);

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Custom validation message.");
    }

    // ── Operation Failed ──
    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithOperationFailed_ExtractsErrorMessages()
    {
        var json = """{"title":"Operation failed","detail":"An error occurred.","errors":[{"code":"Order.Invalid","message":"Insufficient inventory."}]}""";
        using var response = CreateJsonResponse(json, HttpStatusCode.BadRequest);

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().ThrowAsync<DomainInvariantViolationException>()
            .WithMessage("Insufficient inventory.");
    }

    // ── Non-Error Responses ──
    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithNonJsonBody_DoesNotThrow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized", Encoding.UTF8, "text/plain")
        };

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithEmptyBody_DoesNotThrow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(string.Empty)
        };

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithUnknownTitle_DoesNotThrow()
    {
        var json = """{"title":"Unknown Error","detail":"Something happened."}""";
        var response = CreateJsonResponse(json, HttpStatusCode.BadRequest);

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithNoTitleProperty_DoesNotThrow()
    {
        var json = """{"error":"Something went wrong."}""";
        var response = CreateJsonResponse(json, HttpStatusCode.BadRequest);

        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(response);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ThrowIfDomainExceptionAsync_WithNullResponse_ThrowsArgumentNull()
    {
        var act = () => ServiceExceptionHelper.ThrowIfDomainExceptionAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Helpers ──
    private static HttpResponseMessage CreateJsonResponse(string json, HttpStatusCode statusCode) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
