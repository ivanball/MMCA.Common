using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MMCA.Common.API.Middleware;
using MMCA.Common.Shared.Exceptions;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

public sealed class ExceptionHandlerTests
{
    // ── GlobalExceptionHandler ──
    [Fact]
    public async Task GlobalExceptionHandler_SetsStatusCode500()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .ReturnsAsync(true);
        var sut = new GlobalExceptionHandler(
            problemDetailsService.Object,
            NullLogger<GlobalExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        var exception = new InvalidOperationException("Something went wrong");

        bool handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task GlobalExceptionHandler_WritesProblemDetails()
    {
        ProblemDetailsContext? capturedContext = null;
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(ctx => capturedContext = ctx)
            .ReturnsAsync(true);
        var sut = new GlobalExceptionHandler(
            problemDetailsService.Object,
            NullLogger<GlobalExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();

        await sut.TryHandleAsync(httpContext, new InvalidOperationException("test"), CancellationToken.None);

        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Title.Should().Be("Internal Server Error");
    }

    // ── DomainExceptionHandler ──
    [Fact]
    public async Task DomainExceptionHandler_WithDomainException_Returns400()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .ReturnsAsync(true);
        var sut = new DomainExceptionHandler(
            problemDetailsService.Object,
            NullLogger<DomainExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        var exception = new TestDomainException("Business rule violated");

        bool handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task DomainExceptionHandler_WithNonDomainException_ReturnsFalse()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        var sut = new DomainExceptionHandler(
            problemDetailsService.Object,
            NullLogger<DomainExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        var exception = new InvalidOperationException("Not a domain exception");

        bool handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeFalse();
    }

    // ── ValidationExceptionHandler ──
    [Fact]
    public async Task ValidationExceptionHandler_WithValidationException_Returns400()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .ReturnsAsync(true);
        var sut = new ValidationExceptionHandler(
            problemDetailsService.Object,
            NullLogger<ValidationExceptionHandler>.Instance);

        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required"),
            new("Name", "Name must be at least 3 characters"),
            new("Email", "Email is required"),
        };
        var httpContext = new DefaultHttpContext();
        var exception = new ValidationException(failures);

        bool handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ValidationExceptionHandler_GroupsErrorsByProperty()
    {
        ProblemDetailsContext? capturedContext = null;
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .Callback<ProblemDetailsContext>(ctx => capturedContext = ctx)
            .ReturnsAsync(true);
        var sut = new ValidationExceptionHandler(
            problemDetailsService.Object,
            NullLogger<ValidationExceptionHandler>.Instance);

        var failures = new List<ValidationFailure>
        {
            new("Name", "Name is required"),
            new("Name", "Name is too short"),
        };
        var httpContext = new DefaultHttpContext();

        await sut.TryHandleAsync(httpContext, new ValidationException(failures), CancellationToken.None);

        capturedContext.Should().NotBeNull();
        capturedContext!.ProblemDetails.Extensions.Should().ContainKey("errors");
    }

    [Fact]
    public async Task ValidationExceptionHandler_WithNonValidationException_ReturnsFalse()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        var sut = new ValidationExceptionHandler(
            problemDetailsService.Object,
            NullLogger<ValidationExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();

        bool handled = await sut.TryHandleAsync(httpContext, new InvalidOperationException("not validation"), CancellationToken.None);

        handled.Should().BeFalse();
    }

    // ── DbUpdateExceptionHandler ──
    [Fact]
    public async Task DbUpdateExceptionHandler_WithDbUpdateException_Returns409()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .ReturnsAsync(true);
        var sut = new DbUpdateExceptionHandler(
            problemDetailsService.Object,
            NullLogger<DbUpdateExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        var exception = new DbUpdateException("Conflict", new InvalidOperationException("inner"));

        bool handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task DbUpdateExceptionHandler_WithNonDbUpdateException_ReturnsFalse()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        var sut = new DbUpdateExceptionHandler(
            problemDetailsService.Object,
            NullLogger<DbUpdateExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();

        bool handled = await sut.TryHandleAsync(httpContext, new InvalidOperationException("not db"), CancellationToken.None);

        handled.Should().BeFalse();
    }

    // ── OperationCanceledExceptionHandler ──
    [Fact]
    public async Task OperationCanceledExceptionHandler_WithOperationCanceledException_Returns499()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        problemDetailsService.Setup(x => x.TryWriteAsync(It.IsAny<ProblemDetailsContext>()))
            .ReturnsAsync(true);
        var sut = new OperationCanceledExceptionHandler(
            problemDetailsService.Object,
            NullLogger<OperationCanceledExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        var exception = new OperationCanceledException();

        bool handled = await sut.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
    }

    [Fact]
    public async Task OperationCanceledExceptionHandler_WithNonCanceledException_ReturnsFalse()
    {
        var problemDetailsService = new Mock<IProblemDetailsService>();
        var sut = new OperationCanceledExceptionHandler(
            problemDetailsService.Object,
            NullLogger<OperationCanceledExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();

        bool handled = await sut.TryHandleAsync(httpContext, new InvalidOperationException("not canceled"), CancellationToken.None);

        handled.Should().BeFalse();
    }
}

// ── Test helpers ──
public sealed class TestDomainException : DomainException
{
    public TestDomainException() { }

    public TestDomainException(string message)
        : base(message) { }

    public TestDomainException(string message, Exception innerException)
        : base(message, innerException) { }
}
