using MMCA.Common.AI;

namespace MMCA.Common.Architecture.Tests.AiBoundaryFixtures;

/// <summary>
/// Names a governed <c>MMCA.Common.AI</c> type, so an architecture map that treats this test assembly
/// as a non-Infrastructure layer must fail <c>GovernedAiPackageStaysBehindInfrastructure</c> (M257).
/// </summary>
public static class FixtureAiConsumer
{
    /// <summary>Reads a prompt contract's name, which is all it takes to depend on the package.</summary>
    /// <param name="contract">The prompt contract.</param>
    /// <returns>The contract's name.</returns>
    public static string NameOf(PromptContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return contract.Name;
    }
}
