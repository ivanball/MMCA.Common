using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MMCA.Common.API.Middleware;
using MMCA.Common.Application.Interfaces;
using Moq;

namespace MMCA.Common.API.Tests.Middleware;

public sealed class CorrelationIdMiddlewareTests
{
    // ── Generates ID when none provided ──
    [Fact]
    public async Task InvokeAsync_NoHeader_GeneratesCorrelationId()
    {
        string? capturedId = null;
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.SetCorrelationId(It.IsAny<string>()))
            .Callback<string>(id => capturedId = id);

        var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var httpContext = new DefaultHttpContext();

        await sut.InvokeAsync(httpContext, correlationContext.Object);

        capturedId.Should().NotBeNullOrWhiteSpace();
        correlationContext.Verify(x => x.SetCorrelationId(It.IsAny<string>()), Times.Once);
    }

    // ── Uses existing header value ──
    [Fact]
    public async Task InvokeAsync_WithHeader_UsesExistingCorrelationId()
    {
        const string expectedId = "my-custom-correlation-id";
        string? capturedId = null;
        var correlationContext = new Mock<ICorrelationContext>();
        correlationContext.Setup(x => x.SetCorrelationId(It.IsAny<string>()))
            .Callback<string>(id => capturedId = id);

        var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[CorrelationIdMiddleware.HeaderName] = expectedId;

        await sut.InvokeAsync(httpContext, correlationContext.Object);

        capturedId.Should().Be(expectedId);
    }

    // ── Registers OnStarting callback for response header ──
    [Fact]
    public async Task InvokeAsync_RegistersOnStartingCallback_ThatSetsResponseHeader()
    {
        const string expectedId = "echo-test-id";
        var correlationContext = new Mock<ICorrelationContext>();

        // Track registered OnStarting callbacks
        Func<object, Task>? registeredCallback = null;
        object? registeredState = null;

        var responseFeature = new Mock<IHttpResponseFeature>();
        responseFeature.SetupGet(x => x.Headers).Returns(new HeaderDictionary());
        responseFeature.Setup(x => x.OnStarting(It.IsAny<Func<object, Task>>(), It.IsAny<object>()))
            .Callback<Func<object, Task>, object>((cb, state) =>
            {
                registeredCallback = cb;
                registeredState = state;
            });

        var requestFeature = new Mock<IHttpRequestFeature>();
        var requestHeaders = new HeaderDictionary
        {
            [CorrelationIdMiddleware.HeaderName] = expectedId,
        };
        requestFeature.SetupGet(x => x.Headers).Returns(requestHeaders);

        var features = new FeatureCollection();
        features.Set(requestFeature.Object);
        features.Set(responseFeature.Object);

        var httpContext = new DefaultHttpContext(features);

        var sut = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(httpContext, correlationContext.Object);

        // Verify OnStarting was called
        registeredCallback.Should().NotBeNull();

        // Execute the callback and verify it sets the header
        await registeredCallback!(registeredState!);

        httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString()
            .Should().Be(expectedId);
    }

    // ── Calls next middleware ──
    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        var nextCalled = false;
        var correlationContext = new Mock<ICorrelationContext>();

        var sut = new CorrelationIdMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext();

        await sut.InvokeAsync(httpContext, correlationContext.Object);

        nextCalled.Should().BeTrue();
    }
}
