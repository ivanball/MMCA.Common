using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace MMCA.Common.Shared.Identifiers;

/// <summary>
/// <see cref="TypeConverter"/> that converts a strongly typed identifier to and from its primitive
/// and from text.
/// <para>
/// MVC binds a <c>[FromRoute]</c> or <c>[FromQuery]</c> scalar through
/// <c>TypeDescriptor.GetConverter</c>, not through <see cref="IParsable{TSelf}"/> (that path is
/// minimal APIs only), so a wrapper needs this converter for <c>GET /orders/42</c> to bind. The
/// <see cref="IParsable{TSelf}"/> implementation on the interface still carries minimal-API
/// endpoints and every hand-written <c>TryParse</c> call, and this converter delegates to it, so
/// both routes parse identically.
/// </para>
/// </summary>
/// <typeparam name="TSelf">The identifier type.</typeparam>
/// <typeparam name="TValue">The wrapped primitive.</typeparam>
public sealed class StronglyTypedIdTypeConverter<TSelf, TValue> : TypeConverter
    where TSelf : struct, IStronglyTypedId<TSelf, TValue>
    where TValue : notnull, IEquatable<TValue>
{
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string)
            || sourceType == typeof(TValue)
            || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc />
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string)
            || destinationType == typeof(TValue)
            || base.CanConvertTo(context, destinationType);

    /// <inheritdoc />
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object? value)
        => value switch
        {
            null => null,
            TValue primitive => TSelf.From(primitive),
            string text when StronglyTypedId.TryParse<TSelf, TValue>(text, culture, out var parsed) => parsed,
            string text => throw new FormatException(
                $"'{text}' is not a valid {typeof(TSelf).Name}."),
            _ => base.ConvertFrom(context, culture, value)
        };

    /// <inheritdoc />
    public override object? ConvertTo(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object? value,
        Type? destinationType)
    {
        if (value is TSelf identifier)
        {
            if (destinationType == typeof(TValue))
                return identifier.Value;

            if (destinationType == typeof(string))
                return Convert.ToString(identifier.Value, culture ?? CultureInfo.InvariantCulture);
        }

        return destinationType is null
            ? throw new ArgumentNullException(nameof(destinationType))
            : base.ConvertTo(context, culture, value, destinationType);
    }
}

/// <summary>
/// Registers <see cref="StronglyTypedIdTypeConverter{TSelf, TValue}"/> for the identifier types a
/// consumer declares, so MVC route and query binding works without a
/// <c>[TypeConverter]</c> attribute on every wrapper.
/// <para>
/// Call it once at startup (<c>services.AddStronglyTypedIds(typeof(OrderId).Assembly)</c> in
/// <c>MMCA.Common.API</c> is the wrapper). Registration is process-global and idempotent:
/// <see cref="TypeDescriptor"/> keeps the last attribute set registered for a type, so a repeated
/// call re-registers the same converter rather than stacking.
/// </para>
/// </summary>
public static class StronglyTypedIdTypeConverters
{
    /// <summary>
    /// Registers the converter for one identifier type.
    /// </summary>
    /// <param name="identifierType">A type implementing <see cref="IStronglyTypedId{TSelf, TValue}"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="identifierType"/> is not an identifier type.</exception>
    public static void Register(Type identifierType)
    {
        ArgumentNullException.ThrowIfNull(identifierType);

        var valueType = StronglyTypedId.GetValueType(identifierType)
            ?? throw new ArgumentException(
                $"'{identifierType.FullName}' does not implement IStronglyTypedId<{identifierType.Name}, TValue>.",
                nameof(identifierType));

        var converterType = typeof(StronglyTypedIdTypeConverter<,>).MakeGenericType(identifierType, valueType);

        TypeDescriptor.AddAttributes(identifierType, new TypeConverterAttribute(converterType));
    }

    /// <summary>
    /// Registers the converter for every identifier type declared in <paramref name="assemblies"/>.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The number of identifier types registered.</returns>
    public static int RegisterAll(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var registered = 0;

        foreach (var assembly in assemblies)
        {
            foreach (var candidate in GetIdentifierTypes(assembly))
            {
                Register(candidate);
                registered++;
            }
        }

        return registered;
    }

    /// <summary>
    /// The identifier types an assembly declares. A type that fails to load is skipped rather than
    /// failing the scan, the same tolerance the architecture map's loadable-types helper applies.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>Every value type in the assembly implementing the identifier contract for itself.</returns>
    public static IEnumerable<Type> GetIdentifierTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        Type?[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException loadFailure)
        {
            types = loadFailure.Types;
        }

        return
        [
            .. types
                .Where(t => t is { IsValueType: true, IsGenericTypeDefinition: false })
                .Select(t => t!)
                .Where(StronglyTypedId.IsStronglyTypedId)
        ];
    }
}
