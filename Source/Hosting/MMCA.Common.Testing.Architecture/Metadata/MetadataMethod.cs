using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>One method definition: its signature, attributes and, on demand, its body.</summary>
internal sealed class MetadataMethod
{
    /// <summary>The code-type bits of the implementation flags (IL, native, OPTIL, runtime).</summary>
    private const int CodeTypeMask = 3;

    private readonly MetadataModule _module;
    private readonly int _relativeVirtualAddress;
    private readonly Lazy<MetadataMethodBody> _body;

    internal MetadataMethod(MetadataModule module, MetadataType declaringType, MethodDefinitionHandle handle)
    {
        var reader = module.Reader;
        var definition = reader.GetMethodDefinition(handle);
        var attributes = definition.Attributes;
        var implementation = definition.ImplAttributes;

        _module = module;
        _relativeVirtualAddress = definition.RelativeVirtualAddress;
        DeclaringType = declaringType;
        Handle = handle;
        MetadataToken = MetadataTokens.GetToken(handle);
        Name = reader.GetString(definition.Name);
        IsStatic = attributes.HasFlag(MethodAttributes.Static);
        IsVirtual = attributes.HasFlag(MethodAttributes.Virtual);
        IsAbstract = attributes.HasFlag(MethodAttributes.Abstract);

        // IL only: no abstract, P/Invoke, internal-call or unmanaged method, and a code type of IL
        // (native, OPTIL and runtime-implemented methods such as a delegate's Invoke have none).
        HasBody = !IsAbstract
            && !attributes.HasFlag(MethodAttributes.PinvokeImpl)
            && ((int)implementation & ((int)MethodImplAttributes.InternalCall | (int)MethodImplAttributes.Unmanaged | CodeTypeMask)) == 0;

        Scope = new GenericScope(declaringType.GenericParameterNames, module.GenericParameterNames(definition.GetGenericParameters()));
        var signature = definition.DecodeSignature(module.Provider, Scope);
        ReturnType = signature.ReturnType;
        ParameterTypes = signature.ParameterTypes;
        Attributes = module.Attributes(definition.GetCustomAttributes());
        Constraints = module.Constraints(definition.GetGenericParameters(), Scope);

        var parameterAttributes = new ImmutableArray<MetadataAttribute>[ParameterTypes.Length + 1];
        foreach (var parameterHandle in definition.GetParameters())
        {
            var parameter = reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber <= ParameterTypes.Length)
            {
                parameterAttributes[parameter.SequenceNumber] = module.Attributes(parameter.GetCustomAttributes());
            }
        }

        ReturnAttributes = parameterAttributes[0].IsDefault ? [] : parameterAttributes[0];
        Parameters = [.. ParameterTypes.Select((type, i) =>
            new MetadataParameter(type, parameterAttributes[i + 1].IsDefault ? [] : parameterAttributes[i + 1]))];

        _body = new Lazy<MetadataMethodBody>(ReadBody);
        module.Register(this);
    }

    /// <summary>Gets the type that declares the method.</summary>
    internal MetadataType DeclaringType { get; }

    /// <summary>Gets the method's definition handle.</summary>
    internal MethodDefinitionHandle Handle { get; }

    /// <summary>Gets the method's metadata token, as reflection's <see cref="MemberInfo.MetadataToken"/> reports it.</summary>
    internal int MetadataToken { get; }

    /// <summary>Gets the method name (<c>.ctor</c>, <c>get_Value</c>, <c>Some.Ns.IFoo.Bar</c> for an explicit implementation).</summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the signature-qualified name, <c>ReturnType DeclaringType::Name(ParameterTypes)</c>, which
    /// tells overloads apart.
    /// </summary>
    internal string FullName =>
        field ??= $"{ReturnType.FullName} {DeclaringType.FullName}::{Name}({string.Join(",", ParameterTypes.Select(p => p.FullName))})";

    /// <summary>Gets a value indicating whether the method is static.</summary>
    internal bool IsStatic { get; }

    /// <summary>Gets a value indicating whether the method is virtual (interface members and overrides included).</summary>
    internal bool IsVirtual { get; }

    /// <summary>Gets a value indicating whether the method is abstract.</summary>
    internal bool IsAbstract { get; }

    /// <summary>
    /// Gets a value indicating whether the method has IL: false for abstract, extern, P/Invoke,
    /// internal-call and runtime-implemented (delegate) methods.
    /// </summary>
    internal bool HasBody { get; }

    /// <summary>Gets the return type.</summary>
    internal TypeSig ReturnType { get; }

    /// <summary>Gets the parameter types, in order, the implicit <see langword="this"/> excluded.</summary>
    internal ImmutableArray<TypeSig> ParameterTypes { get; }

    /// <summary>Gets the parameters with their attributes.</summary>
    internal ImmutableArray<MetadataParameter> Parameters { get; }

    /// <summary>Gets the custom attributes declared on the method.</summary>
    internal ImmutableArray<MetadataAttribute> Attributes { get; }

    /// <summary>Gets the custom attributes declared on the return value.</summary>
    internal ImmutableArray<MetadataAttribute> ReturnAttributes { get; }

    /// <summary>Gets the constraint types of the method's generic parameters.</summary>
    internal ImmutableArray<TypeSig> Constraints { get; }

    /// <summary>Gets the local variable types of the body; empty without a body.</summary>
    internal ImmutableArray<TypeSig> Locals => _body.Value.Locals;

    /// <summary>Gets the body's instructions in order; empty without a body.</summary>
    internal IReadOnlyList<IlInstruction> Instructions => _body.Value.Instructions;

    /// <summary>Gets the generic scope the method's signatures and body are read in.</summary>
    internal GenericScope Scope { get; }

    /// <summary>True when the method carries an attribute with the given full metadata name.</summary>
    internal bool HasAttribute(string attributeFullName) =>
        Attributes.Any(a => string.Equals(a.FullName, attributeFullName, StringComparison.Ordinal));

    private MetadataMethodBody ReadBody()
    {
        if (!HasBody || _relativeVirtualAddress == 0)
        {
            return MetadataMethodBody.Empty;
        }

        var body = _module.GetMethodBody(_relativeVirtualAddress);
        ImmutableArray<TypeSig> locals = body.LocalSignature.IsNil
            ? []
            : _module.Reader.GetStandaloneSignature(body.LocalSignature).DecodeLocalSignature(_module.Provider, Scope);

        return new MetadataMethodBody(locals, IlReader.Read(_module, body.GetILReader(), Scope));
    }
}

/// <summary>A method body: its locals and its instructions.</summary>
/// <param name="Locals">The local variable types.</param>
/// <param name="Instructions">The instructions, in order.</param>
internal sealed record MetadataMethodBody(ImmutableArray<TypeSig> Locals, IReadOnlyList<IlInstruction> Instructions)
{
    /// <summary>Gets the body of a method that has none.</summary>
    internal static MetadataMethodBody Empty { get; } = new([], []);
}
