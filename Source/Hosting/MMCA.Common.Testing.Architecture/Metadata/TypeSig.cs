using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Text;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>The shape of a <see cref="TypeSig"/>.</summary>
internal enum TypeSigKind
{
    /// <summary>A type named by a definition or a reference (including the primitive types).</summary>
    Named,

    /// <summary>A closed or partially closed generic type (<c>List`1&lt;Foo&gt;</c>).</summary>
    GenericInstance,

    /// <summary>An array, single-dimensional or not.</summary>
    Array,

    /// <summary>An unmanaged pointer.</summary>
    Pointer,

    /// <summary>A managed reference (a <see langword="ref"/>, <see langword="out"/> or <see langword="in"/> parameter or local).</summary>
    ByReference,

    /// <summary>A pinned local.</summary>
    Pinned,

    /// <summary>A type carrying a <c>modreq</c> or <c>modopt</c> custom modifier.</summary>
    Modified,

    /// <summary>A generic type or method parameter.</summary>
    GenericParameter,

    /// <summary>A function pointer.</summary>
    FunctionPointer,
}

/// <summary>
/// One type as a signature, a base-type slot or an IL operand names it: the read-only type model the
/// architecture rules reason over. Names follow the ECMA-335 metadata spelling rather than the
/// reflection one, so a nested type reads <c>Namespace.Outer/Inner</c>, a generic type keeps its arity
/// marker (<c>List`1</c>), a closed generic lists its arguments in angle brackets
/// (<c>List`1&lt;System.Int32&gt;</c>) and a modified type carries its modifier as a suffix
/// (<c>System.Void modreq(System.Runtime.CompilerServices.IsExternalInit)</c>).
/// <para>
/// A wrapper (array, pointer, reference, pinned or modified type) reports the namespace of what it
/// wraps and that type's name plus its own suffix, and is never nested itself; a generic instance
/// reports the name, namespace and declaring type of its definition.
/// </para>
/// </summary>
internal sealed class TypeSig
{
    private static readonly string[] PrimitiveNames = BuildPrimitiveNames();

    private readonly string _suffix;

    private TypeSig(TypeSigKind kind, string name, string @namespace, TypeSig? declaringType, string suffix = "")
    {
        Kind = kind;
        Name = name;
        Namespace = @namespace;
        DeclaringType = declaringType;
        _suffix = suffix;
    }

    /// <summary>Gets the shape of the type.</summary>
    internal TypeSigKind Kind { get; }

    /// <summary>Gets the simple name: the arity marker stays, a wrapper adds its suffix to the wrapped name.</summary>
    internal string Name { get; }

    /// <summary>Gets the namespace; a nested named type reports its own (usually empty) metadata namespace.</summary>
    internal string Namespace { get; }

    /// <summary>Gets the declaring type of a nested type (or of the definition of a nested generic instance).</summary>
    internal TypeSig? DeclaringType { get; }

    /// <summary>Gets a value indicating whether the type is nested inside another.</summary>
    internal bool IsNested => DeclaringType is not null;

    /// <summary>
    /// Gets the definition this named type resolves to inside the module that produced it, or a nil
    /// handle for a type defined elsewhere (and for every shape other than <see cref="TypeSigKind.Named"/>).
    /// </summary>
    internal TypeDefinitionHandle Definition { get; private init; }

    /// <summary>Gets the wrapped type of every shape that wraps one (the generic definition of an instance).</summary>
    internal TypeSig? Element { get; private init; }

    /// <summary>Gets the type arguments of a generic instance (the parameter types of a function pointer).</summary>
    internal ImmutableArray<TypeSig> Arguments { get; private init; } = [];

    /// <summary>Gets the per-dimension lower and upper bounds of a general array; empty for a single-dimensional one.</summary>
    internal ImmutableArray<(int? Lower, int? Upper)> Dimensions { get; private init; } = [];

    /// <summary>Gets the return type of a function pointer.</summary>
    internal TypeSig? ReturnType { get; private init; }

    /// <summary>Gets the full metadata name (see the type summary for the spelling).</summary>
    internal string FullName => field ??= BuildFullName();

    /// <summary>Gets the innermost wrapped type: the generic definition of an instance, the element of an array.</summary>
    internal TypeSig ElementType => Element is null ? this : Element.ElementType;

    /// <summary>Creates a named type.</summary>
    internal static TypeSig CreateNamed(string @namespace, string name, TypeSig? declaring, TypeDefinitionHandle definition) =>
        new(TypeSigKind.Named, name, @namespace, declaring) { Definition = definition };

    /// <summary>Creates a primitive type (<c>System.Int32</c>, <c>System.String</c>, ...).</summary>
    internal static TypeSig CreatePrimitive(PrimitiveTypeCode code) =>
        CreateNamed("System", PrimitiveNames[(int)code], declaring: null, default);

    /// <summary>Creates a generic instance of <paramref name="definition"/>.</summary>
    internal static TypeSig CreateGenericInstance(TypeSig definition, ImmutableArray<TypeSig> arguments) =>
        new(TypeSigKind.GenericInstance, definition.Name, definition.Namespace, definition.DeclaringType)
        {
            Element = definition,
            Arguments = arguments,
        };

    /// <summary>Creates a single-dimensional, zero-based array.</summary>
    internal static TypeSig CreateVector(TypeSig element) => Wrap(TypeSigKind.Array, element, "[]");

    /// <summary>Creates a general array from its metadata shape.</summary>
    internal static TypeSig CreateArray(TypeSig element, ArrayShape shape)
    {
        var dimensions = ImmutableArray.CreateBuilder<(int? Lower, int? Upper)>(shape.Rank);
        for (var i = 0; i < shape.Rank; i++)
        {
            int? lower = i < shape.LowerBounds.Length ? shape.LowerBounds[i] : null;
            int? upper = i < shape.Sizes.Length ? lower + shape.Sizes[i] - 1 : null;
            dimensions.Add((lower, upper));
        }

        var bounds = dimensions.MoveToImmutable();
        return new(TypeSigKind.Array, element.Name + ArraySuffix(bounds), element.Namespace, declaringType: null, ArraySuffix(bounds))
        {
            Element = element,
            Dimensions = bounds,
        };
    }

    /// <summary>Creates an unmanaged pointer to <paramref name="element"/>.</summary>
    internal static TypeSig CreatePointer(TypeSig element) => Wrap(TypeSigKind.Pointer, element, "*");

    /// <summary>Creates a managed reference to <paramref name="element"/>.</summary>
    internal static TypeSig CreateByReference(TypeSig element) => Wrap(TypeSigKind.ByReference, element, "&");

    /// <summary>Creates a pinned local of type <paramref name="element"/>.</summary>
    internal static TypeSig CreatePinned(TypeSig element) => Wrap(TypeSigKind.Pinned, element, string.Empty);

    /// <summary>Creates a type carrying a custom modifier.</summary>
    internal static TypeSig CreateModified(TypeSig modifier, TypeSig unmodified, bool isRequired) =>
        Wrap(TypeSigKind.Modified, unmodified, (isRequired ? " modreq(" : " modopt(") + modifier.FullName + ")");

    /// <summary>Creates a generic parameter, named when its owner is known, positional (<c>!0</c>, <c>!!0</c>) otherwise.</summary>
    internal static TypeSig CreateGenericParameter(string? name, int position, bool isMethodParameter)
    {
        var prefix = isMethodParameter ? "!!" : "!";
        var resolved = string.IsNullOrEmpty(name) ? prefix + position.ToString(CultureInfo.InvariantCulture) : name;
        return new(TypeSigKind.GenericParameter, resolved, string.Empty, declaringType: null);
    }

    /// <summary>Creates a function pointer type.</summary>
    internal static TypeSig CreateFunctionPointer(MethodSignature<TypeSig> signature) =>
        new(TypeSigKind.FunctionPointer, "method", string.Empty, declaringType: null)
        {
            ReturnType = signature.ReturnType,
            Arguments = signature.ParameterTypes,
        };

    private static TypeSig Wrap(TypeSigKind kind, TypeSig element, string suffix) =>
        new(kind, element.Name + suffix, element.Namespace, declaringType: null, suffix) { Element = element };

    private static string ArraySuffix(ImmutableArray<(int? Lower, int? Upper)> dimensions)
    {
        if (dimensions.Length == 1 && dimensions[0] is { Lower: null, Upper: null })
        {
            return "[]";
        }

        var suffix = new StringBuilder("[");
        for (var i = 0; i < dimensions.Length; i++)
        {
            if (i > 0)
            {
                suffix.Append(',');
            }

            var (lower, upper) = dimensions[i];
            if (lower.HasValue || upper.HasValue)
            {
                suffix.Append(lower?.ToString(CultureInfo.InvariantCulture))
                    .Append("...")
                    .Append(upper?.ToString(CultureInfo.InvariantCulture));
            }
        }

        return suffix.Append(']').ToString();
    }

    private static string[] BuildPrimitiveNames()
    {
        var names = new string[(int)PrimitiveTypeCode.Object + 1];
        names[(int)PrimitiveTypeCode.Boolean] = "Boolean";
        names[(int)PrimitiveTypeCode.Byte] = "Byte";
        names[(int)PrimitiveTypeCode.SByte] = "SByte";
        names[(int)PrimitiveTypeCode.Char] = "Char";
        names[(int)PrimitiveTypeCode.Int16] = "Int16";
        names[(int)PrimitiveTypeCode.UInt16] = "UInt16";
        names[(int)PrimitiveTypeCode.Int32] = "Int32";
        names[(int)PrimitiveTypeCode.UInt32] = "UInt32";
        names[(int)PrimitiveTypeCode.Int64] = "Int64";
        names[(int)PrimitiveTypeCode.UInt64] = "UInt64";
        names[(int)PrimitiveTypeCode.Single] = "Single";
        names[(int)PrimitiveTypeCode.Double] = "Double";
        names[(int)PrimitiveTypeCode.IntPtr] = "IntPtr";
        names[(int)PrimitiveTypeCode.UIntPtr] = "UIntPtr";
        names[(int)PrimitiveTypeCode.Object] = "Object";
        names[(int)PrimitiveTypeCode.String] = "String";
        names[(int)PrimitiveTypeCode.TypedReference] = "TypedReference";
        names[(int)PrimitiveTypeCode.Void] = "Void";
        return names;
    }

    private string BuildFullName()
    {
        if (Kind == TypeSigKind.Named)
        {
            var qualified = Namespace.Length == 0 ? Name : Namespace + "." + Name;
            return DeclaringType is null ? qualified : DeclaringType.FullName + "/" + qualified;
        }

        if (Kind == TypeSigKind.GenericInstance)
        {
            return Element!.FullName + "<" + string.Join(",", Arguments.Select(a => a.FullName)) + ">";
        }

        if (Kind == TypeSigKind.FunctionPointer)
        {
            return "method " + ReturnType!.FullName + " *(" + string.Join(",", Arguments.Select(a => a.FullName)) + ")";
        }

        return Element is null ? Name : Element.FullName + _suffix;
    }
}
