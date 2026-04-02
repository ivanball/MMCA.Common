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
        public TIdentifier Parse<TIdentifier>()
        {
            var type = typeof(TIdentifier);

            if (type == typeof(string))
                return (TIdentifier)(object)(id ?? string.Empty);

            if (string.IsNullOrWhiteSpace(id))
                return default!;

            return ParseNonEmpty<TIdentifier>(id, type);
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
            return Enum.TryParse(type, id, ignoreCase: true, out var enumValue) ? (TIdentifier)enumValue! : default!;

        throw new FormatException($"Unsupported identifier type: {type.FullName}");
    }
}
