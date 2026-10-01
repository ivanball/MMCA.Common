using Microsoft.AspNetCore.Components;

namespace MMCA.Common.Architecture.Tests.Ui;

/// <summary>
/// Component-contract rule (rubric section 18), framework-only: a public <c>[Parameter]</c> on a public
/// MMCA.Common.UI component whose type is a non-nullable reference type promises the component a
/// value, so it must carry <c>[EditorRequired]</c> and let the Razor compiler flag a call site that
/// omits it (RZ2012), instead of the component meeting <see langword="null"/> at render time.
/// <para>
/// Out of scope: value types, <see cref="RenderFragment"/> / <see cref="EventCallback"/> (an absent
/// one is a legitimate "nothing to render / nobody listening"), unconstrained generic parameters
/// (their nullability is the caller's type argument), and <c>[CascadingParameter]</c> /
/// <c>[SupplyParameterFromQuery]</c> members, which the framework supplies rather than the caller.
/// </para>
/// <para>
/// Reflection cannot see a property initializer, so a non-nullable parameter that is genuinely
/// optional because its initializer supplies a non-null default is listed in
/// <see cref="ParametersWithInitializerDefaults"/>, each with the default that makes it optional.
/// Everything else the rule finds is a real gap. An initializer that only guards against null
/// (<c>string.Empty</c>, <c>[]</c>) on a value the component cannot meaningfully render without is not
/// a default and is not listed: <c>PageHeader.Title</c> (an empty level-one heading) and
/// <c>MobileCardList.Items</c> (the list's data) are gaps, not exemptions.
/// </para>
/// </summary>
public sealed class EditorRequiredParameterConventionTests
{
    /// <summary>
    /// <c>Component.Property</c> pairs whose non-null default comes from a property initializer, so a
    /// caller may omit them. Each entry names the default it relies on.
    /// </summary>
    private static readonly HashSet<string> ParametersWithInitializerDefaults = new(StringComparer.Ordinal)
    {
        // Defaults to "application/octet-stream", the correct type when the caller does not know it.
        "ApiFileDownloadButton.ContentType",

        // Defaults to "APIClient", the named HttpClient every MMCA host registers.
        "ApiFileDownloadButton.HttpClientName",

        // Defaults to the Material Download icon.
        "ApiFileDownloadButton.Icon",

        // Defaults to "mobile-list-card", the card's own styling hook.
        "ClickableCard.Class",

        // Defaults to the Material SearchOff icon.
        "EmptyState.Icon",

        // Defaults to "mb-4", the spacing the alert needs above a form.
        "ErrorSummary.Class",

        // Defaults to MMCATheme.Instance, the framework theme a host overrides only to rebrand.
        "MmcaThemeProviders.Theme",

        // Defaults to the Material SearchOff icon for the empty state.
        "MobileCardList`1.EmptyIcon",

        // Defaults to the Material SearchOff icon for the empty state.
        "MobileInfiniteScrollList`1.EmptyIcon",

        // Defaults to an empty list, documented as "offers only lock and unlock".
        "UserAdminList`1.AssignableRoles",
    };

    [Fact]
    public void Components_AreDiscovered_GateIsNotVacuous() =>
        ComponentParameters().Should().NotBeEmpty(
            "MMCA.Common.UI ships public components with [Parameter] properties; finding none means the scan drifted");

    [Fact]
    public void NonNullableReferenceParameters_ShouldBe_EditorRequired()
    {
        var nullability = new NullabilityInfoContext();

        var offenders = ComponentParameters()
            .Where(p => IsNonNullableReference(p, nullability)
                && !p.IsDefined(typeof(EditorRequiredAttribute), inherit: false))
            .Select(static p => $"{p.DeclaringType!.Name}.{p.Name}")
            .Where(static key => !ParametersWithInitializerDefaults.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a non-nullable reference-type [Parameter] on a public component must carry [EditorRequired] "
            + "(or be made nullable, or be listed with its initializer default); offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void InitializerDefaultAllowList_HasNoStaleEntries()
    {
        var live = ComponentParameters()
            .Select(static p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToHashSet(StringComparer.Ordinal);

        ParametersWithInitializerDefaults.Where(key => !live.Contains(key)).Should().BeEmpty(
            "an allow-list entry for a parameter that no longer exists hides nothing and must be removed");
    }

    /// <summary>
    /// The public <c>[Parameter]</c> properties declared on the public components of MMCA.Common.UI,
    /// minus the framework-supplied ones.
    /// </summary>
    private static List<PropertyInfo> ComponentParameters() =>
        [.. typeof(Common.UI.UISharedAssemblyReference).Assembly.GetTypes()
            .Where(static t => t.IsPublic && t.IsClass && typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(static p => p.IsDefined(typeof(ParameterAttribute), inherit: false)
                && !p.IsDefined(typeof(CascadingParameterAttribute), inherit: false)
                && !p.IsDefined(typeof(SupplyParameterFromQueryAttribute), inherit: false))];

    private static bool IsNonNullableReference(PropertyInfo property, NullabilityInfoContext nullability)
    {
        var type = property.PropertyType;
        if (type.IsValueType || type.IsGenericParameter)
        {
            return false;
        }

        if (type == typeof(RenderFragment)
            || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RenderFragment<>))
        {
            return false;
        }

        return nullability.Create(property).WriteState == NullabilityState.NotNull;
    }
}
