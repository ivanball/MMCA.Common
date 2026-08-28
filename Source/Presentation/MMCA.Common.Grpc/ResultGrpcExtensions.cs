using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Grpc.Core;
using MMCA.Common.Grpc.Exceptions;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Grpc;

/// <summary>
/// Extensions that bridge the <see cref="Result"/>/<see cref="Result{T}"/> pattern with the
/// gRPC transport layer. The mapping mirrors the HTTP error mapping used by
/// <c>ApiControllerBase.HandleFailure</c> in <c>MMCA.Common.API</c>:
/// <list type="bullet">
///   <item><see cref="ErrorType.Validation"/>, <see cref="ErrorType.Invariant"/>, <see cref="ErrorType.Failure"/> → <see cref="StatusCode.InvalidArgument"/></item>
///   <item><see cref="ErrorType.NotFound"/> → <see cref="StatusCode.NotFound"/></item>
///   <item><see cref="ErrorType.Conflict"/> → <see cref="StatusCode.Aborted"/></item>
///   <item><see cref="ErrorType.Unauthorized"/> → <see cref="StatusCode.Unauthenticated"/></item>
///   <item><see cref="ErrorType.Forbidden"/> → <see cref="StatusCode.PermissionDenied"/></item>
///   <item><see cref="ErrorType.UnprocessableEntity"/> → <see cref="StatusCode.FailedPrecondition"/></item>
///   <item><see cref="ErrorType.Unexpected"/> → <see cref="StatusCode.Internal"/></item>
/// </list>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1708:Identifiers should differ by more than case",
    Justification = "False positive: with multiple extension(T) blocks in one static class, CA1708 flags the compiler-generated grouping members as case-colliding. No user-visible identifier differs only by case.")]
public static class ResultGrpcExtensions
{
    /// <summary>
    /// Immutable mapping from domain error types to gRPC status codes. Mirrors
    /// <c>ErrorHttpMapping.ErrorTypeToStatusCode</c> in <c>MMCA.Common.API</c>.
    /// </summary>
    private static readonly FrozenDictionary<ErrorType, StatusCode> ErrorTypeToStatusCode =
        new Dictionary<ErrorType, StatusCode>
        {
            [ErrorType.Validation] = StatusCode.InvalidArgument,
            [ErrorType.Invariant] = StatusCode.InvalidArgument,
            [ErrorType.NotFound] = StatusCode.NotFound,
            [ErrorType.Conflict] = StatusCode.Aborted,
            [ErrorType.Unauthorized] = StatusCode.Unauthenticated,
            [ErrorType.Forbidden] = StatusCode.PermissionDenied,
            [ErrorType.UnprocessableEntity] = StatusCode.FailedPrecondition,
            [ErrorType.Failure] = StatusCode.InvalidArgument,
            [ErrorType.Unexpected] = StatusCode.Internal,
        }.ToFrozenDictionary();

    extension(ErrorType errorType)
    {
        /// <summary>
        /// Resolves the gRPC <see cref="StatusCode"/> for the given <see cref="ErrorType"/>,
        /// falling back to <see cref="StatusCode.InvalidArgument"/> if no explicit mapping exists.
        /// </summary>
        /// <returns>The corresponding gRPC status code.</returns>
        public StatusCode ToGrpcStatusCode() =>
            ErrorTypeToStatusCode.GetValueOrDefault(errorType, StatusCode.InvalidArgument);
    }

    extension(Result result)
    {
        /// <summary>
        /// Throws a <see cref="ResultFailureException"/> if the result is a failure, allowing
        /// gRPC service implementations to surface domain errors with a single guard clause.
        /// The <c>GrpcResultExceptionInterceptor</c> server interceptor will translate the
        /// exception into an <see cref="RpcException"/> with the right status code.
        /// </summary>
        /// <exception cref="ResultFailureException">Thrown when the result is a failure.</exception>
        public void ThrowIfFailure()
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.IsFailure)
            {
                throw new ResultFailureException(result.Errors);
            }
        }
    }

    extension<T>(Result<T> result)
    {
        /// <summary>
        /// Returns the success value or throws <see cref="ResultFailureException"/> on failure.
        /// </summary>
        /// <returns>The success value carried by the result.</returns>
        /// <exception cref="ResultFailureException">Thrown when the result is a failure.</exception>
        public T UnwrapOrThrow()
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.IsFailure)
            {
                throw new ResultFailureException(result.Errors);
            }

            return result.Value!;
        }
    }

    extension(IReadOnlyList<Error> errors)
    {
        /// <summary>
        /// Builds an <see cref="RpcException"/> from a list of <see cref="Error"/> instances.
        /// The <b>most severe</b> error's <see cref="Error.Type"/> determines the status code
        /// (<see cref="ErrorTypeSeverity"/>, the same ranking the HTTP edge uses), so an aggregate
        /// built by <see cref="Result.Combine"/> cannot be downgraded by error ordering: an
        /// <see cref="ErrorType.Unauthorized"/> travelling behind an <see cref="ErrorType.Validation"/>
        /// still answers <see cref="StatusCode.Unauthenticated"/>. Ties keep the earliest error.
        /// All errors are serialized into the trailers as <c>error-{i}-code</c>,
        /// <c>error-{i}-message</c>, and <c>error-{i}-type</c> entries for consumers that need
        /// structured access to the failure; ranking picks the status only.
        /// </summary>
        /// <returns>An <see cref="RpcException"/> populated with status, detail, and trailing metadata.</returns>
        public RpcException ToRpcException()
        {
            ArgumentNullException.ThrowIfNull(errors);

            var statusCode = errors.Count > 0
                ? ErrorTypeSeverity.MostSevere(errors).Type.ToGrpcStatusCode()
                : StatusCode.Internal;

            var detail = errors.Count > 0
                ? string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"))
                : "Unspecified failure";

            var trailers = new Metadata();
            for (var i = 0; i < errors.Count; i++)
            {
                var error = errors[i];
                trailers.Add(string.Create(CultureInfo.InvariantCulture, $"error-{i}-code"), error.Code);
                trailers.Add(string.Create(CultureInfo.InvariantCulture, $"error-{i}-message"), error.Message);
                trailers.Add(string.Create(CultureInfo.InvariantCulture, $"error-{i}-type"), error.Type.ToString());
                if (!string.IsNullOrEmpty(error.Source))
                {
                    trailers.Add(string.Create(CultureInfo.InvariantCulture, $"error-{i}-source"), error.Source);
                }

                if (!string.IsNullOrEmpty(error.Target))
                {
                    trailers.Add(string.Create(CultureInfo.InvariantCulture, $"error-{i}-target"), error.Target);
                }
            }

            return new RpcException(new Status(statusCode, detail), trailers);
        }
    }

    extension(Metadata? trailers)
    {
        /// <summary>
        /// Reconstructs the <see cref="Error"/> list from the <c>error-{i}-*</c> trailers written by
        /// <see cref="ToRpcException"/>, the exact inverse of the encoder. Index-based iteration
        /// starts at zero and stops at the first missing <c>error-{i}-code</c>, matching the
        /// contiguous layout the encoder writes.
        /// <para>
        /// A missing <c>error-{i}-message</c> decodes as the empty string and a missing
        /// <c>error-{i}-source</c>/<c>error-{i}-target</c> as <see langword="null"/>, mirroring the
        /// encoder's decision to omit an empty source or target entirely. An unrecognized
        /// <c>error-{i}-type</c> falls back to <see cref="ErrorType.Failure"/> rather than throwing,
        /// so a newer peer that adds an error type cannot break an older client.
        /// </para>
        /// </summary>
        /// <returns>
        /// The decoded errors, empty when the trailers carry no structured failure (a pure
        /// transport fault such as a reset connection or an exceeded deadline).
        /// </returns>
        public IReadOnlyList<Error> ToErrors()
        {
            if (trailers is null || trailers.Count == 0)
            {
                return [];
            }

            var errors = new List<Error>();
            var index = 0;
            while (true)
            {
                var indexText = index.ToString(CultureInfo.InvariantCulture);
                var code = trailers.GetValue($"error-{indexText}-code");
                if (code is null)
                {
                    break;
                }

                var message = trailers.GetValue($"error-{indexText}-message") ?? string.Empty;
                var typeText = trailers.GetValue($"error-{indexText}-type");
                var source = trailers.GetValue($"error-{indexText}-source");
                var target = trailers.GetValue($"error-{indexText}-target");

                errors.Add(BuildError(ParseErrorType(typeText), code, message, source, target));
                index++;
            }

            return errors;
        }
    }

    extension(RpcException exception)
    {
        /// <summary>
        /// Converts a caught <see cref="RpcException"/> back into a failed <see cref="Result"/>,
        /// closing the round trip opened by <see cref="ToRpcException"/>. The structured
        /// <c>error-{i}-*</c> trailers win when present; a transport-level fault that carries none
        /// degrades to a single <see cref="ErrorType.Failure"/> error coded
        /// <c>Grpc.{StatusCode}</c> carrying the RPC detail.
        /// </summary>
        /// <param name="source">
        /// Origin context stamped on the synthesized transport error. Defaults to the calling
        /// member's name.
        /// </param>
        /// <returns>A failed <see cref="Result"/>; never a success.</returns>
        public Result ToResult([CallerMemberName] string source = "")
        {
            ArgumentNullException.ThrowIfNull(exception);

            var errors = exception.Trailers.ToErrors();

            return errors.Count > 0
                ? Result.Failure(errors)
                : Result.Failure(TransportError(exception, source));
        }

        /// <summary>
        /// Converts a caught <see cref="RpcException"/> back into a failed <see cref="Result{T}"/>,
        /// closing the round trip opened by <see cref="ToRpcException"/>. The structured
        /// <c>error-{i}-*</c> trailers win when present; a transport-level fault that carries none
        /// degrades to a single <see cref="ErrorType.Failure"/> error coded
        /// <c>Grpc.{StatusCode}</c> carrying the RPC detail.
        /// </summary>
        /// <typeparam name="T">The value type the failed result stands in for.</typeparam>
        /// <param name="source">
        /// Origin context stamped on the synthesized transport error. Defaults to the calling
        /// member's name.
        /// </param>
        /// <returns>A failed <see cref="Result{T}"/>; never a success.</returns>
        public Result<T> ToResult<T>([CallerMemberName] string source = "")
        {
            ArgumentNullException.ThrowIfNull(exception);

            var errors = exception.Trailers.ToErrors();

            return errors.Count > 0
                ? Result.Failure<T>(errors)
                : Result.Failure<T>(TransportError(exception, source));
        }
    }

    /// <summary>
    /// The <see cref="Error"/> factory per <see cref="ErrorType"/>. A lookup table rather than a
    /// switch so adding an error type stays a one-line entry instead of pushing the decoder past
    /// the cyclomatic-complexity ceiling.
    /// </summary>
    private static readonly FrozenDictionary<ErrorType, Func<string, string, string?, string?, Error>> ErrorFactories =
        new Dictionary<ErrorType, Func<string, string, string?, string?, Error>>
        {
            [ErrorType.Validation] = Error.Validation,
            [ErrorType.Invariant] = Error.Invariant,
            [ErrorType.NotFound] = Error.NotFoundError,
            [ErrorType.Conflict] = Error.Conflict,
            [ErrorType.Unauthorized] = Error.Unauthorized,
            [ErrorType.Forbidden] = Error.Forbidden,
            [ErrorType.UnprocessableEntity] = Error.UnprocessableEntity,
            [ErrorType.Failure] = Error.Failure,
            [ErrorType.Unexpected] = Error.Unexpected,
        }.ToFrozenDictionary();

    /// <summary>
    /// Parses the <see cref="ErrorType"/> from the enum-name wire form the encoder writes,
    /// defaulting to <see cref="ErrorType.Failure"/> on any mismatch.
    /// </summary>
    private static ErrorType ParseErrorType(string? typeText) =>
        Enum.TryParse<ErrorType>(typeText, ignoreCase: false, out var errorType)
            ? errorType
            : ErrorType.Failure;

    /// <summary>Builds an <see cref="Error"/> from the trailer fields via the factory for its type.</summary>
    private static Error BuildError(ErrorType errorType, string code, string message, string? source, string? target) =>
        ErrorFactories.TryGetValue(errorType, out var factory)
            ? factory(code, message, source, target)
            : Error.Failure(code, message, source, target);

    /// <summary>
    /// Builds the stand-in error for an <see cref="RpcException"/> that carries no structured
    /// trailers, so a connection reset or an exceeded deadline still reaches the caller as a
    /// <see cref="Result"/> failure rather than an exception.
    /// </summary>
    private static Error TransportError(RpcException exception, string source) =>
        Error.Failure(
            code: $"Grpc.{exception.StatusCode}",
            message: exception.Status.Detail,
            source: source);
}
