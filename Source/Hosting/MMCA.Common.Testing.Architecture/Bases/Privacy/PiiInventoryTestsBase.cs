namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// Two-way personal-data inventory gate (ADR-005, rubric section 30): the <c>[Pii]</c> markers in
/// the code and the inventory table in <c>PRIVACY.md</c> describe the same set of members.
/// <list type="bullet">
/// <item>Code to document: every <c>[Pii]</c> property in <see cref="Assemblies"/> has an inventory
/// row whose <c>Purpose</c>, <c>Lawful basis</c> and <c>Retention</c> cells are filled.</item>
/// <item>Document to code: every inventory row names a property that still carries <c>[Pii]</c>, so
/// the policy cannot keep promising things about data the code no longer marks.</item>
/// </list>
/// <para>
/// <b>Canonical inventory format.</b> A Markdown table anywhere in <c>PRIVACY.md</c> whose header row
/// holds the four columns below (any order, case-insensitive, extra columns allowed; several such
/// tables, for example one per module, are read together). The <c>Member</c> cell is the declaring
/// type's simple name and the property name, <c>Type.Property</c>, optionally in backticks. Cells may
/// not contain a literal pipe.
/// </para>
/// <code>
/// ## Personal-data inventory
///
/// | Member | Purpose | Lawful basis | Retention |
/// | --- | --- | --- | --- |
/// | `User.Email` | Sign-in and account email | Contract (GDPR Art. 6(1)(b)) | While the account exists; overwritten on erasure |
/// </code>
/// <para>
/// <b>Subclassing:</b> supply the assemblies whose <c>[Pii]</c> members the policy must account for
/// (normally the module Domain assemblies, plus a framework assembly whose personal-data entities the
/// host persists, such as the one holding <c>RefreshSession</c>) and the path to the policy, for
/// example <c>Path.Combine(ArchitectureMapBase.FindRepoRoot("MMCA.Store.slnx"), "PRIVACY.md")</c>.
/// </para>
/// </summary>
public abstract class PiiInventoryTestsBase
{
    /// <summary>The assemblies whose <c>[Pii]</c> properties the inventory must cover.</summary>
    protected abstract IReadOnlyList<Assembly> Assemblies { get; }

    /// <summary>The absolute path of the repo's <c>PRIVACY.md</c>.</summary>
    protected abstract string PrivacyPolicyPath { get; }

    [Fact]
    public void PiiMembers_ShouldHave_CompleteInventoryRows() =>
        ArchitectureRules.PiiMembersHaveInventoryRows(Assemblies, ReadPrivacyPolicy());

    [Fact]
    public void InventoryRows_ShouldMapTo_LivePiiMembers() =>
        ArchitectureRules.PiiInventoryRowsMapToPiiMembers(Assemblies, ReadPrivacyPolicy());

    private string ReadPrivacyPolicy()
    {
        File.Exists(PrivacyPolicyPath).Should().BeTrue(
            because: $"the personal-data inventory gate reads the privacy policy at '{PrivacyPolicyPath}'");
        return File.ReadAllText(PrivacyPolicyPath);
    }
}
