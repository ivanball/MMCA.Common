using MMCA.Common.Testing.Architecture;

namespace MMCA.Common.Architecture.Tests.Governance;

/// <summary>
/// PII rules (ADR-005, rubric section 30) over the framework's own assemblies.
/// <list type="bullet">
///   <item>The inherited <see cref="PiiConventionTestsBase"/> rule: an entity with a <c>[Pii]</c>
///   property must implement <c>IAnonymizable</c>, so it has an erasure path.</item>
///   <item>The marking rule this subclass adds, so the scan is not structurally vacuous in the
///   framework: every string property whose name says it holds a person's email, name, phone number or
///   address, declared on a class in MMCA.Common's Domain assembly or in the auth, user and
///   notification namespaces of its Application assembly, carries <c>[Pii]</c>. Without the marker,
///   <c>PiiRedactor</c> logs the value in clear text and the erasure rule above never sees it.</item>
/// </list>
/// The Shared assembly is out of scope: it does not reference Domain, where <c>[Pii]</c> lives, so a
/// Shared DTO cannot carry the marker. The redaction + erasure machinery itself is proven end to end
/// by <see cref="PiiErasureContractFitnessTests"/>.
/// </summary>
public sealed class PiiConventionTests : PiiConventionTestsBase
{
    private const string PiiAttributeName = "PiiAttribute";

    /// <summary>Application namespaces whose types carry a data subject's personal data.</summary>
    private static readonly string[] PersonalDataApplicationNamespaces =
    [
        "MMCA.Common.Application.Auth",
        "MMCA.Common.Application.Users",
        "MMCA.Common.Application.Notifications",
    ];

    /// <summary>Name fragments (case-insensitive) that mark a contact detail.</summary>
    private static readonly string[] ContactFragments = ["Email", "Phone", "Address"];

    /// <summary>Property names (case-insensitive) that hold a person's name. A bare <c>Name</c> is not
    /// one: in the framework it names a role, permission, event or file, never a person.</summary>
    private static readonly string[] PersonNames =
    [
        "FirstName", "LastName", "MiddleName", "FullName", "DisplayName", "UserName", "GivenName",
        "FamilyName", "Surname",
    ];

    protected override IArchitectureMap Map { get; } = new CommonArchitectureMap();

    [Fact]
    public void PersonalDataMembers_AreDiscovered_GateIsNotVacuous() =>
        PersonalDataMembers().Should().NotBeEmpty(
            "the framework's Domain and auth/user/notification Application types hold personal data; "
            + "finding none means the population or the name rule drifted and the [Pii] gate checks nothing");

    [Fact]
    public void PersonalDataMembers_ShouldCarry_PiiAttribute()
    {
        var offenders = PersonalDataMembers()
            .Where(static p => !p.GetCustomAttributes(inherit: true)
                .Any(static a => string.Equals(a.GetType().Name, PiiAttributeName, StringComparison.Ordinal)))
            .Select(static p => $"{p.DeclaringType!.FullName}.{p.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a property holding a person's email, name, phone number or address must carry [Pii] so "
            + "PiiRedactor masks it and the IAnonymizable rule sees it (ADR-005); offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The string properties, declared on a class of the scanned population, whose name says they hold
    /// a person's email, name, phone number or address.
    /// </summary>
    private List<PropertyInfo> PersonalDataMembers()
    {
        var domainTypes = Map.OfLayer(Layer.Domain).SelectMany(static a => a.GetTypes());
        var applicationTypes = Map.OfLayer(Layer.Application)
            .SelectMany(static a => a.GetTypes())
            .Where(static t => PersonalDataApplicationNamespaces.Any(ns => t.Namespace is { } typeNamespace
                && (string.Equals(typeNamespace, ns, StringComparison.Ordinal)
                    || typeNamespace.StartsWith(ns + ".", StringComparison.Ordinal))));

        return [.. domainTypes.Concat(applicationTypes)
            .Where(static t => t.IsClass && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
            .SelectMany(static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(static p => p.PropertyType == typeof(string) && IsPersonalDataName(p.Name))];
    }

    private static bool IsPersonalDataName(string propertyName) =>
        ContactFragments.Any(f => propertyName.Contains(f, StringComparison.OrdinalIgnoreCase))
        || PersonNames.Any(n => propertyName.EndsWith(n, StringComparison.OrdinalIgnoreCase));
}
