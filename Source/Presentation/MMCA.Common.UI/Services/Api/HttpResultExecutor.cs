using System.Text.Json;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.UI.Services.Api;

/// <summary>
/// Turns the exceptions an HTTP call can still throw after
/// <c>MMCA.Common.Shared.Http.ProblemDetailsResultReader</c> has taken care of the response into
/// failed <see cref="Result"/> values, so a UI service never throws for a condition the page is
/// expected to render.
/// <para>
/// The reader converts a <b>response</b>; this converts the absence of one: a refused connection, a
/// DNS failure, a dropped socket, a body that never arrived, an <see cref="HttpClient"/> timeout.
/// Both halves are needed for a service method to be honestly typed as returning a
/// <see cref="Result"/>.
/// </para>
/// <para>
/// <b>Cancellation always propagates.</b> When the caller's token is the reason the operation
/// stopped, the <see cref="OperationCanceledException"/> is rethrown: a page owns its own
/// cancellation (a disposed component, a superseded grid fetch) and must not have it reported back
/// as an error to render. An <see cref="HttpClient"/> timeout raises the same exception type with
/// the token NOT cancelled, and that one does become a failure.
/// </para>
/// <para>
/// <b>Messages are English.</b> A transport failure never reached a server, so nothing localized it
/// on the way back, exactly as with the reader's own synthesized messages. A page that needs
/// translated wording branches on the code (<see cref="TransportErrorCode"/>,
/// <see cref="TimeoutErrorCode"/>) or supplies a resource key of its own.
/// </para>
/// </summary>
public static class HttpResultExecutor
{
    /// <summary>Error code for a request that never completed a round trip (connection, DNS, socket, body).</summary>
    public const string TransportErrorCode = "Http.TransportFailure";

    /// <summary>Error code for a request the client itself gave up on before the caller cancelled.</summary>
    public const string TimeoutErrorCode = "Http.Timeout";

    private const string TransportMessage =
        "The request could not be completed. Check the connection and try again.";

    private const string TimeoutMessage =
        "The request timed out before the server responded.";

    /// <summary>
    /// Runs a valueless HTTP operation, converting transport faults into failures.
    /// </summary>
    /// <param name="operation">The operation, which already returns a <see cref="Result"/> for every response it gets.</param>
    /// <param name="cancellationToken">The caller's token; when it is the reason the operation stopped, the cancellation propagates.</param>
    /// <returns>The operation's own result, or a transport failure.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> requested cancellation.</exception>
    public static async Task<Result> ExecuteAsync(Func<Task<Result>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Checked up front so an already-abandoned call never reaches the network, and so the
        // "caller cancellation propagates" contract holds even for a stack that would otherwise
        // complete before noticing the token.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(TimeoutError());
        }
        catch (Exception exception) when (IsTransportFault(exception))
        {
            return Result.Failure(TransportError(exception));
        }
    }

    /// <summary>
    /// Runs a value-returning HTTP operation, converting transport faults into failures.
    /// </summary>
    /// <typeparam name="T">The success value type.</typeparam>
    /// <param name="operation">The operation, which already returns a <see cref="Result{T}"/> for every response it gets.</param>
    /// <param name="cancellationToken">The caller's token; when it is the reason the operation stopped, the cancellation propagates.</param>
    /// <returns>The operation's own result, or a transport failure.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> requested cancellation.</exception>
    public static async Task<Result<T>> ExecuteAsync<T>(Func<Task<Result<T>>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Checked up front so an already-abandoned call never reaches the network, and so the
        // "caller cancellation propagates" contract holds even for a stack that would otherwise
        // complete before noticing the token.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<T>(TimeoutError());
        }
        catch (Exception exception) when (IsTransportFault(exception))
        {
            return Result.Failure<T>(TransportError(exception));
        }
    }

    /// <summary>
    /// The fault set a client-side HTTP call can raise once responses are handled: the request
    /// never got an answer (<see cref="HttpRequestException"/>), the stream broke mid-body
    /// (<see cref="IOException"/>), or the payload could not be serialized or read
    /// (<see cref="JsonException"/>). Anything else is a genuine programming fault and keeps
    /// travelling as an exception.
    /// </summary>
    private static bool IsTransportFault(Exception exception) =>
        exception is HttpRequestException or IOException or JsonException;

    private static Error TransportError(Exception exception) =>
        // The exception text goes on Source, not Message: it is diagnostic detail, neither
        // localizable nor safe to render verbatim (rubric section 24).
        Error.Unexpected(TransportErrorCode, TransportMessage, exception.Message);

    private static Error TimeoutError() =>
        Error.Unexpected(TimeoutErrorCode, TimeoutMessage);
}
