using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// Asserts the framework's own UI ships no hard-coded user-visible literals (ADR-027 / rubric §27):
/// snackbar messages, page titles, <c>&lt;PageTitle&gt;</c> markup, and breadcrumb labels in
/// <c>MMCA.Common.UI</c> must resolve through <c>IStringLocalizer</c> so the shared chrome follows the
/// selected language. Thin subclass of the shared <see cref="LocalizedTextConventionTestsBase"/> rule.
/// </summary>
public sealed class LocalizedTextConventionTests : LocalizedTextConventionTestsBase
{
    private const string FixtureFileName = "Probe.razor";

    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    // MMCA.Common.UI alone carries ~30 razor files; a floor of 20 catches a wrong scan root.
    protected override int MinimumScannedFiles => 20;

    /// <summary>
    /// A literal <c>EntityType</c> on the shared <c>&lt;DeleteConfirmation&gt;</c> is rendered in the dialog
    /// title and prompt, so it is hard-coded user-visible text: the failure names the file, the line and
    /// the attribute.
    /// </summary>
    [Fact]
    public void DeleteConfirmation_LiteralEntityType_FailsNamingFileLineAndAttribute()
    {
        var act = () => ScanOneRazorFile("""<DeleteConfirmation @ref="_d" EntityType="Product" />""");

        var message = act.Should().Throw<Exception>().Which.Message;
        message.Should().Contain($"{FixtureFileName}:1", "the violation names the file and the offending line");
        message.Should().Contain("EntityType", "the violation names the attribute that must be localized");
    }

    /// <summary>
    /// A razor expression (a localized noun or a variable) and an omitted attribute are all compliant,
    /// including an expression with nested quotes (MMCA.Store <c>CategoryList.razor</c> uses that shape).
    /// </summary>
    /// <param name="razor">The razor markup written to the fixture file.</param>
    [Theory]
    [InlineData("""<DeleteConfirmation @ref="_d" EntityType="@L["Entity.Product"].Value" />""")]
    [InlineData("""<DeleteConfirmation @ref="_d" EntityType="@EntityName" />""")]
    [InlineData("""<DeleteConfirmation @ref="_d" />""")]
    public void DeleteConfirmation_ExpressionOrOmittedEntityType_Passes(string razor)
    {
        var act = () => ScanOneRazorFile(razor);

        act.Should().NotThrow("EntityType is a razor expression or is not set at all");
    }

    /// <summary>An empty literal is still a literal, not a localized noun.</summary>
    [Fact]
    public void DeleteConfirmation_EmptyLiteralEntityType_Fails()
    {
        var act = () => ScanOneRazorFile("""<DeleteConfirmation @ref="_d" EntityType="" />""");

        act.Should().Throw<Exception>().Which.Message.Should().Contain($"{FixtureFileName}:1");
    }

    /// <summary>
    /// A tag split across lines is judged as one element, and the violation is reported on the line that
    /// carries the attribute, not the line that opens the tag.
    /// </summary>
    [Fact]
    public void DeleteConfirmation_MultiLineTagWithLiteralEntityType_FailsOnTheAttributeLine()
    {
        const string razor =
            "<DeleteConfirmation @ref=\"_d\"\n" +
            "    OnConfirm=\"@(async () => await DeleteAsync())\"\n" +
            "    EntityType=\"Sponsor\" />\n";

        var act = () => ScanOneRazorFile(razor);

        act.Should().Throw<Exception>().Which.Message.Should().Contain(
            $"{FixtureFileName}:3", "the literal sits on the third line, where the attribute is written");
    }

    /// <summary>The same multi-line shape with a razor expression is compliant.</summary>
    [Fact]
    public void DeleteConfirmation_MultiLineTagWithExpressionEntityType_Passes()
    {
        const string razor =
            "<DeleteConfirmation @ref=\"_d\"\n" +
            "    OnConfirm=\"@(async () => await DeleteAsync())\"\n" +
            "    EntityType=\"@Noun\" />\n";

        var act = () => ScanOneRazorFile(razor);

        act.Should().NotThrow("the multi-line tag binds EntityType to an expression");
    }

    /// <summary>
    /// The check is scoped to the <c>&lt;DeleteConfirmation&gt;</c> element: a same-named attribute on a
    /// different component, or on a separate element after a closed DeleteConfirmation tag, is not judged.
    /// </summary>
    /// <param name="razor">The razor markup written to the fixture file.</param>
    [Theory]
    [InlineData("""<EntityBadge EntityType="Product" />""")]
    [InlineData("<DeleteConfirmation @ref=\"_d\" EntityType=\"@Noun\" />\n<EntityBadge EntityType=\"Product\" />\n")]
    [InlineData("<DeleteConfirmation @ref=\"_d\" />\n<EntityBadge EntityType=\"Product\" />\n")]
    public void DeleteConfirmation_LiteralEntityTypeOutsideTheTag_Passes(string razor)
    {
        var act = () => ScanOneRazorFile(razor);

        act.Should().NotThrow("the literal EntityType belongs to a different component");
    }

    /// <summary>A namespace-qualified tag is still the shared component.</summary>
    [Fact]
    public void DeleteConfirmation_NamespaceQualifiedTagWithLiteralEntityType_Fails()
    {
        var act = () => ScanOneRazorFile(
            """<MMCA.Common.UI.Components.Forms.DeleteConfirmation EntityType="Product" />""");

        act.Should().Throw<Exception>().Which.Message.Should().Contain($"{FixtureFileName}:1");
    }

    /// <summary>A line carrying the <c>i18n: allow</c> marker is a deliberate, reviewed exemption.</summary>
    [Fact]
    public void DeleteConfirmation_LiteralEntityTypeWithAllowMarker_Passes()
    {
        var act = () => ScanOneRazorFile(
            """<DeleteConfirmation @ref="_d" EntityType="Product" /> @* i18n: allow *@""");

        act.Should().NotThrow("the line is explicitly marked as an allowed literal");
    }

    private static void ScanOneRazorFile(string razor)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mmca-l10n-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, FixtureFileName), razor);

            ArchitectureRules.UserVisibleTextIsLocalized(root, []);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
