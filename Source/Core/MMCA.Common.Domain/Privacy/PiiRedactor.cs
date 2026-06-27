using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using MMCA.Common.Domain.Attributes;

namespace MMCA.Common.Domain.Privacy;

/// <summary>
/// Produces a log- and telemetry-safe view of an object by masking every property marked with
/// <see cref="PiiAttribute"/>. This is the redaction half of the <see cref="PiiAttribute"/> contract
/// (see ADR-005): the framework deliberately logs scalar identifiers rather than whole entities, but
/// when an aggregate that carries a data subject's personal data must be written to a structured log
/// or a telemetry attribute, route it through <see cref="Redact(object?)"/> (or
/// <see cref="RedactToString(object?)"/>) so the personal data never leaves the process in clear text.
/// </summary>
/// <remarks>
/// Masking is shallow and value-erasing: a <see cref="PiiAttribute"/> property is replaced wholesale
/// with <see cref="RedactedToken"/> — not truncated or hashed — because even a value's length or hash
/// can leak information about a data subject. Non-PII properties pass through unchanged. Reflection
/// metadata is cached per type, so repeated redaction on a hot logging path is allocation-light.
/// </remarks>
public static class PiiRedactor
{
    /// <summary>The placeholder substituted for every <see cref="PiiAttribute"/>-marked value.</summary>
    public const string RedactedToken = "[REDACTED]";

    private const string UnreadableToken = "[unreadable]";

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<RedactableProperty>> Cache = new();

    private static readonly IReadOnlyDictionary<string, object?> Empty =
        ReadOnlyDictionary<string, object?>.Empty;

    /// <summary>
    /// Returns a map of the object's public readable properties with every
    /// <see cref="PiiAttribute"/>-marked value replaced by <see cref="RedactedToken"/>.
    /// </summary>
    /// <param name="value">The object to redact; <see langword="null"/> yields an empty map.</param>
    /// <returns>A property-name → (possibly redacted) value map.</returns>
    public static IReadOnlyDictionary<string, object?> Redact(object? value)
    {
        if (value is null)
        {
            return Empty;
        }

        var properties = GetProperties(value.GetType());
        var redacted = new Dictionary<string, object?>(properties.Count, StringComparer.Ordinal);
        foreach (var property in properties)
        {
            redacted[property.Name] = property.IsPii ? RedactedToken : property.Read(value);
        }

        return redacted;
    }

    /// <summary>
    /// Returns a single-line <c>TypeName { Prop = value, Pii = [REDACTED] }</c> rendering of
    /// <paramref name="value"/> with PII masked — convenient for a structured-log message argument.
    /// </summary>
    /// <param name="value">The object to render; <see langword="null"/> yields <c>"null"</c>.</param>
    /// <returns>A redacted, human-readable representation.</returns>
    public static string RedactToString(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var type = value.GetType();
        var properties = GetProperties(type);
        var builder = new StringBuilder(type.Name).Append(" { ");
        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(property.Name).Append(" = ");
            builder.Append(property.IsPii
                ? RedactedToken
                : Convert.ToString(property.Read(value), CultureInfo.InvariantCulture) ?? "null");
        }

        return builder.Append(" }").ToString();
    }

    /// <summary>
    /// Indicates whether <paramref name="type"/> declares any <see cref="PiiAttribute"/>-marked
    /// property — i.e. whether redaction would mask anything.
    /// </summary>
    /// <param name="type">The type to inspect.</param>
    /// <returns><see langword="true"/> if at least one property carries <see cref="PiiAttribute"/>.</returns>
    public static bool HasPii(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        foreach (var property in GetProperties(type))
        {
            if (property.IsPii)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<RedactableProperty> GetProperties(Type type) =>
        Cache.GetOrAdd(type, static t =>
        [
            .. t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Select(static p => new RedactableProperty(
                    p.Name,
                    p.IsDefined(typeof(PiiAttribute), inherit: false),
                    p)),
        ]);

    private sealed class RedactableProperty(string name, bool isPii, PropertyInfo info)
    {
        public string Name { get; } = name;

        public bool IsPii { get; } = isPii;

        public object? Read(object target)
        {
            try
            {
                return info.GetValue(target);
            }
            catch (TargetInvocationException)
            {
                // A property getter that throws must never break a logging call site.
                return UnreadableToken;
            }
        }
    }
}
