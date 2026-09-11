namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// The model-boundary fitness function: a language-model dependency is confined to Infrastructure
/// and to the governed <c>MMCA.Common.AI</c> package, in both directions.
/// <para>
/// The two rules are the two ways the boundary leaks. A module can name the vendor SDK directly
/// (<c>Anthropic</c>, <c>OpenAI</c>, <c>Azure.AI</c>, or <c>Microsoft.Extensions.AI</c> itself), or
/// it can take the framework's governed package as a convenience and start passing a
/// <c>PromptContract</c> around as a domain type. Either one turns "the app calls a model at one
/// place" into "the model is spread through the app", and a dependency that is everywhere cannot be
/// versioned, evaluated, capped or swapped (rubric section 16).
/// </para>
/// <para>
/// Subclass it in each repo's architecture tests with that repo's <see cref="IArchitectureMap"/>,
/// exactly like the other shared bases (ADR-015).
/// </para>
/// </summary>
public abstract class AiDependencyIsolationTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    [Fact]
    public void LanguageModelSdks_ShouldStayAt_TheModelBoundary() =>
        ArchitectureRules.LanguageModelSdksStayAtTheModelBoundary(Map);

    [Fact]
    public void GovernedAiPackage_ShouldStayBehind_Infrastructure() =>
        ArchitectureRules.GovernedAiPackageStaysBehindInfrastructure(Map);
}
