namespace MMCA.Common.Architecture.Tests.Passkeys;

/// <summary>
/// The passkeys boundary (ADR-139, the ADR-134 adapter shape), asserted against the compiled framework:
/// no core layer, nor the service defaults, the test infrastructure or the MCP package, names a type
/// from <c>MMCA.Common.Passkeys</c> or from the WebAuthn libraries behind it, or references their
/// assemblies. WebAuthn verification (Fido2NetLib, and through it NSec.Cryptography and the native
/// libsodium) stays in the opt-in package, so a host that does not reference it carries none of them.
/// The compile-time half is <c>EnforcePasskeysBoundary</c> in
/// <c>Source/Build/MMCA.Common.LayerEnforcement.targets</c>.
/// </summary>
public sealed class PasskeysIsolationTests
{
    private static readonly string[] ForbiddenNamespaces = ["MMCA.Common.Passkeys", "Fido2NetLib", "NSec"];

    private static readonly string[] ForbiddenAssemblyPrefixes = ["MMCA.Common.Passkeys", "Fido2", "NSec", "libsodium"];

    public static TheoryData<string> GuardedAssemblies =>
    [
        .. new CommonArchitectureMap().Layers.Select(layer => layer.Assembly.GetName().Name!),
        typeof(Aspire.Extensions).Assembly.GetName().Name!,
        typeof(Testing.Fixtures.CrossServiceFixtureBase).Assembly.GetName().Name!,
        typeof(Testing.Aspire.Fixtures.AppHostFixtureBase).Assembly.GetName().Name!,
        typeof(Mcp.McpSettings).Assembly.GetName().Name!,
    ];

    [Theory]
    [MemberData(nameof(GuardedAssemblies))]
    public void FrameworkAssembly_NamesNoPasskeysOrWebAuthnType(string assemblyName)
    {
        var result = Types.InAssembly(Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(ForbiddenNamespaces)
            .GetResult();

        (result.FailingTypes ?? []).Select(type => type.FullName).Should().BeEmpty(
            "{0} must reach passkeys only through IPasskeyCeremony and IPasskeyService; WebAuthn types live in MMCA.Common.Passkeys",
            assemblyName);
    }

    [Theory]
    [MemberData(nameof(GuardedAssemblies))]
    public void FrameworkAssembly_ReferencesNoPasskeysOrWebAuthnAssembly(string assemblyName)
    {
        var references = Load(assemblyName).GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

        references.Should().BeEmpty("{0} is a core framework assembly; the passkey adapter references it, never the reverse", assemblyName);
    }

    private static Assembly Load(string assemblyName) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal))
        ?? Assembly.Load(assemblyName);
}
