namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Per-module conformance fitness function (ADR-015): a module's <c>Name</c>, <c>Dependencies</c> and
/// <c>RequiresDependencies</c> are the whole contract <c>ModuleLoader</c> registers on. It resolves the
/// topological (Kahn) registration order from <c>Dependencies</c>, matches <c>ModulesSettings</c> entries by
/// <c>Name</c>, and decides between a hard start failure and stub registration from
/// <c>RequiresDependencies</c>. Drift in any of the three does not throw: it silently reorders registration,
/// leaves a module permanently enabled, or swaps a real service for a disabled stub. Authored once here and
/// re-run as a thin subclass per module, each supplying only its expectations.
/// <para>
/// The three members are read through the <c>IModule</c> contract by reflection rather than a compile-time
/// cast, exactly as the rest of this package matches framework types by full name: it keeps the package free
/// of the framework's transitive graph (see the csproj remark), and it deliberately dispatches to the
/// DEFAULT interface implementations, so a leaf module that overrides neither <c>Dependencies</c> nor
/// <c>RequiresDependencies</c> is still asserted against the framework's defaults. That is the same reach
/// the hand-written consumer tests needed an explicit <c>(IModule)</c> cast for.
/// </para>
/// </summary>
/// <typeparam name="TModule">The module under test; needs a public parameterless constructor.</typeparam>
public abstract class ModuleConformanceTestsBase<TModule>
    where TModule : class, new()
{
    private const string ModuleContractFullName = "MMCA.Common.Application.Modules.IModule";

    /// <summary>The module's declared <c>Name</c>: the key <c>ModulesSettings</c> and the dependency graph match on.</summary>
    protected abstract string ExpectedName { get; }

    /// <summary>The modules this one declares as dependencies. Defaults to none (a leaf module).</summary>
    protected virtual IReadOnlyCollection<string> ExpectedDependencies => [];

    /// <summary>
    /// Whether the module refuses to start when a declared dependency is disabled. Defaults to
    /// <see langword="false"/>: disabled dependencies are tolerated and their stub services are used instead.
    /// </summary>
    protected virtual bool ExpectedRequiresDependencies => false;

    [Fact]
    public void Module_ShouldDeclare_ExpectedName() =>
        ReadContractMember("Name").Should().Be(
            ExpectedName,
            because: "ModulesSettings entries and every Dependencies reference match a module by this exact name, so renaming it silently disables the module's configuration and drops it from other modules' dependency graphs");

    [Fact]
    public void Module_ShouldDeclare_ExpectedDependencies()
    {
        var dependencies = ReadContractMember("Dependencies") as IEnumerable<string>;

        dependencies.Should().NotBeNull(
            because: "IModule.Dependencies must be a string collection for ModuleLoader to topologically sort on");

        dependencies.Should().BeEquivalentTo(
            ExpectedDependencies,
            because: "ModuleLoader registers modules in topological (Kahn) order over exactly this list, so a missing entry lets a module register before the services it consumes");
    }

    [Fact]
    public void Module_ShouldDeclare_ExpectedRequiresDependencies() =>
        ReadContractMember("RequiresDependencies").Should().Be(
            ExpectedRequiresDependencies,
            because: "this flag is the only thing that turns a disabled dependency into a startup failure instead of a silently substituted stub");

    [Fact]
    public void Module_ShouldRegister_ExpectedDisabledStubs() => AssertDisabledStubs(CreateModule());

    /// <summary>Creates the module under test. Override when the module has no parameterless constructor.</summary>
    /// <returns>A new module instance.</returns>
    protected virtual TModule CreateModule() => new();

    /// <summary>
    /// Asserts what <c>RegisterDisabledStubs</c> puts in the container. Deliberately vacuous by default:
    /// a module that exports no cross-module contract registers no stubs. Override for a module that does
    /// (build a <c>ServiceCollection</c>, call <c>RegisterDisabledStubs</c>, assert the descriptor), so the
    /// contract other modules resolve stays available while this one is disabled.
    /// </summary>
    /// <param name="module">The module under test.</param>
    protected virtual void AssertDisabledStubs(TModule module)
    {
    }

    private object? ReadContractMember(string memberName)
    {
        var module = CreateModule();

        var contract = Array.Find(
            module.GetType().GetInterfaces(),
            i => string.Equals(i.FullName, ModuleContractFullName, StringComparison.Ordinal));

        contract.Should().NotBeNull(
            because: $"{typeof(TModule).FullName} must implement {ModuleContractFullName} to be discovered and registered by ModuleLoader");

        var property = contract.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull(
            because: $"{ModuleContractFullName} must expose {memberName}");

        // Reading through the INTERFACE property dispatches to the module's override when it has one and to
        // the framework's default implementation when it does not.
        return property.GetValue(module);
    }
}
