using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using MMCA.Common.Shared.Abstractions;

namespace MMCA.Common.Shared.Http;

/// <summary>
/// Reads an RFC 9457 Problem Details payload produced by an MMCA WebAPI back into the
/// <see cref="Error"/>/<see cref="Result"/> shape the caller started from. This is the reverse of
/// the API edge: <c>ApiControllerBase.HandleFailure</c> turns a failed <see cref="Result"/> into
/// Problem Details, and this reader turns that response back into a failed <see cref="Result"/>
/// with the original <see cref="ErrorType"/> preserved.
/// <para>
/// It lives in <c>MMCA.Common.Shared</c> because the client side of that round trip is
/// <c>MMCA.Common.UI</c>, which references Shared only. The reader uses nothing beyond the base
/// class library (<c>System.Text.Json</c>, <c>System.Net.Http</c>), so the Shared layer stays
/// framework-free.
/// </para>
/// <para><b>Payload shapes understood</b> (property lookup is case-insensitive, so both the
/// camelCase wire form and a PascalCase hand-built payload parse):</para>
/// <list type="number">
///   <item>
///     <b>MMCA error array.</b> The shape emitted by <c>ApiControllerBase.HandleFailure</c> and
///     <c>UnhandledResultFailureFilter</c>: an <c>errors</c> extension holding an array of objects
///     with <c>code</c>, <c>message</c>, <c>type</c>, <c>source</c>, <c>target</c>. Every field
///     round-trips, and <c>type</c> is parsed back into <see cref="ErrorType"/>, so this path is
///     lossless.
///   </item>
///   <item>
///     <b>Validation dictionary.</b> The standard ASP.NET Core shape emitted by
///     <c>ValidationExceptionHandler</c> and by automatic model validation: an <c>errors</c>
///     extension holding an object of <c>propertyName -&gt; [messages]</c>. One
///     <see cref="Error"/> is produced per message, with <c>Code</c> =
///     <c>"Validation.{propertyName}"</c> (<c>"Validation"</c> for an object-level rule with an
///     empty key) and <c>Target</c> = the property name. The type comes from the status code.
///   </item>
///   <item>
///     <b>Plain Problem Details.</b> No <c>errors</c> extension at all, as emitted by
///     <c>DomainExceptionHandler</c> and <c>GlobalExceptionHandler</c>: a single
///     <see cref="Error"/> is synthesized with <c>Code</c> = <c>"Http.{status}"</c> and
///     <c>Message</c> taken from <c>detail</c>, then <c>title</c>.
///   </item>
///   <item>
///     <b>Non-JSON or empty body.</b> A bare challenge, an HTML error page, or no body at all: a
///     single synthesized <see cref="Error"/> carrying the status code, exactly as case 3 but with
///     a generic message.
///   </item>
/// </list>
/// <para><b>Fidelity.</b> Only case 1 preserves the original <see cref="ErrorType"/> verbatim.
/// Cases 2 to 4 derive it from the HTTP status via <see cref="FromHttpStatusCode(int)"/>, which is
/// <b>lossy for 400 Bad Request</b>: the API maps <see cref="ErrorType.Validation"/>,
/// <see cref="ErrorType.Invariant"/> and <see cref="ErrorType.Failure"/> all onto 400, and the
/// reverse mapping can only pick one of the three (it picks
/// <see cref="ErrorType.Validation"/>). Callers that need the distinction must consume an endpoint
/// that emits the MMCA error array.</para>
/// </summary>
public static class ProblemDetailsResultReader
{
    /// <summary>
    /// Prefix of the synthesized error code used when the payload carries no machine-readable
    /// code of its own. The full code is this prefix followed by the HTTP status code, for example
    /// <c>"Http.404"</c>.
    /// </summary>
    public const string StatusErrorCodePrefix = "Http.";

    /// <summary>
    /// Error code reported when a success response carries no body (or a whitespace-only body) but
    /// the caller asked for a deserialized value.
    /// </summary>
    public const string EmptyResponseCode = "Http.EmptyResponse";

    /// <summary>
    /// Error code reported when a success response body cannot be deserialized into the requested
    /// type.
    /// </summary>
    public const string MalformedResponseCode = "Http.MalformedResponse";

    private const string ErrorsProperty = "errors";
    private const string CodeProperty = "code";
    private const string MessageProperty = "message";
    private const string TypeProperty = "type";
    private const string SourceProperty = "source";
    private const string TargetProperty = "target";
    private const string DetailProperty = "detail";
    private const string TitleProperty = "title";
    private const string StatusProperty = "status";
    private const string ValidationCodePrefix = "Validation";

    /// <summary>
    /// The exact reverse of <c>ErrorHttpMapping.ErrorTypeToStatusCode</c> in
    /// <c>MMCA.Common.API</c> for the statuses that map one-to-one. 400 is deliberately absent
    /// from the forward map's three-way collapse and resolves to
    /// <see cref="ErrorType.Validation"/> here.
    /// </summary>
    private static readonly FrozenDictionary<int, ErrorType> StatusCodeToErrorType =
        new Dictionary<int, ErrorType>
        {
            [500] = ErrorType.Unexpected,
            [401] = ErrorType.Unauthorized,
            [403] = ErrorType.Forbidden,
            [409] = ErrorType.Conflict,
            [404] = ErrorType.NotFound,
            [422] = ErrorType.UnprocessableEntity,
            [400] = ErrorType.Validation,
        }.ToFrozenDictionary();

    /// <summary>
    /// Reverses the API edge's <see cref="ErrorType"/> to HTTP status mapping.
    /// <list type="bullet">
    ///   <item>500 -&gt; <see cref="ErrorType.Unexpected"/></item>
    ///   <item>401 -&gt; <see cref="ErrorType.Unauthorized"/></item>
    ///   <item>403 -&gt; <see cref="ErrorType.Forbidden"/></item>
    ///   <item>409 -&gt; <see cref="ErrorType.Conflict"/></item>
    ///   <item>404 -&gt; <see cref="ErrorType.NotFound"/></item>
    ///   <item>422 -&gt; <see cref="ErrorType.UnprocessableEntity"/></item>
    ///   <item>400 -&gt; <see cref="ErrorType.Validation"/></item>
    ///   <item>any other 4xx -&gt; <see cref="ErrorType.Failure"/></item>
    ///   <item>anything else, 5xx included -&gt; <see cref="ErrorType.Unexpected"/></item>
    /// </list>
    /// <para>The forward mapping is many-to-one on 400, so this reverse mapping is lossy there:
    /// an <see cref="ErrorType.Invariant"/> or <see cref="ErrorType.Failure"/> that reached the
    /// wire as a bare 400 comes back as <see cref="ErrorType.Validation"/>. It is only used when
    /// the payload does not state the type itself.</para>
    /// </summary>
    /// <param name="statusCode">The HTTP status code of the response.</param>
    /// <returns>The error category the status code most likely came from.</returns>
    public static ErrorType FromHttpStatusCode(int statusCode)
    {
        if (StatusCodeToErrorType.TryGetValue(statusCode, out var errorType))
        {
            return errorType;
        }

        return statusCode is >= 400 and < 500 ? ErrorType.Failure : ErrorType.Unexpected;
    }

    /// <summary>
    /// Parses a Problem Details body into the errors it describes. This is the pure core of the
    /// reader: no HTTP, no I/O, no allocation of an <see cref="HttpResponseMessage"/>, so it can be
    /// tested directly against captured payloads.
    /// </summary>
    /// <param name="statusCode">
    /// The HTTP status code the body arrived with. Used to type errors the payload does not type
    /// itself. When zero or negative, the body's own <c>status</c> member is used instead.
    /// </param>
    /// <param name="jsonBody">The raw response body. May be <see langword="null"/>, empty, or not JSON at all.</param>
    /// <returns>
    /// The errors described by the payload. Never empty and never <see langword="null"/>: an
    /// unreadable body still yields one synthesized error carrying the status code, so the caller
    /// can always build a failed <see cref="Result"/> from it.
    /// </returns>
    public static IReadOnlyList<Error> ParseProblemDetails(int statusCode, string? jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody))
        {
            return [SynthesizeError(statusCode, null)];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(jsonBody);
        }
        catch (JsonException)
        {
            // Not JSON at all: a bare challenge, an HTML error page, a proxy response.
            return [SynthesizeError(statusCode, null)];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [SynthesizeError(statusCode, null)];
            }

            var effectiveStatus = ResolveStatus(statusCode, root);
            var fallbackType = FromHttpStatusCode(effectiveStatus);

            if (TryGetProperty(root, ErrorsProperty, out var errorsElement))
            {
                List<Error> parsed = [];

                if (errorsElement.ValueKind == JsonValueKind.Array)
                {
                    parsed = ReadErrorArray(errorsElement, effectiveStatus, fallbackType);
                }
                else if (errorsElement.ValueKind == JsonValueKind.Object)
                {
                    parsed = ReadValidationDictionary(errorsElement, fallbackType);
                }

                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }

            return [SynthesizeError(effectiveStatus, ReadDetailOrTitle(root))];
        }
    }

    /// <summary>
    /// Convenience wrapper over <see cref="ParseProblemDetails(int, string?)"/> that lifts the
    /// parsed errors into a failed <see cref="Result"/>.
    /// </summary>
    /// <param name="statusCode">The HTTP status code the body arrived with.</param>
    /// <param name="jsonBody">The raw response body.</param>
    /// <returns>A failed <see cref="Result"/> carrying every parsed error.</returns>
    public static Result ToFailureResult(int statusCode, string? jsonBody) =>
        Result.Failure(ParseProblemDetails(statusCode, jsonBody));

    /// <summary>
    /// Converts a response into a valueless <see cref="Result"/>: success when the status is 2xx,
    /// otherwise a failure carrying every error the Problem Details payload describes.
    /// </summary>
    /// <param name="response">The response to read. Its content is buffered as a string.</param>
    /// <param name="cancellationToken">Token used to cancel reading the body.</param>
    /// <returns>A success <see cref="Result"/>, or a failure carrying the parsed errors.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="response"/> is <see langword="null"/>.</exception>
    public static async Task<Result> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
        {
            return Result.Success();
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return ToFailureResult((int)response.StatusCode, body);
    }

    /// <summary>
    /// Converts a response into a <see cref="Result{T}"/>: on a 2xx the body is deserialized into
    /// <typeparamref name="T"/>, otherwise the Problem Details payload is parsed into errors.
    /// <para>
    /// A 2xx with no body (a 204, for instance) is a failure here, coded
    /// <see cref="EmptyResponseCode"/>: the caller explicitly asked for a value. Use the
    /// non-generic <see cref="ReadAsync(HttpResponseMessage, CancellationToken)"/> for endpoints
    /// that legitimately answer without a body.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The type to deserialize a successful body into.</typeparam>
    /// <param name="response">The response to read. Its content is buffered as a string.</param>
    /// <param name="options">
    /// Serializer options for the success path. Defaults to <see cref="JsonSerializerOptions.Web"/>,
    /// which matches the camelCase shape ASP.NET Core MVC emits.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel reading the body.</param>
    /// <returns>A success <see cref="Result{T}"/> carrying the deserialized value, or a failure.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="response"/> is <see langword="null"/>.</exception>
    public static async Task<Result<T>> ReadAsync<T>(
        HttpResponseMessage response,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var statusCode = (int)response.StatusCode;
        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return Result.Failure<T>(ParseProblemDetails(statusCode, body));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Result.Failure<T>(Error.Unexpected(
                EmptyResponseCode,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The request succeeded with HTTP status code {statusCode} but returned no body.")));
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, options ?? JsonSerializerOptions.Web);
            return value is null
                ? Result.Failure<T>(Error.Unexpected(
                    EmptyResponseCode,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The request succeeded with HTTP status code {statusCode} but its body deserialized to null.")))
                : Result.Success(value);
        }
        catch (JsonException exception)
        {
            return Result.Failure<T>(Error.Unexpected(MalformedResponseCode, exception.Message));
        }
    }

    private static async Task<string?> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // A truncated or aborted body is still a failure worth reporting; fall back to the
            // status-only error rather than surfacing a transport exception from a reader.
            return null;
        }
    }

    private static List<Error> ReadErrorArray(JsonElement errorsArray, int statusCode, ErrorType fallbackType)
    {
        var parsed = new List<Error>();

        foreach (var element in errorsArray.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                parsed.Add(ReadErrorObject(element, statusCode, fallbackType));
                continue;
            }

            // A payload that degraded the array to plain strings still carries usable messages.
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                parsed.Add(new Error(StatusErrorCode(statusCode), text, fallbackType));
            }
        }

        return parsed;
    }

    private static Error ReadErrorObject(JsonElement element, int statusCode, ErrorType fallbackType)
    {
        var code = ReadString(element, CodeProperty);
        var message = ReadString(element, MessageProperty);
        var type = ParseErrorType(ReadString(element, TypeProperty), fallbackType);

        return new Error(
            code ?? StatusErrorCode(statusCode),
            message ?? code ?? DefaultMessage(statusCode),
            type,
            ReadString(element, SourceProperty),
            ReadString(element, TargetProperty));
    }

    private static List<Error> ReadValidationDictionary(JsonElement errorsObject, ErrorType fallbackType)
    {
        var parsed = new List<Error>();

        foreach (var property in errorsObject.EnumerateObject())
        {
            var name = property.Name;
            var code = string.IsNullOrEmpty(name)
                ? ValidationCodePrefix
                : string.Concat(ValidationCodePrefix, ".", name);
            var target = string.IsNullOrEmpty(name) ? null : name;

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in property.Value.EnumerateArray())
                {
                    AddValidationError(parsed, message, code, target, fallbackType);
                }
            }
            else
            {
                AddValidationError(parsed, property.Value, code, target, fallbackType);
            }
        }

        return parsed;
    }

    private static void AddValidationError(
        List<Error> parsed,
        JsonElement element,
        string code,
        string? target,
        ErrorType fallbackType)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var message = element.GetString();
        if (!string.IsNullOrWhiteSpace(message))
        {
            parsed.Add(new Error(code, message, fallbackType, null, target));
        }
    }

    private static ErrorType ParseErrorType(string? typeName, ErrorType fallbackType) =>
        !string.IsNullOrWhiteSpace(typeName)
            && Enum.TryParse(typeName, ignoreCase: true, out ErrorType parsed)
            && Enum.IsDefined(parsed)
                ? parsed
                : fallbackType;

    private static Error SynthesizeError(int statusCode, string? message) =>
        new(
            StatusErrorCode(statusCode),
            string.IsNullOrWhiteSpace(message) ? DefaultMessage(statusCode) : message,
            FromHttpStatusCode(statusCode));

    private static string? ReadDetailOrTitle(JsonElement root) =>
        ReadString(root, DetailProperty) ?? ReadString(root, TitleProperty);

    private static int ResolveStatus(int statusCode, JsonElement root)
    {
        if (statusCode > 0)
        {
            return statusCode;
        }

        return TryGetProperty(root, StatusProperty, out var status)
            && status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var fromBody)
                ? fromBody
                : statusCode;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Case-insensitive property lookup. The wire form is camelCase (ASP.NET Core MVC's default
    /// naming policy), but a payload assembled by hand or by a differently-configured host can be
    /// PascalCase, and a reader that only understood one of the two would silently drop every
    /// error field.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        var match = element.EnumerateObject()
            .FirstOrDefault(property => string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase));

        if (match.Value.ValueKind == JsonValueKind.Undefined)
        {
            value = default;
            return false;
        }

        value = match.Value;
        return true;
    }

    private static string StatusErrorCode(int statusCode) =>
        string.Concat(StatusErrorCodePrefix, statusCode.ToString(CultureInfo.InvariantCulture));

    private static string DefaultMessage(int statusCode) =>
        string.Create(CultureInfo.InvariantCulture, $"The request failed with HTTP status code {statusCode}.");
}
