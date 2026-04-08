using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Grpc.Exceptions;

/// <summary>
/// Thrown by gRPC service implementations to surface a <see cref="Result"/> failure as
/// a transport-layer error. The <see cref="Interceptors.GrpcResultExceptionInterceptor"/>
/// catches this exception and translates it into an <see cref="global::Grpc.Core.RpcException"/>
/// with the appropriate <see cref="global::Grpc.Core.StatusCode"/> and structured error trailers,
/// mirroring the HTTP/Problem Details mapping in <c>ApiControllerBase.HandleFailure</c>.
/// <para>
/// Service implementations should not construct or throw this exception directly — call
/// <c>result.ThrowIfFailure()</c> from <see cref="ResultGrpcExtensions"/> instead.
/// </para>
/// </summary>
public sealed class ResultFailureException : Exception
{
    /// <summary>Initializes a new instance with no errors. Provided to satisfy CA1032.</summary>
    public ResultFailureException()
        : base("Result failure") => Errors = [];

    /// <summary>Initializes a new instance with a custom message and no errors.</summary>
    /// <param name="message">The exception message.</param>
    public ResultFailureException(string message)
        : base(message) => Errors = [];

    /// <summary>Initializes a new instance with a custom message, no errors, and an inner exception.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The wrapped inner exception.</param>
    public ResultFailureException(string message, Exception innerException)
        : base(message, innerException) => Errors = [];

    /// <summary>Initializes a new instance carrying the errors from a failing <see cref="Result"/>.</summary>
    /// <param name="errors">The errors carried by the failing result.</param>
    public ResultFailureException(IReadOnlyList<Error> errors)
        : base(BuildMessage(errors)) => Errors = errors;

    /// <summary>Gets the errors carried by the failing result. Empty for the parameterless / message-only constructors.</summary>
    public IReadOnlyList<Error> Errors { get; }

    private static string BuildMessage(IReadOnlyList<Error> errors) =>
        errors.Count == 0
            ? "Result failure"
            : string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"));
}
