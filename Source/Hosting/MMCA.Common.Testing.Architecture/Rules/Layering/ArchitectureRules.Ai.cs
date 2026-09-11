namespace MMCA.Common.Testing.Architecture;

public static partial class ArchitectureRules
{
    /// <summary>
    /// The governed language-model package. Every layer that is not Infrastructure reaches a model
    /// through this package or not at all.
    /// </summary>
    /// <remarks>
    /// Matched with the trailing dot on purpose: a bare <c>MMCA.Common.AI</c> prefix would also
    /// match every <c>MMCA.Common.API.*</c> type and fail the rule on the API layer for no reason.
    /// </remarks>
    public const string GovernedAiPackagePrefix = "MMCA.Common.AI.";

    /// <summary>
    /// Language-model SDK namespaces that must not appear outside the model boundary.
    /// <para>
    /// An SDK reference is not a dependency like any other: it pins a vendor, a wire format, a
    /// credential and a billing relationship, and a prompt written beside business logic is a
    /// behavioral change nobody can evaluate or roll back on its own. Confining these namespaces to
    /// Infrastructure (and to <c>MMCA.Common.AI</c>, which exists precisely to hold them) is what
    /// keeps the model swappable, the prompts versioned and the spend attributable (rubric
    /// section 16).
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> LanguageModelDependencies =
    [
        "Anthropic",
        "Microsoft.Extensions.AI",
        "OpenAI",
        "Azure.AI",
    ];

    /// <summary>
    /// Assert that no layer outside the model boundary names a language-model SDK type.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    public static void LanguageModelSdksStayAtTheModelBoundary(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var forbidden = LanguageModelDependencies.ToArray();

        foreach (var layerRef in map.Layers.Where(layer => !IsModelBoundaryAssembly(layer.Assembly)))
        {
            var result = Types.InAssembly(layerRef.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(forbidden)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{layerRef.RootNamespace}: {layerRef.Layer} must not name a language-model SDK type. "
                    + "Depend on Microsoft.Extensions.AI's IChatClient behind an interface your module owns, "
                    + "and let MMCA.Common.AI (or a module Infrastructure project) construct the client");
        }
    }

    /// <summary>
    /// Assert that only Infrastructure consumes the governed model package: a prompt, a bound or a
    /// token count must not become a type Domain, Application, Shared, API or UI depends on.
    /// </summary>
    /// <param name="map">The repo's architecture map.</param>
    public static void GovernedAiPackageStaysBehindInfrastructure(IArchitectureMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        foreach (var layerRef in map.Layers.Where(layer => !IsModelBoundaryAssembly(layer.Assembly)))
        {
            var result = Types.InAssembly(layerRef.Assembly)
                .ShouldNot()
                .HaveDependencyOnAny(GovernedAiPackagePrefix)
                .GetResult();

            ArchitectureAssert.NoViolations(result,
                $"{layerRef.RootNamespace}: {layerRef.Layer} must not reference MMCA.Common.AI. The governed "
                    + "chat client is an Infrastructure-tier dependency; expose it to the rest of the module "
                    + "through an interface the module declares, exactly as it would a database or a broker");
        }
    }

    /// <summary>
    /// Whether an assembly is allowed to hold model-SDK types: a module's (or the framework's)
    /// Infrastructure, or the governed package itself.
    /// </summary>
    /// <param name="assembly">The assembly to classify.</param>
    /// <returns><see langword="true"/> when the assembly sits at the model boundary.</returns>
    private static bool IsModelBoundaryAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;

        return name is not null
            && (name.Contains(".Infrastructure", StringComparison.Ordinal)
                || string.Equals(name, "MMCA.Common.AI", StringComparison.Ordinal)
                || name.StartsWith(GovernedAiPackagePrefix, StringComparison.Ordinal));
    }
}
