namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Error-catalog fitness function: the set of <c>Error</c> codes a repo's modules construct is a
/// public vocabulary, so it is held to two rules. (1) One code means one thing: the same literal code
/// may not be owned by two different types. (2) A code carries the prefix of the module that owns it,
/// so the code alone says where it came from.
/// <para>
/// Codes are read out of IL, at the <c>Error</c> factory call sites (<c>Validation</c>,
/// <c>NotFoundError</c>, <c>Conflict</c>, <c>Unexpected</c>, and the rest), across the repo's
/// per-module Domain and Application assemblies. A code that is not a literal cannot be judged
/// statically and is listed as UNVERIFIABLE in the failure message rather than passed or failed.
/// </para>
/// <para>
/// Adoption: subclass and supply <see cref="Map"/>. Override <see cref="AllowedCodePrefixes"/> when
/// the catalog is namespaced by aggregate rather than by module name, or
/// <see cref="IsCodePrefixAllowed"/> for a convention a prefix list cannot express.
/// </para>
/// </summary>
public abstract class ErrorCatalogTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// The prefixes a code may carry, matched as <c>prefix + "."</c>. Defaults to the repo's module
    /// names, which is the convention: <c>Sales.OrderNotFound</c> in the Sales module. Override with
    /// the aggregate names when a repo namespaces by aggregate instead.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedCodePrefixes => Map.ModuleNames;

    /// <summary>
    /// Codes that are deliberately shared across types, exempt from both rules. Defaults to the three
    /// generic statics on the framework's <c>Error</c> class, which exist precisely to be reused.
    /// </summary>
    protected virtual IReadOnlyCollection<string> AllowedSharedCodes =>
        ["Error.NotFound", "Error.AlreadyDeleted", "Error.InvalidEntityField"];

    /// <summary>
    /// Minimum number of distinct codes the scan must find, so a map that resolves to no module
    /// assemblies cannot let the catalog rules pass without reading anything. Raise it to the repo's
    /// known catalog size to also catch a module dropping out of the map.
    /// </summary>
    protected virtual int MinimumErrorCodes => 1;

    [Fact]
    public void ErrorCodes_ShouldBe_Unique() =>
        ArchitectureRules.ErrorCodesAreUnique(Map, AllowedSharedCodes);

    [Fact]
    public void ErrorCodes_ShouldCarry_TheOwningModulePrefix() =>
        ArchitectureRules.ErrorCodesUseAnAllowedPrefix(Map, IsCodePrefixAllowed, AllowedSharedCodes);

    [Fact]
    public void ErrorCodeCatalog_ShouldNotBe_Empty() =>
        ArchitectureRules.DistinctErrorCodeCount(Map).Should().BeGreaterThanOrEqualTo(
            MinimumErrorCodes,
            because: "the catalog rules must actually read a module's codes; finding none means the map registers no module Domain/Application assemblies and both gates are vacuous");

    /// <summary>True when a code carries one of <see cref="AllowedCodePrefixes"/>.</summary>
    /// <param name="code">The machine-readable error code, e.g. <c>Sales.OrderNotFound</c>.</param>
    /// <returns>True when the code is acceptably prefixed.</returns>
    protected virtual bool IsCodePrefixAllowed(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        return AllowedCodePrefixes.Any(prefix => code.StartsWith(prefix + ".", StringComparison.Ordinal));
    }
}
