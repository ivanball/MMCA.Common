namespace MMCA.Common.Testing.Architecture;

/// <summary>
/// UX-safety convention fitness function (rubric §24): every admin <c>*Create</c> form under a repo's
/// <c>Source/Modules</c> must keep its unsaved-changes guard and client-side validation, so those
/// protections cannot silently regress. Authored once here and re-run as a thin subclass in each repo
/// (each supplies its <see cref="Map"/> and, optionally, a higher <see cref="MinimumCreateForms"/> count
/// matching its known number of create forms). A create form must contain the <c>UnsavedChangesGuard</c>
/// (bound through a live <c>IsDirtyAccessor</c>, which pre-empts the one-render stale-IsDirty lag — see
/// §19), an <c>_isDirty</c> tracking field, and a validated <c>MudForm</c> with at least one
/// <c>Required</c>/<c>RequiredError</c> field. Self-service forms with no navigate-away step (e.g. a
/// single-section Profile password/delete form) carry no guard by design and simply must not match the
/// <c>*Create.razor</c> glob.
/// </summary>
public abstract class FormsConventionTestsBase
{
    protected abstract IArchitectureMap Map { get; }

    /// <summary>
    /// Minimum number of <c>*Create.razor</c> forms the scan must discover — a non-vacuous guard so a glob
    /// that matched nothing cannot let the gate pass without checking anything. Override in the subclass to
    /// the repo's known count so a removed or renamed create form is caught.
    /// </summary>
    protected virtual int MinimumCreateForms => 1;

    /// <summary>Literal markers every admin create form must contain in its <c>.razor</c> markup.</summary>
    protected virtual IReadOnlyList<string> RequiredMarkers =>
    [
        "UnsavedChangesGuard",  // the navigation guard component is present
        "IsDirtyAccessor",      // bound through the live accessor, not the lagging parameter
        "_isDirty",             // dirty-tracking field backing the guard
        "<MudForm",             // a validated MudForm wraps the inputs
        "Required=\"true\"",    // at least one field is required
        "RequiredError",        // with a user-facing required message
    ];

    [Fact]
    public void AdminCreateForms_KeepUnsavedChangesGuardAndValidation()
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
            because: $"at least {MinimumCreateForms} admin create form(s) under Source/Modules must be discovered, so the convention is actually verified");

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
            because: "create forms must keep the UnsavedChangesGuard (with a live IsDirtyAccessor), dirty tracking, and a validated MudForm with Required/RequiredError so the §24 UX-safety guards cannot silently regress");
    }
}
