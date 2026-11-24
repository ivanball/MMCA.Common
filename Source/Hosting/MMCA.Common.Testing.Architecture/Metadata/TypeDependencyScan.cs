using System.Collections.Immutable;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>
/// Finds which names of a <see cref="DependencyNameTree"/> one type depends on. A type depends on
/// every type it names anywhere in its compiled form:
/// <list type="bullet">
/// <item>its base type, interfaces, generic-parameter constraints and custom attribute types;</item>
/// <item>the types of its fields, properties and events, and their attribute types;</item>
/// <item>each method's attribute types, constraints, return and parameter types (with their attributes) and local variable types;</item>
/// <item>every type an instruction names: a type token (<c>castclass</c>, <c>isinst</c>, <c>box</c>,
/// <c>newarr</c>, <c>ldtoken</c>, ...), the declaring type of a called, constructed or loaded method
/// or field, and the type arguments of a generic method instance;</item>
/// <item>all of the above inside the compiler-generated types nested in it (lambda closures, async and
/// iterator state machines, local-function displays), which count as the type that wrote them.</item>
/// </list>
/// Arrays, pointers and references count as their element type; a closed generic counts as its
/// definition and each argument; a generic parameter counts as nothing. A call to one of the type's own
/// methods, or a read of one of its own fields, is not a dependency.
/// </summary>
internal sealed class TypeDependencyScan
{
    private readonly MetadataType _subject;
    private readonly DependencyNameTree _names;
    private readonly int _enough;
    private readonly HashSet<object> _found = [];

    private TypeDependencyScan(MetadataType subject, DependencyNameTree names, int enough)
    {
        _subject = subject;
        _names = names;
        _enough = enough;
    }

    private bool Done => _found.Count >= _enough;

    /// <summary>True when the type depends on at least one of the names.</summary>
    internal static bool DependsOnAny(MetadataType type, DependencyNameTree names) =>
        Run(type, names, enough: 1) >= 1;

    /// <summary>True when the type depends on every one of the names.</summary>
    internal static bool DependsOnAll(MetadataType type, DependencyNameTree names) =>
        Run(type, names, names.TerminatedCount) == names.TerminatedCount;

    private static int Run(MetadataType type, DependencyNameTree names, int enough)
    {
        var scan = new TypeDependencyScan(type, names, enough);
        scan.CheckType(type);
        return scan._found.Count;
    }

    private void CheckType(MetadataType type)
    {
        CheckReference(type.BaseType);
        CheckAttributes(type.Attributes);
        CheckReferences(type.Interfaces);
        CheckReferences(type.Constraints);

        foreach (var field in type.Fields)
        {
            CheckAttributes(field.Attributes);
            CheckReference(field.Type);
        }

        foreach (var property in type.Properties)
        {
            CheckAttributes(property.Attributes);
            CheckReference(property.Type);
        }

        foreach (var @event in type.Events)
        {
            CheckAttributes(@event.Attributes);
            CheckReference(@event.Type);
            foreach (var method in @event.OtherMethods)
            {
                CheckMethodHeader(method);
                CheckReferences(method.Locals);
                CheckInstructions(method);
            }
        }

        foreach (var method in type.Methods.TakeWhile(_ => !Done))
        {
            CheckMethodHeader(method);
        }

        foreach (var method in type.Methods.TakeWhile(_ => !Done))
        {
            CheckReferences(method.Locals);
        }

        foreach (var method in type.Methods.TakeWhile(_ => !Done))
        {
            CheckInstructions(method);
        }

        foreach (var nested in type.NestedTypes.Where(n => n.IsCompilerGenerated).TakeWhile(_ => !Done))
        {
            CheckType(nested);
        }
    }

    private void CheckMethodHeader(MetadataMethod method)
    {
        CheckAttributes(method.Attributes);
        CheckReferences(method.Constraints);
        CheckAttributes(method.ReturnAttributes);
        CheckReference(method.ReturnType);

        foreach (var parameter in method.Parameters)
        {
            CheckAttributes(parameter.Attributes);
            CheckReference(parameter.Type);
        }
    }

    private void CheckInstructions(MetadataMethod method)
    {
        foreach (var instruction in method.Instructions)
        {
            switch (instruction.Operand)
            {
                case TypeSig type:
                    CheckReference(type);
                    break;

                case MethodOperand { IsGenericInstance: true } genericMethod:
                    CheckReference(genericMethod.DeclaringType);
                    CheckReferences(genericMethod.GenericArguments);
                    break;

                case MethodOperand callee when callee.DeclaringDefinition != _subject.Handle:
                    CheckReference(callee.DeclaringType);
                    break;

                case FieldOperand field when field.DeclaringDefinition != _subject.Handle:
                    CheckReference(field.DeclaringType);
                    break;

                default:
                    // A call or field read on the type itself, a string literal, or an operand that names no type.
                    break;
            }
        }
    }

    private void CheckAttributes(ImmutableArray<MetadataAttribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            CheckReference(attribute.AttributeType);
        }
    }

    private void CheckReferences(ImmutableArray<TypeSig> types)
    {
        foreach (var type in types)
        {
            CheckReference(type);
        }
    }

    /// <summary>Checks one named type, recursing into array elements and generic arguments.</summary>
    private void CheckReference(TypeSig? type)
    {
        if (type is null || type.Kind == TypeSigKind.GenericParameter)
        {
            return;
        }

        if (type.Kind is TypeSigKind.Array or TypeSigKind.Pointer or TypeSigKind.ByReference)
        {
            if (type.Element!.Kind == TypeSigKind.GenericParameter)
            {
                return;
            }

            CheckReference(type.Element);
        }

        foreach (var match in _names.Match(type))
        {
            _found.Add(match);
        }

        if (type.Kind == TypeSigKind.GenericInstance)
        {
            CheckReferences(type.Arguments);
        }
    }
}
