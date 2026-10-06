using System.Globalization;
using System.Text;

namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// UX-safety convention fitness function (rubric §24): every admin <c>*Create</c> form under a repo's
/// <c>Source/Modules</c> must keep its unsaved-changes guard and its model-sourced client-side
/// validation, and the Identity Profile page must keep its password validation, so those protections
/// cannot silently regress. Authored once here and re-run as a thin subclass in each repo: each supplies
/// its <see cref="Map"/> and a <see cref="MinimumCreateForms"/> count matching its known number of create
/// forms.
/// <para>
/// A create form must contain the <c>UnsavedChangesGuard</c> (bound through a live
/// <c>IsDirtyAccessor</c>, which pre-empts the one-render stale-IsDirty lag, see §19), an
/// <c>_isDirty</c> tracking field, a <c>MudForm</c> that names its model and runs the model's rules
/// through the <c>MMCA.Common.UI.Validation</c> bridge, and a per-form <c>ErrorSummary</c> fed both the
/// failed save <c>Result</c> and the live form errors (<see cref="RequiredMarkers"/>). Requiredness is
/// declared once as a <c>[Required]</c> attribute on the form model and read back into the markup as
/// <c>ModelValidation.IsRequired</c>, either in the page or in the sibling <c>*FormFields.razor</c>
/// component the page renders (<see cref="AdminCreateForms_ReadRequirednessOffTheirModel"/>).
/// Self-service forms with no navigate-away step (the single-section Profile password/delete form)
/// carry no guard by design and simply must not match the <c>*Create.razor</c> glob; the Profile page
/// has its own fact (<see cref="ProfileForm_KeepsErrorSummaryAndPasswordValidation"/>).
/// </para>
/// </summary>
public abstract class FormsConventionTestsBase
{
    /// <summary>The literal that marks a Profile page rendering the shared change-password component.</summary>
    public const string ChangePasswordCardTag = "<ChangePasswordCard";

    /// <summary>
    /// The markers an inline change-password form must keep: the per-form error summary fed from the
    /// live MudForm error list, and the client-side min-length and match validation wiring. The three
    /// password fields must also each stay required with a user-facing message
    /// (see <see cref="MissingPasswordFormMarkers"/>).
    /// </summary>
    private static readonly string[] PasswordFormMarkers =
    [
        "<ErrorSummary",                        // per-form error summary inside the form card (shared component)
        "Messages=\"_passwordForm?.Errors\"",   // summary fed from the live MudForm error list
        "ValidateNewPassword",                  // client-side min-length validation wiring
        "ValidateConfirmPassword",              // client-side match validation wiring
    ];

    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Minimum number of <c>*Create.razor</c> forms the scan must discover — a non-vacuous guard so a glob
    /// that matched nothing cannot let the gate pass without checking anything. Override in the subclass to
    /// the repo's known count so a removed or renamed create form is caught.
    /// </summary>
    protected virtual int MinimumCreateForms => 1;

    /// <summary>
    /// Literal markers every admin create form must contain in its <c>.razor</c> markup. The default is
    /// the model-bridge set: a form that still spelled <c>Required="true"</c> plus a
    /// <c>RequiredError</c> message on every field would be the regression, not the convention. The
    /// requiredness marker itself is checked by
    /// <see cref="AdminCreateForms_ReadRequirednessOffTheirModel"/> rather than listed here, because the
    /// field block may live in a shared sibling component.
    /// </summary>
    protected virtual IReadOnlyList<string> RequiredMarkers =>
    [
        "UnsavedChangesGuard",          // the navigation guard component is present
        "IsDirtyAccessor",              // bound through the live accessor, not the lagging parameter
        "_isDirty",                     // dirty-tracking field backing the guard
        "<MudForm",                     // a validated MudForm wraps the inputs
        "Model=\"_model\"",             // the form declares the model its rules come from
        "Validation=\"@_validate\"",    // fields run the model's rules through the bridge
        "<ErrorSummary",                // per-form error summary inside the form card (shared component)
        "Result=\"_saveResult\"",       // fed the failed create Result, not just MudForm validation
        "Messages=\"_form?.Errors\"",   // and the live MudForm error list
        "Validation.CorrectFollowing",  // localized error-summary heading
    ];

    /// <summary>
    /// The repo's Identity Profile page. Defaults to the conventional
    /// <c>Source/Modules/Identity/{RepoToken}.Identity.UI/Pages/Users/Profile/Profile.razor</c>.
    /// Return <see langword="null"/> only when the repo ships no Profile page at all.
    /// </summary>
    protected virtual string? ProfileFormPath =>
        Path.Combine(
            ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx"),
            "Source",
            "Modules",
            "Identity",
            $"{Map.RepoToken}.Identity.UI",
            "Pages",
            "Users",
            "Profile",
            "Profile.razor");

    /// <summary>
    /// The markers a change-password form's markup is missing: the error summary and validation wiring,
    /// plus at least three <c>Required="true"</c> fields each with a <c>RequiredError</c> message.
    /// Empty when the form is complete. Public so the shared <c>ChangePasswordCard</c> can be held to
    /// the same list the inline forms are.
    /// </summary>
    /// <param name="markup">The form's <c>.razor</c> markup.</param>
    /// <returns>The missing markers, empty when none.</returns>
    public static IReadOnlyList<string> MissingPasswordFormMarkers(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);

        var missing = PasswordFormMarkers
            .Where(marker => !markup.Contains(marker, StringComparison.Ordinal))
            .ToList();

        if (CountOccurrences(markup, "Required=\"true\"") < 3)
        {
            missing.Add("Required=\"true\" on all three password fields (current/new/confirm)");
        }

        if (CountOccurrences(markup, "RequiredError") < 3)
        {
            missing.Add("RequiredError on all three password fields");
        }

        return missing;
    }

    [Fact]
    public void AdminCreateForms_KeepUnsavedChangesGuardAndValidation()
    {
        var createForms = DiscoverCreateForms();

        var violations = new List<string>();
        foreach (var form in createForms)
        {
            var markup = File.ReadAllText(form);
            var missing = RequiredMarkers
                .Where(marker => !markup.Contains(marker, StringComparison.Ordinal))
                .ToArray();
            if (missing.Length > 0)
            {
                violations.Add($"{Path.GetFileName(form)} is missing: {string.Join(", ", missing)}");
            }
        }

        violations.Should().BeEmpty(
            because: "create forms must keep the UnsavedChangesGuard (with a live IsDirtyAccessor), dirty tracking, a MudForm validated through the model bridge, and a per-form ErrorSummary so the §24 UX-safety guards cannot silently regress");
    }

    /// <summary>
    /// The requiredness half of the model bridge, checked over each create page PLUS any sibling
    /// <c>*FormFields.razor</c> component it renders. A create form usually shares its field block with
    /// the matching detail page's inline editor, so the fields (and with them the
    /// <c>ModelValidation.IsRequired</c> affordance read off the model) can live in that sibling while
    /// the page keeps the parts <see cref="AdminCreateForms_KeepUnsavedChangesGuardAndValidation"/>
    /// checks. This widens the scan to follow the extraction rather than weakening it: every create
    /// form still declares at least one field as required by reading the model, and a form that inlines
    /// its fields is still covered, because its own markup is scanned first.
    /// </summary>
    [Fact]
    public void AdminCreateForms_ReadRequirednessOffTheirModel()
    {
        var createForms = DiscoverCreateForms();

        var violations = new List<string>();
        foreach (var form in createForms)
        {
            var scanned = new StringBuilder(File.ReadAllText(form));

            foreach (var fieldComponent in Directory.EnumerateFiles(
                Path.GetDirectoryName(form)!, "*FormFields.razor", SearchOption.TopDirectoryOnly))
            {
                scanned.Append(File.ReadAllText(fieldComponent));
            }

            if (!scanned.ToString().Contains("ModelValidation.IsRequired", StringComparison.Ordinal))
            {
                violations.Add(Path.GetFileName(form));
            }
        }

        violations.Should().BeEmpty(
            because: "every create form must declare at least one field as required by reading it off the form model, either in its own markup or in the shared field component it renders, so the §24 model-sourced validation bridge cannot silently regress");
    }

    /// <summary>
    /// The Profile page keeps its change-password validation: either it renders the shared
    /// <c>ChangePasswordCard</c> from MMCA.Common.UI (whose markup MMCA.Common holds to the same
    /// markers in its own tests), or its inline form keeps the per-form ErrorSummary, the three required
    /// password fields and the client-side match and min-length wiring.
    /// </summary>
    [Fact]
    public void ProfileForm_KeepsErrorSummaryAndPasswordValidation()
    {
        var profileForm = ProfileFormPath;
        if (profileForm is null)
        {
            return;
        }

        File.Exists(profileForm).Should().BeTrue(
            because: $"the Identity Profile form must be discovered at {profileForm} so its §24 conventions are actually verified (override ProfileFormPath if it lives elsewhere)");

        var markup = File.ReadAllText(profileForm);
        if (RendersChangePasswordCard(markup))
        {
            return;
        }

        MissingPasswordFormMarkers(markup).Should().BeEmpty(
            because: "the Profile form must keep its per-form ErrorSummary, three required password fields and client-side password validation wiring (or render the shared ChangePasswordCard) so the §24 UX-safety guards cannot silently regress");
    }

    private string[] DiscoverCreateForms()
    {
        var repoRoot = ArchitectureMapBase.FindRepoRoot($"{Map.RepoToken}.slnx");
        var modulesDir = Path.Combine(repoRoot, "Source", "Modules");

        var createForms = Directory
            .EnumerateFiles(modulesDir, "*Create.razor", SearchOption.AllDirectories)
            .Where(static p =>
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        (createForms.Length >= MinimumCreateForms).Should().BeTrue(
            because: $"at least {MinimumCreateForms.ToString(CultureInfo.InvariantCulture)} admin create form(s) under Source/Modules must be discovered, so the convention is actually verified");

        return createForms;
    }

    /// <summary>
    /// Whether the markup renders the shared <c>ChangePasswordCard</c>. Razor (<c>@* *@</c>) and HTML
    /// (<c>&lt;!-- --&gt;</c>) comments are stripped first, so a commented-out tag does not stand in for
    /// a password form.
    /// </summary>
    /// <param name="markup">The Razor markup of the Profile page.</param>
    /// <returns><see langword="true"/> when an uncommented card tag is present.</returns>
    public static bool RendersChangePasswordCard(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        var uncommented = StripComments(StripComments(markup, "@*", "*@"), "<!--", "-->");
        return uncommented.Contains(ChangePasswordCardTag, StringComparison.Ordinal);
    }

    private static string StripComments(string text, string open, string close)
    {
        var builder = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf(open, position, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            builder.Append(text, position, start - position);
            var end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
            position = end < 0 ? text.Length : end + close.Length;
        }

        return builder.ToString();
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
