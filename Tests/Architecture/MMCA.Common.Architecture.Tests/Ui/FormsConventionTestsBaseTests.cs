using MMCA.Common.Testing.Architecture;
using Xunit.Sdk;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// Guard for the Profile half of <see cref="FormsConventionTestsBase"/>: the password-form marker list
/// is what both the consumers' inline Profile forms and the shared <c>ChangePasswordCard</c> are held
/// to, so it must flag every missing piece and pass a complete form. MMCA.Common has no
/// <c>Source/Modules</c> tree, so the create-form facts run in the consumers only.
/// </summary>
public sealed class FormsConventionTestsBaseTests
{
    private const string CompleteForm =
        "<MudForm @ref=\"_passwordForm\">"
        + "<MudTextField Required=\"true\" RequiredError=\"a\" />"
        + "<MudTextField Required=\"true\" RequiredError=\"b\" Validation=\"@(new Func<string, string?>(ValidateNewPassword))\" />"
        + "<MudTextField Required=\"true\" RequiredError=\"c\" Validation=\"@(new Func<string, string?>(ValidateConfirmPassword))\" />"
        + "</MudForm>"
        + "<ErrorSummary Messages=\"_passwordForm?.Errors\" />";

    [Fact]
    public void CompletePasswordForm_HasNoMissingMarkers() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(CompleteForm).Should().BeEmpty();

    [Fact]
    public void PasswordForm_WithoutErrorSummary_IsFlagged() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("<ErrorSummary Messages=\"_passwordForm?.Errors\" />", string.Empty, StringComparison.Ordinal))
            .Should().Contain("<ErrorSummary").And.Contain("Messages=\"_passwordForm?.Errors\"");

    [Fact]
    public void PasswordForm_WithAFieldNoLongerRequired_IsFlagged() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("Required=\"true\" RequiredError=\"a\" ", string.Empty, StringComparison.Ordinal))
            .Should().HaveCount(2, "both the requiredness and the required message dropped below three fields");

    /// <summary>
    /// A consumer Profile page that renders the shared card passes the base's Profile fact on the
    /// strength of this test, so the card must keep every marker an inline form is held to.
    /// </summary>
    [Fact]
    public void SharedChangePasswordCard_KeepsEveryPasswordFormMarker()
    {
        var card = Path.Combine(
            ArchitectureMapBase.FindRepoRoot("MMCA.Common.slnx"),
            "Source", "Presentation", "MMCA.Common.UI", "Components", "Auth", "ChangePasswordCard.razor");

        FormsConventionTestsBase.MissingPasswordFormMarkers(File.ReadAllText(card)).Should().BeEmpty(
            "the shared ChangePasswordCard stands in for an inline Profile password form, so it must keep the ErrorSummary, three required fields and both validators");
    }

    [Fact]
    public void PasswordForm_WithoutMatchValidation_IsFlagged() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("ValidateConfirmPassword", "Other", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Be("ValidateConfirmPassword");

    // M238: a guard wrapped in a Razor or HTML comment no longer renders, so it must not count.
    [Theory]
    [InlineData("@* <ErrorSummary Messages=\"_passwordForm?.Errors\" /> *@")]
    [InlineData("<!-- <ErrorSummary Messages=\"_passwordForm?.Errors\" /> -->")]
    public void PasswordForm_WithACommentedOutErrorSummary_IsFlagged(string commentedSummary) =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("<ErrorSummary Messages=\"_passwordForm?.Errors\" />", commentedSummary, StringComparison.Ordinal))
            .Should().Contain("<ErrorSummary").And.Contain("Messages=\"_passwordForm?.Errors\"");

    [Fact]
    public void PasswordForm_WithACommentedOutRequiredField_IsFlagged() =>
        FormsConventionTestsBase.MissingPasswordFormMarkers(
                CompleteForm.Replace("<MudTextField Required=\"true\" RequiredError=\"a\" />", "@* <MudTextField Required=\"true\" RequiredError=\"a\" /> *@", StringComparison.Ordinal))
            .Should().HaveCount(2, "a commented-out field is not a required field");

    [Theory]
    [InlineData("@* <UnsavedChangesGuard IsDirtyAccessor=\"() => IsDirty\" /> *@ <MudForm>")]
    [InlineData("<!-- <UnsavedChangesGuard IsDirtyAccessor=\"() => IsDirty\" /> --> <MudForm>")]
    public void CreateForm_WithACommentedOutGuard_IsFlagged(string markup) =>
        FormsConventionTestsBase.MissingMarkers(markup, ["UnsavedChangesGuard", "IsDirtyAccessor", "<MudForm"])
            .Should().Equal(["UnsavedChangesGuard", "IsDirtyAccessor"], "only the uncommented MudForm still renders");

    [Fact]
    public void CreateForm_WithTheGuardRendered_HasNoMissingMarkers() =>
        FormsConventionTestsBase.MissingMarkers(
                "@* old note *@ <UnsavedChangesGuard IsDirtyAccessor=\"() => IsDirty\" /> <MudForm>",
                ["UnsavedChangesGuard", "IsDirtyAccessor", "<MudForm"])
            .Should().BeEmpty();

    [Theory]
    [InlineData("<ChangePasswordCard />", true)]
    [InlineData("@* <ChangePasswordCard /> *@", false)]
    [InlineData("<!-- <ChangePasswordCard /> -->", false)]
    [InlineData("@* old *@ <ChangePasswordCard MinLength=\"8\" />", true)]
    [InlineData("<MudText>no card</MudText>", false)]
    public void RendersChangePasswordCard_IgnoresCommentedOutTags(string markup, bool expected) =>
        FormsConventionTestsBase.RendersChangePasswordCard(markup).Should().Be(expected);

    // == Create forms on CreatePageBase: the default markers name the base members ==
    private const string BaseMemberCreateForm =
        "<UnsavedChangesGuard IsDirtyAccessor=\"() => IsDirty\" />"
        + "<MudForm @ref=\"Form\" Model=\"_model\">"
        + "<MudTextField Validation=\"@_validate\" />"
        + "</MudForm>"
        + "<ErrorSummary Title=\"@L[\"Validation.CorrectFollowing\"]\" Result=\"SaveResult\" Messages=\"Form?.Errors\" />";

    private const string PrivateFieldCreateForm =
        "<UnsavedChangesGuard IsDirtyAccessor=\"() => _isDirty\" />"
        + "<MudForm @ref=\"_form\" Model=\"_model\">"
        + "<MudTextField Validation=\"@_validate\" />"
        + "</MudForm>"
        + "<ErrorSummary Title=\"@L[\"Validation.CorrectFollowing\"]\" Result=\"_saveResult\" Messages=\"_form?.Errors\" />";

    [Fact]
    public void DefaultRequiredMarkers_NameTheCreatePageBaseMembers() =>
        new MarkerProbe().Markers.Should().BeEquivalentTo(
        [
            "UnsavedChangesGuard",
            "IsDirtyAccessor=\"() => IsDirty\"",
            "<MudForm",
            "@ref=\"Form\"",
            "Model=\"_model\"",
            "Validation=\"@_validate\"",
            "<ErrorSummary",
            "Result=\"SaveResult\"",
            "Messages=\"Form?.Errors\"",
            "Validation.CorrectFollowing",
        ],
        "create forms bind the CreatePageBase members, so the private _isDirty, _form and _saveResult markers are gone");

    [Fact]
    public void CreateForm_WithBaseMarkers_HasNoMissingMarkers() =>
        FormsConventionTestsBase.MissingMarkers(BaseMemberCreateForm, new MarkerProbe().Markers).Should().BeEmpty();

    [Fact]
    public void CreateForm_WithPrivateFieldMarkersOnly_IsFlagged() =>
        FormsConventionTestsBase.MissingMarkers(PrivateFieldCreateForm, new MarkerProbe().Markers)
            .Should().BeEquivalentTo(
            [
                "IsDirtyAccessor=\"() => IsDirty\"",
                "@ref=\"Form\"",
                "Result=\"SaveResult\"",
                "Messages=\"Form?.Errors\"",
            ],
            "a form still on the private _isDirty, _form and _saveResult fields has not moved to CreatePageBase");

    // == InheritsCreatePageBase ==
    [Theory]
    [InlineData("public partial class RoomCreate : CreatePageBase\r\n{\r\n}")]
    [InlineData("public partial class RoomCreate : IAsyncDisposable, CreatePageBase\r\n{\r\n}")]
    public void InheritsCreatePageBase_CodeBehindDeclaration_True(string codeBehind) =>
        FormsConventionTestsBase.InheritsCreatePageBase("<MudForm @ref=\"Form\" />", codeBehind).Should().BeTrue();

    [Fact]
    public void InheritsCreatePageBase_InheritsDirective_True() =>
        FormsConventionTestsBase.InheritsCreatePageBase(
                "@page \"/rooms/create\"\r\n@inherits CreatePageBase\r\n<MudForm @ref=\"Form\" />",
                codeBehind: null)
            .Should().BeTrue();

    // DESIGN-QUESTION: "comment-stripped code-behind" is read as C# comments (// and /* */) stripped, not only the Razor/HTML comments StripAllComments handles today.
    [Theory]
    [InlineData("@* @inherits CreatePageBase *@\r\n<MudForm />", null)]
    [InlineData("<!-- @inherits CreatePageBase -->\r\n<MudForm />", null)]
    [InlineData("<MudForm />", "// TODO: move to : CreatePageBase\r\npublic partial class RoomCreate : ComponentBase\r\n{\r\n}")]
    [InlineData("<MudForm />", "/* public partial class RoomCreate : CreatePageBase */\r\npublic partial class RoomCreate : ComponentBase\r\n{\r\n}")]
    public void InheritsCreatePageBase_OnlyInComment_False(string markup, string? codeBehind) =>
        FormsConventionTestsBase.InheritsCreatePageBase(markup, codeBehind).Should().BeFalse();

    [Fact]
    public void InheritsCreatePageBase_NeitherFileNamesTheBase_False() =>
        FormsConventionTestsBase.InheritsCreatePageBase(
                "@inherits ComponentBase\r\n<MudForm />",
                "public partial class RoomCreate : ComponentBase, IDisposable\r\n{\r\n}")
            .Should().BeFalse();

    // == AdminCreateForms_InheritCreatePageBase over a fixture tree ==
    // The fixtures are written to a temp folder at run time: a committed *.razor.cs fixture would be
    // compiled into this project.
    [Fact]
    public void AdminCreateForms_InheritCreatePageBase_ListsEveryFormThatDoesNot()
    {
        var root = Directory.CreateTempSubdirectory("mmca-create-forms-");
        try
        {
            var pages = Directory.CreateDirectory(Path.Combine(root.FullName, "Widgets", "Pages"));
            File.WriteAllText(Path.Combine(pages.FullName, "GoodCreate.razor"), "<MudForm @ref=\"Form\" />");
            File.WriteAllText(Path.Combine(pages.FullName, "GoodCreate.razor.cs"), "public partial class GoodCreate : CreatePageBase\r\n{\r\n}");
            File.WriteAllText(Path.Combine(pages.FullName, "DirectiveCreate.razor"), "@inherits CreatePageBase\r\n<MudForm @ref=\"Form\" />");
            File.WriteAllText(Path.Combine(pages.FullName, "BadCreate.razor"), "<MudForm @ref=\"_form\" />");
            File.WriteAllText(Path.Combine(pages.FullName, "BadCreate.razor.cs"), "public partial class BadCreate : ComponentBase, IDisposable\r\n{\r\n}");
            File.WriteAllText(Path.Combine(pages.FullName, "CommentedCreate.razor"), "@* @inherits CreatePageBase *@\r\n<MudForm @ref=\"_form\" />");

            var act = () => new FixtureFormsTests(root.FullName).AdminCreateForms_InheritCreatePageBase();

            var message = act.Should().Throw<XunitException>().Which.Message;
            message.Should().Contain("BadCreate.razor").And.Contain("CommentedCreate.razor");
            message.Should().NotContain("GoodCreate.razor").And.NotContain("DirectiveCreate.razor");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void AdminCreateForms_InheritCreatePageBase_PassesWhenEveryFormInherits()
    {
        var root = Directory.CreateTempSubdirectory("mmca-create-forms-");
        try
        {
            var pages = Directory.CreateDirectory(Path.Combine(root.FullName, "Widgets", "Pages"));
            File.WriteAllText(Path.Combine(pages.FullName, "GoodCreate.razor"), "<MudForm @ref=\"Form\" />");
            File.WriteAllText(Path.Combine(pages.FullName, "GoodCreate.razor.cs"), "public partial class GoodCreate : CreatePageBase\r\n{\r\n}");
            File.WriteAllText(Path.Combine(pages.FullName, "DirectiveCreate.razor"), "@inherits CreatePageBase\r\n<MudForm @ref=\"Form\" />");

            var act = () => new FixtureFormsTests(root.FullName).AdminCreateForms_InheritCreatePageBase();

            act.Should().NotThrow();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Exposes the protected default marker list. Private, so xUnit does not discover its inherited facts.</summary>
    private sealed class MarkerProbe : FormsConventionTestsBase
    {
        public IReadOnlyList<string> Markers => RequiredMarkers;

        protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();
    }

    /// <summary>Points the create-form facts at a fixture tree. Private, so xUnit does not discover its inherited facts.</summary>
    private sealed class FixtureFormsTests(string modulesRoot) : FormsConventionTestsBase
    {
        protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

        protected override string ModulesRoot => modulesRoot;
    }
}
