using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace MMCA.Common.Testing.Architecture.Metadata;

/// <summary>
/// One IL instruction. The operand is a <see cref="MethodOperand"/>, a <see cref="FieldOperand"/>, a
/// <see cref="TypeSig"/> (a type token), the literal of an <c>ldstr</c>, or null for every other kind
/// (branch targets, numbers, locals and arguments, <c>calli</c> signatures), none of which a rule reads.
/// </summary>
internal sealed class IlInstruction(OpCode opCode, object? operand)
{
    /// <summary>Gets the opcode, prefixes (<c>constrained.</c>, <c>volatile.</c>) being instructions of their own.</summary>
    internal OpCode OpCode { get; } = opCode;

    /// <summary>Gets the operand (see the type summary).</summary>
    internal object? Operand { get; } = operand;

    /// <summary>Gets the instruction before this one in the body, or null for the first.</summary>
    internal IlInstruction? Previous { get; private set; }

    /// <summary>Links the instruction to its predecessor while the body is being read.</summary>
    internal void Follow(IlInstruction? previous) => Previous = previous;
}

/// <summary>A method an instruction names: called, constructed, loaded as a pointer or as a token.</summary>
/// <param name="DeclaringType">The type the method is declared on, as the reference spells it (generic arguments included).</param>
/// <param name="Name">The method name.</param>
/// <param name="ParameterTypes">The parameter types of the referenced signature.</param>
/// <param name="GenericArguments">The method's own type arguments when the operand is a generic method instance.</param>
/// <param name="DeclaringDefinition">The declaring type's definition in the reading module, or nil when it is defined elsewhere or reached through a generic instance.</param>
internal sealed record MethodOperand(
    TypeSig DeclaringType,
    string Name,
    ImmutableArray<TypeSig> ParameterTypes,
    ImmutableArray<TypeSig> GenericArguments,
    TypeDefinitionHandle DeclaringDefinition)
{
    /// <summary>Gets a value indicating whether the operand instantiates a generic method.</summary>
    internal bool IsGenericInstance { get; init; }
}

/// <summary>A field an instruction names.</summary>
/// <param name="DeclaringType">The type the field is declared on, as the reference spells it.</param>
/// <param name="Name">The field name.</param>
/// <param name="DeclaringDefinition">The declaring type's definition in the reading module, or nil when it is defined elsewhere or reached through a generic instance.</param>
internal sealed record FieldOperand(TypeSig DeclaringType, string Name, TypeDefinitionHandle DeclaringDefinition);

/// <summary>Decodes a method body's IL stream into <see cref="IlInstruction"/>s.</summary>
internal static class IlReader
{
    private static readonly OpCode?[] OneByteOpCodes = new OpCode?[0x100];
    private static readonly OpCode?[] TwoByteOpCodes = new OpCode?[0x100];

    static IlReader()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = (ushort)opCode.Value;
            if (opCode.Size == 1)
            {
                OneByteOpCodes[value] = opCode;
            }
            else
            {
                TwoByteOpCodes[value & 0xFF] = opCode;
            }
        }
    }

    /// <summary>Reads every instruction of an IL stream.</summary>
    internal static IReadOnlyList<IlInstruction> Read(MetadataModule module, BlobReader il, GenericScope scope)
    {
        var instructions = new List<IlInstruction>();
        IlInstruction? previous = null;

        while (il.RemainingBytes > 0)
        {
            var opCode = ReadOpCode(ref il);
            var instruction = new IlInstruction(opCode, ReadOperand(module, ref il, opCode, scope));
            instruction.Follow(previous);
            instructions.Add(instruction);
            previous = instruction;
        }

        return instructions;
    }

    private static OpCode ReadOpCode(ref BlobReader il)
    {
        var first = il.ReadByte();
        var opCode = first == 0xFE ? TwoByteOpCodes[il.ReadByte()] : OneByteOpCodes[first];
        return opCode ?? throw new InvalidOperationException($"Unknown IL opcode 0x{first:X2}.");
    }

    private static object? ReadOperand(MetadataModule module, ref BlobReader il, OpCode opCode, GenericScope scope)
    {
        var operandType = opCode.OperandType;

        if (operandType == OperandType.InlineString)
        {
            return module.Reader.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32()));
        }

        if (operandType == OperandType.InlineMethod)
        {
            return module.ResolveMethod(MetadataTokens.EntityHandle(il.ReadInt32()), scope);
        }

        if (operandType == OperandType.InlineField)
        {
            return module.ResolveField(MetadataTokens.EntityHandle(il.ReadInt32()), scope);
        }

        if (operandType == OperandType.InlineType)
        {
            return module.DecodeType(MetadataTokens.EntityHandle(il.ReadInt32()), scope);
        }

        if (operandType == OperandType.InlineTok)
        {
            return ResolveToken(module, MetadataTokens.EntityHandle(il.ReadInt32()), scope);
        }

        if (operandType == OperandType.InlineSwitch)
        {
            var targets = il.ReadInt32();
            il.Offset += 4 * targets;
            return null;
        }

        il.Offset += SkippedOperandSize(operandType);
        return null;
    }

    /// <summary>The operand of an <c>ldtoken</c>: a method, a field or a type.</summary>
    private static object ResolveToken(MetadataModule module, EntityHandle token, GenericScope scope)
    {
        var isMethod = token.Kind is HandleKind.MethodDefinition or HandleKind.MethodSpecification
            || token.Kind == HandleKind.MemberReference
                && module.Reader.GetMemberReference((MemberReferenceHandle)token).GetKind() == MemberReferenceKind.Method;
        if (isMethod)
        {
            return module.ResolveMethod(token, scope);
        }

        return token.Kind is HandleKind.FieldDefinition or HandleKind.MemberReference
            ? module.ResolveField(token, scope)
            : module.DecodeType(token, scope);
    }

    /// <summary>The size of an operand no rule reads: branch targets, numbers, locals, arguments and <c>calli</c> signatures.</summary>
    private static int SkippedOperandSize(OperandType operandType)
    {
        if (operandType == OperandType.InlineNone)
        {
            return 0;
        }

        if (operandType is OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar)
        {
            return 1;
        }

        if (operandType == OperandType.InlineVar)
        {
            return 2;
        }

        return operandType is OperandType.InlineI8 or OperandType.InlineR ? 8 : 4;
    }
}
