using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using MMCA.Common.API.Concurrency;
using MMCA.Common.API.Controllers;
using MMCA.Common.Application.UseCases.Contracts;
using MMCA.Common.Shared.Abstractions;
using MMCA.Common.Shared.Http;

namespace MMCA.Common.API.Tests.Controllers;

/// <summary>
/// The narrow-update helpers on <see cref="ApiControllerBase"/>: a conditional write reads the
/// <c>If-Match</c> token the filter decoded, builds its command from it, dispatches the command, and
/// answers 204 (no body) or 200 (the handler value plus its ETag). A failure is the base's Problem
/// Details and never runs the success callback.
/// </summary>
public sealed class ApiControllerBaseConditionalCommandTests
{
    /// <summary>The weak ETag <see cref="IfMatchToken"/> formats to.</summary>
    private const string ExpectedETag = "W/\"AAAAAAAAB9E=\"";

    /// <summary>The token the conditional-write filter decoded for the request under test.</summary>
    private static readonly byte[] IfMatchToken = [0, 0, 0, 0, 0, 0, 7, 209];

    private static ConditionalCommandProbeController CreateController(bool withIfMatchToken = true)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };

        // [SupportsIfMatch] decodes the If-Match header into this slot before the action runs, so a
        // direct call supplies it the same way.
        if (withIfMatchToken)
        {
            httpContext.Items[SupportsIfMatchAttribute.TokenItemKey] = IfMatchToken;
        }

        return new ConditionalCommandProbeController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static Error NotFound() => Error.NotFoundError("Probe.NotFound", "No such probe");

    // -- HandleConditionalCommandAsync (204) --
    [Fact]
    public async Task NoContentHelper_PassesDecodedTokenToBuilder()
    {
        var sut = CreateController();
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result>(Result.Success());
        using var cts = new CancellationTokenSource();
        byte[]? received = null;
        ProbeRenameCommand? built = null;

        await sut.RunNoContentAsync(
            handler,
            token =>
            {
                received = token;
                built = new ProbeRenameCommand(7, token);
                return built;
            },
            cancellationToken: cts.Token);

        received.Should().Equal(IfMatchToken, "the builder is handed the token the filter decoded");
        handler.Commands.Should().ContainSingle().Which.Should().BeSameAs(built, "the built command is what the handler runs");
        handler.CancellationTokens.Should().ContainSingle().Which.Should().Be(cts.Token);
    }

    [Fact]
    public async Task NoContentHelper_Success_Returns204_NoETag()
    {
        var sut = CreateController();
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result>(Result.Success());

        var result = await sut.RunNoContentAsync(handler, token => new ProbeRenameCommand(1, token));

        result.Should().BeOfType<NoContentResult>().Which.StatusCode.Should().Be(StatusCodes.Status204NoContent);
        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public async Task NoContentHelper_ResultOfTHandler_Returns204()
    {
        var sut = CreateController();
        var value = new ProbeVersionedValue(1, IfMatchToken);
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(Result.Success(value));

        var result = await sut.RunNoContentAsync(handler, token => new ProbeRenameCommand(1, token));

        result.Should().BeOfType<NoContentResult>("a Result<T> handler is accepted and its value is discarded");
        sut.Response.Headers.Should().NotContainKey(
            ConcurrencyETag.ETagHeaderName,
            "the 204 helper never emits an ETag, even when the discarded value carries a token");
    }

    [Fact]
    public async Task NoContentHelper_Failure_ReturnsProblemDetails_SkipsOnSuccess()
    {
        var sut = CreateController();
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result>(Result.Failure(NotFound()));
        var onSuccessCalls = 0;

        var result = await sut.RunNoContentAsync(
            handler,
            token => new ProbeRenameCommand(1, token),
            onSuccess: _ =>
            {
                onSuccessCalls++;
                return ValueTask.CompletedTask;
            });

        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
        onSuccessCalls.Should().Be(0, "a failed write has nothing to follow up");
        sut.HandleFailureCalls.Should().Be(1, "the failure goes through the virtual HandleFailure, so an override applies");
    }

    [Fact]
    public async Task NoContentHelper_Success_RunsOnSuccessBeforeReturning()
    {
        var sut = CreateController();
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result>(Result.Success());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;

        var pending = sut.RunNoContentAsync(
            handler,
            token => new ProbeRenameCommand(1, token),
            onSuccess: ct =>
            {
                seen = ct;
                return new ValueTask(gate.Task);
            },
            cancellationToken: cts.Token);

        pending.IsCompleted.Should().BeFalse("the helper awaits the success callback before answering");

        gate.SetResult();
        var result = await pending;

        result.Should().BeOfType<NoContentResult>();
        seen.Should().Be(cts.Token, "the callback receives the request's cancellation token");
    }

    // -- HandleConditionalCommandWithResponseAsync (200) --
    [Fact]
    public async Task ResponseHelper_Success_Returns200WithValue_AndETag()
    {
        var sut = CreateController();
        var value = new ProbeVersionedValue(5, IfMatchToken);
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(Result.Success(value));

        var result = await sut.RunWithResponseAsync(handler, token => new ProbeRenameCommand(5, token));

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Which;
        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeSameAs(value, "the body is exactly the handler value");
        sut.Response.Headers[ConcurrencyETag.ETagHeaderName].ToString().Should().Be(ExpectedETag);
    }

    [Fact]
    public async Task ResponseHelper_ValueWithoutRowVersion_NoETag()
    {
        var sut = CreateController();
        var value = new ProbeUnversionedValue(5);
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeUnversionedValue>>(Result.Success(value));

        var result = await sut.RunWithResponseAsync(handler, token => new ProbeRenameCommand(5, token));

        result.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(value);
        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public async Task ResponseHelper_Failure_ReturnsProblemDetails_SkipsOnSuccess()
    {
        var sut = CreateController();
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(
            Result.Failure<ProbeVersionedValue>(NotFound()));
        var onSuccessCalls = 0;

        var result = await sut.RunWithResponseAsync(
            handler,
            token => new ProbeRenameCommand(5, token),
            onSuccess: (_, _) =>
            {
                onSuccessCalls++;
                return ValueTask.CompletedTask;
            });

        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        objectResult.Value.Should().BeOfType<ProblemDetails>();
        onSuccessCalls.Should().Be(0);
        sut.HandleFailureCalls.Should().Be(1, "the failure goes through the virtual HandleFailure, so an override applies");
        sut.Response.Headers.Should().NotContainKey(ConcurrencyETag.ETagHeaderName);
    }

    [Fact]
    public async Task ResponseHelper_OnSuccessReceivesValue()
    {
        var sut = CreateController();
        var value = new ProbeVersionedValue(5, IfMatchToken);
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(Result.Success(value));
        using var cts = new CancellationTokenSource();
        ProbeVersionedValue? seenValue = null;
        CancellationToken seenToken = default;

        await sut.RunWithResponseAsync(
            handler,
            token => new ProbeRenameCommand(5, token),
            onSuccess: (v, ct) =>
            {
                seenValue = v;
                seenToken = ct;
                return ValueTask.CompletedTask;
            },
            cancellationToken: cts.Token);

        seenValue.Should().BeSameAs(value);
        seenToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ResponseHelper_Success_AwaitsOnSuccessBeforeReturning()
    {
        var sut = CreateController();
        var value = new ProbeVersionedValue(5, IfMatchToken);
        var handler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(Result.Success(value));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = sut.RunWithResponseAsync(
            handler,
            token => new ProbeRenameCommand(5, token),
            onSuccess: (_, _) => new ValueTask(gate.Task));

        pending.IsCompleted.Should().BeFalse("the helper awaits the success callback before answering");

        gate.SetResult();
        var result = await pending;

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    // -- Guards --
    [Fact]
    public async Task Helpers_WithoutIfMatchToken_ThrowInvalidOperation_HandlerNotCalled()
    {
        var sut = CreateController(withIfMatchToken: false);
        var noContentHandler = new RecordingCommandHandler<ProbeRenameCommand, Result>(Result.Success());
        var responseHandler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(
            Result.Success(new ProbeVersionedValue(1, IfMatchToken)));
        var builderCalls = 0;

        var noContent = () => sut.RunNoContentAsync(noContentHandler, token =>
        {
            builderCalls++;
            return new ProbeRenameCommand(1, token);
        });
        var withResponse = () => sut.RunWithResponseAsync(responseHandler, token =>
        {
            builderCalls++;
            return new ProbeRenameCommand(1, token);
        });

        (await noContent.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*SupportsIfMatchAttribute*", because: "a helper reached without the filter is a wiring mistake");
        (await withResponse.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*SupportsIfMatchAttribute*");
        noContentHandler.Commands.Should().BeEmpty("the handler never runs without a precondition");
        responseHandler.Commands.Should().BeEmpty("the handler never runs without a precondition");
        builderCalls.Should().Be(0, "there is no token to build a command from");
    }

    [Fact]
    public async Task Helpers_NullArguments_Throw()
    {
        var sut = CreateController();
        var noContentHandler = new RecordingCommandHandler<ProbeRenameCommand, Result>(Result.Success());
        var responseHandler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(
            Result.Success(new ProbeVersionedValue(1, IfMatchToken)));

        var noContentNullHandler = () => sut.RunNoContentAsync<ProbeRenameCommand, Result>(
            null!, token => new ProbeRenameCommand(1, token));
        var noContentNullBuilder = () => sut.RunNoContentAsync(noContentHandler, null!);
        var responseNullHandler = () => sut.RunWithResponseAsync<ProbeRenameCommand, ProbeVersionedValue>(
            null!, token => new ProbeRenameCommand(1, token));
        var responseNullBuilder = () => sut.RunWithResponseAsync(responseHandler, null!);

        await noContentNullHandler.Should().ThrowAsync<ArgumentNullException>();
        await noContentNullBuilder.Should().ThrowAsync<ArgumentNullException>();
        await responseNullHandler.Should().ThrowAsync<ArgumentNullException>();
        await responseNullBuilder.Should().ThrowAsync<ArgumentNullException>();
        noContentHandler.Commands.Should().BeEmpty();
        responseHandler.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task OnSuccessThrows_Propagates()
    {
        var sut = CreateController();
        var noContentHandler = new RecordingCommandHandler<ProbeRenameCommand, Result>(Result.Success());
        var responseHandler = new RecordingCommandHandler<ProbeRenameCommand, Result<ProbeVersionedValue>>(
            Result.Success(new ProbeVersionedValue(1, IfMatchToken)));

        var noContent = () => sut.RunNoContentAsync(
            noContentHandler,
            token => new ProbeRenameCommand(1, token),
            onSuccess: _ => throw new InvalidTimeZoneException("follow-up failed"));
        var withResponse = () => sut.RunWithResponseAsync(
            responseHandler,
            token => new ProbeRenameCommand(1, token),
            onSuccess: (_, _) => throw new InvalidTimeZoneException("follow-up failed"));

        await noContent.Should().ThrowAsync<InvalidTimeZoneException>("the helper does not swallow a follow-up failure");
        await withResponse.Should().ThrowAsync<InvalidTimeZoneException>("the helper does not swallow a follow-up failure");
    }
}

/// <summary>
/// Exposes the protected <see cref="ApiControllerBase"/> helpers to the tests and counts calls to the
/// virtual <see cref="ApiControllerBase.HandleFailure"/>, so a test can prove an override applies.
/// Shared by <see cref="ApiControllerBaseConditionalCommandTests"/> and <see cref="ApiControllerBaseETagTests"/>.
/// </summary>
internal sealed class ConditionalCommandProbeController : ApiControllerBase
{
    public int HandleFailureCalls { get; private set; }

    public Task<ActionResult> RunNoContentAsync<TCommand, TResult>(
        ICommandHandler<TCommand, TResult> handler,
        Func<byte[], TCommand> buildCommand,
        Func<CancellationToken, ValueTask>? onSuccess = null,
        CancellationToken cancellationToken = default)
        where TResult : Result =>
        HandleConditionalCommandAsync(handler, buildCommand, onSuccess, cancellationToken);

    public Task<ActionResult<TResponse>> RunWithResponseAsync<TCommand, TResponse>(
        ICommandHandler<TCommand, Result<TResponse>> handler,
        Func<byte[], TCommand> buildCommand,
        Func<TResponse, CancellationToken, ValueTask>? onSuccess = null,
        CancellationToken cancellationToken = default) =>
        HandleConditionalCommandWithResponseAsync(handler, buildCommand, onSuccess, cancellationToken);

    public void RunSetConcurrencyETag(object? dto) => SetConcurrencyETag(dto);

    protected override ObjectResult HandleFailure(IEnumerable<Error> errors)
    {
        HandleFailureCalls++;
        return base.HandleFailure(errors);
    }
}

/// <summary>A command handler that records every dispatch and answers a fixed result.</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
internal sealed class RecordingCommandHandler<TCommand, TResult>(TResult result) : ICommandHandler<TCommand, TResult>
{
    public List<TCommand> Commands { get; } = [];

    public List<CancellationToken> CancellationTokens { get; } = [];

    public Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(result);
    }
}

/// <summary>A narrow-update command carrying the decoded token.</summary>
internal sealed record ProbeRenameCommand(int Id, byte[] RowVersion);

/// <summary>A handler value that carries a concurrency token.</summary>
internal sealed record ProbeVersionedValue(int Id, byte[] RowVersion);

/// <summary>A handler value with no concurrency token.</summary>
internal sealed record ProbeUnversionedValue(int Id);
