using System.Reflection.Emit;
using MMCA.Common.Mcp;
using MMCA.Common.Testing.Architecture;
using ModelContextProtocol.Server;

namespace MMCA.Common.Architecture.Tests.Api;

/// <summary>
/// Adversarial coverage for the MCP tool access declaration rule, over assemblies built at run time so
/// each case holds exactly the tool shapes it needs. Emitted rather than written as fixtures in this
/// project because the permission coverage rule scans this assembly and would count them.
/// </summary>
public sealed class McpToolAccessDeclarationTests
{
    private const string Permission = "fixture:mcp-tool";

    private enum Declares
    {
        None,
        Permission,
        Public,
        Both,
    }

    [Fact]
    public void ToolsDeclaringExactlyOne_Pass() =>
        FluentActions.Invoking(() => ArchitectureRules.McpToolsDeclareAccess(
                [Fixture(("Gated", Declares.Permission), ("Open", Declares.Public))]))
            .Should().NotThrow();

    [Fact]
    public void ToolWithNeitherDeclaration_FailsNamingIt()
    {
        var act = () => ArchitectureRules.McpToolsDeclareAccess([Fixture(("Undeclared", Declares.None))]);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("Undeclared declares neither [McpToolPermission] nor [McpToolPublic]");
    }

    [Fact]
    public void ToolWithBothDeclarations_FailsNamingIt()
    {
        var act = () => ArchitectureRules.McpToolsDeclareAccess([Fixture(("Contradictory", Declares.Both))]);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("Contradictory declares both [McpToolPermission] and [McpToolPublic]");
    }

    [Fact]
    public void AMethodThatIsNotATool_IsIgnored()
    {
        var count = ArchitectureRules.CountMcpTools([Fixture(("Tool", Declares.Public))]);

        count.Should().Be(1, "the emitted type's other members are not MCP tools");
    }

    // The declarations do not inherit, so an override of a tool has to state its own.
    [Fact]
    public void AnOverrideOfATool_WithNoDeclarationOfItsOwn_FailsNamingIt()
    {
        var act = () => ArchitectureRules.McpToolsDeclareAccess([OverrideFixture(Declares.None)]);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("DerivedTools.Shared declares neither");
    }

    [Fact]
    public void AnOverrideOfATool_WithItsOwnDeclaration_Passes() =>
        FluentActions.Invoking(() => ArchitectureRules.McpToolsDeclareAccess([OverrideFixture(Declares.Permission)]))
            .Should().NotThrow();

    [Fact]
    public void ToolsOnNestedAndGenericTypes_AreScanned()
    {
        var act = () => ArchitectureRules.McpToolsDeclareAccess([NestedAndGenericFixture()]);

        var message = act.Should().Throw<Exception>().Which.Message;
        message.Should().Contain("Outer+Inner.NestedTool declares neither");
        message.Should().Contain("GenericTools`1.GenericTool declares neither");
    }

    private static ModuleBuilder NewModule() =>
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"McpFixture{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Fixtures");

    private static MethodBuilder DefineMethod(TypeBuilder type, string name, MethodAttributes attributes)
    {
        var method = type.DefineMethod(name, attributes, typeof(void), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ret);
        return method;
    }

    private static void Declare(MethodBuilder method, Declares declaration)
    {
        if (declaration is Declares.Permission or Declares.Both)
        {
            method.SetCustomAttribute(new CustomAttributeBuilder(typeof(McpToolPermissionAttribute).GetConstructor([typeof(string)])!, [Permission]));
        }

        if (declaration is Declares.Public or Declares.Both)
        {
            method.SetCustomAttribute(new CustomAttributeBuilder(typeof(McpToolPublicAttribute).GetConstructor(Type.EmptyTypes)!, []));
        }
    }

    private static void DefineTool(TypeBuilder type, string name, Declares declaration, MethodAttributes attributes = MethodAttributes.Public | MethodAttributes.Static)
    {
        var method = DefineMethod(type, name, attributes);
        method.SetCustomAttribute(new CustomAttributeBuilder(typeof(McpServerToolAttribute).GetConstructor(Type.EmptyTypes)!, []));
        Declare(method, declaration);
    }

    private static Assembly Fixture(params (string Name, Declares Declaration)[] tools)
    {
        var module = NewModule();
        var type = module.DefineType("FixtureTools", TypeAttributes.Public);

        foreach ((var name, var declaration) in tools)
        {
            DefineTool(type, name, declaration);
        }

        DefineMethod(type, "NotATool", MethodAttributes.Public | MethodAttributes.Static);
        return type.CreateType().Assembly;
    }

    private static Assembly OverrideFixture(Declares overrideDeclares)
    {
        const MethodAttributes virtualMethod = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;

        var module = NewModule();
        var baseType = module.DefineType("BaseTools", TypeAttributes.Public);
        DefineTool(baseType, "Shared", Declares.Public, virtualMethod);

        var derived = module.DefineType("DerivedTools", TypeAttributes.Public, baseType);
        Declare(DefineMethod(derived, "Shared", virtualMethod), overrideDeclares);

        baseType.CreateType();
        return derived.CreateType().Assembly;
    }

    private static Assembly NestedAndGenericFixture()
    {
        var module = NewModule();

        var outer = module.DefineType("Outer", TypeAttributes.Public);
        var inner = outer.DefineNestedType("Inner", TypeAttributes.NestedPublic);
        DefineTool(inner, "NestedTool", Declares.None);

        var generic = module.DefineType("GenericTools`1", TypeAttributes.Public);
        generic.DefineGenericParameters("T");
        DefineTool(generic, "GenericTool", Declares.None);

        outer.CreateType();
        inner.CreateType();
        return generic.CreateType().Assembly;
    }
}
