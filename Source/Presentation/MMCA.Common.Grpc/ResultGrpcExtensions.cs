using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
        /// The first error's <see cref="Error.Type"/> determines the status code; all errors are
        /// serialized into the trailers as <c>error-{i}-code</c>, <c>error-{i}-message</c>, and
        /// <c>error-{i}-type</c> entries for consumers that need structured access to the failure.
        /// </summary>
        /// <returns>An <see cref="RpcException"/> populated with status, detail, and trailing metadata.</returns>
        public RpcException ToRpcException()
        {
            ArgumentNullException.ThrowIfNull(errors);

            var statusCode = errors.Count > 0
                ? errors[0].Type.ToGrpcStatusCode()
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
}
