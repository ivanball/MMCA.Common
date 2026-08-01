using System.Globalization;

namespace MMCA.Common.Shared.Extensions;

/// <summary>
/// Provides extension methods for domain-layer operations using C# preview extension types.
/// </summary>
public static class DomainHelper
{
    // Extension on string that parses identifier strings into strongly-typed values.
    // Used by controllers to convert route parameters into entity identifiers
    // without coupling to a specific identifier type.
    extension(string? id)
    {
        /// <summary>
        /// Parses this string into the target identifier type.
        /// </summary>
        /// <typeparam name="TIdentifier">The target identifier type to parse into.</typeparam>
        /// <returns>The parsed identifier value, or the type's default for null/invalid input.</returns>
        /// <exception cref="FormatException">Thrown when <typeparamref name="TIdentifier"/> is not a supported type.</exception>
        /// <remarks>
        /// This overload coerces by design: malformed input is indistinguishable from a legitimate
        /// default value (<c>"maybe"</c> and <c>"false"</c> both yield <see langword="false"/> for
        /// <see cref="bool"/>, an unrecognized enum name yields the enum default, and <c>"abc"</c>
        /// yields <c>0</c> for numeric identifiers). That is the intended behavior for route
        /// identifiers, where an unparsable value degrades to a not-found lookup. When malformed
        /// input must be distinguished from a legitimate default (<see cref="bool"/> and enum
        /// callers especially), use <c>TryParse&lt;TIdentifier&gt;(out TIdentifier)</c> instead.
        /// </remarks>
        public TIdentifier Parse<TIdentifier>()
        {
            var type = typeof(TIdentifier);

            if (type == typeof(string))
                return (TIdentifier)(object)(id ?? string.Empty);

            if (string.IsNullOrWhiteSpace(id))
                return default!;

            return ParseNonEmpty<TIdentifier>(id, type);
        }

        /// <summary>
        /// Attempts to parse this string into the target identifier type, reporting success instead
        /// of coercing malformed input to a default value. Supports the same identifier types as
        /// <c>Parse&lt;TIdentifier&gt;()</c>.
        /// </summary>
        /// <typeparam name="TIdentifier">The target identifier type to parse into.</typeparam>
        /// <param name="result">
        /// When this method returns <see langword="true"/>, the parsed value; otherwise the type's
        /// default (<see cref="string.Empty"/> for <see cref="string"/>).
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the value parsed; <see langword="false"/> for null, empty,
        /// whitespace-only, or malformed input.
        /// </returns>
        /// <exception cref="FormatException">Thrown when <typeparamref name="TIdentifier"/> is not a supported type.</exception>
        public bool TryParse<TIdentifier>(out TIdentifier result)
        {
            var type = typeof(TIdentifier);

            if (type == typeof(string))
            {
                result = (TIdentifier)(object)(id ?? string.Empty);
                return id is not null;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                result = default!;
                return false;
            }

            return TryParseNonEmpty(id, type, out result);
        }
    }

    // Called from extension(string? id).Parse<T>() — IDE0051 false positive with preview extension types
#pragma warning disable IDE0051
    private static TIdentifier ParseNonEmpty<TIdentifier>(string id, Type type)
#pragma warning restore IDE0051
    {
        if (type == typeof(Guid))
            return Guid.TryParse(id, out var g) ? (TIdentifier)(object)g : (TIdentifier)(object)Guid.Empty;

        if (type == typeof(int))
            return int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? (TIdentifier)(object)i : (TIdentifier)(object)0;

        if (type == typeof(long))
            return long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? (TIdentifier)(object)l : (TIdentifier)(object)0L;

        return ParseOtherTypes<TIdentifier>(id, type);
    }

    private static TIdentifier ParseOtherTypes<TIdentifier>(string id, Type type)
    {
        if (type == typeof(ulong))
            return ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul) ? (TIdentifier)(object)ul : (TIdentifier)(object)0UL;

        if (type == typeof(bool))
            return bool.TryParse(id, out var b) ? (TIdentifier)(object)b : (TIdentifier)(object)false;

        if (type.IsEnum)
            return Enum.TryParse(type, id, ignoreCase: true, out var enumValue) ? (TIdentifier)enumValue : default!;

        throw new FormatException($"Unsupported identifier type: {type.FullName}");
    }

    // Called from extension(string? id).TryParse<T>(out T): IDE0051 false positive with preview extension types
#pragma warning disable IDE0051
    private static bool TryParseNonEmpty<TIdentifier>(string id, Type type, out TIdentifier result)
#pragma warning restore IDE0051
    {
        if (type == typeof(Guid))
        {
            var parsed = Guid.TryParse(id, out var g);
            result = (TIdentifier)(object)g;
            return parsed;
        }

        if (type == typeof(int))
        {
            var parsed = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i);
            result = (TIdentifier)(object)i;
            return parsed;
        }

        if (type == typeof(long))
        {
            var parsed = long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l);
            result = (TIdentifier)(object)l;
            return parsed;
        }

        return TryParseOtherTypes(id, type, out result);
    }

    private static bool TryParseOtherTypes<TIdentifier>(string id, Type type, out TIdentifier result)
    {
        if (type == typeof(ulong))
        {
            var parsed = ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul);
            result = (TIdentifier)(object)ul;
            return parsed;
        }

        if (type == typeof(bool))
        {
            var parsed = bool.TryParse(id, out var b);
            result = (TIdentifier)(object)b;
            return parsed;
        }

        if (type.IsEnum)
        {
            if (Enum.TryParse(type, id, ignoreCase: true, out var enumValue))
            {
                result = (TIdentifier)enumValue;
                return true;
            }

            result = default!;
            return false;
        }

        throw new FormatException($"Unsupported identifier type: {type.FullName}");
    }
}
