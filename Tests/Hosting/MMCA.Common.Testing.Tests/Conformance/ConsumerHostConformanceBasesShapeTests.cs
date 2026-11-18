using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Testing.Conformance.Identity;
using MMCA.Common.Testing.Conformance.UiHost;
using Xunit;

namespace MMCA.Common.Testing.Tests.Conformance;

/// <summary>
/// Guards the shape of the consumer conformance bases that need a booted app host (an Identity
/// service with SQL, or a Blazor UI host) and so cannot run inside Common. What IS checkable headlessly
/// is that each base still DECLARES every fact its consumers inherit and still asks a subclass for
/// only the members it names: a fact silently dropped from a base would otherwise stop running in every
/// consumer with nothing failing. Same pattern as <see cref="MmcaGatewayHardeningTestsBaseTests"/>.
/// </summary>
public sealed class ConsumerHostConformanceBasesShapeTests
{
    public static TheoryData<Type, string[]> DeclaredFacts => new()
    {
        {
            typeof(AuthPreferencesTestsBase<>),
            [
                "PutThenGetPreferences_RoundTripsCultureAndTheme",
                "PutPreferences_NullField_LeavesOtherPreferenceUnchanged",
                "GetPreferences_EachUserSeesOnlyOwnValues",
                "PutPreferences_WithUnsupportedCulture_ReturnsBadRequest",
                "PutPreferences_WithUnsupportedTheme_ReturnsBadRequest",
                "GetPreferences_Anonymous_ReturnsUnauthorized",
            ]
        },
        {
            typeof(FallbackAuthorizationTestsBase<>),
            [
                "Host_DoesNotExemptTheErrorPageByPath",
                "EveryMappedEndpoint_DeclaresADecisionOrSitsUnderAnExemptPrefix",
                "AnonymousRequest_ToAPublicPath_IsNotRefused",
            ]
        },
        {
            typeof(UiHostHardeningTestsBase<,>),
            [
                "Host_RegistersTheCeilingAsASingleton",
                "Host_RegistersTheEdgeRateLimiter",
            ]
        },
    };

    public static TheoryData<Type, string[]> AbstractMembers => new()
    {
        { typeof(AuthPreferencesTestsBase<>), ["RegisterAndAuthenticateAsync"] },
        { typeof(FallbackAuthorizationTestsBase<>), ["Factory"] },
        { typeof(UiHostHardeningTestsBase<,>), ["Factory"] },
    };

    [Theory]
    [MemberData(nameof(DeclaredFacts))]
    public void Base_DeclaresEveryInheritedFact(Type baseType, string[] expected)
    {
        var facts = baseType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>(inherit: true) is not null)
            .Select(m => m.Name);

        facts.Should().BeEquivalentTo(
            expected,
            "every fact the base ships runs in each consumer's sealed subclass; dropping or renaming one silently stops it running everywhere");
    }

    [Theory]
    [MemberData(nameof(AbstractMembers))]
    public void Base_RequiresOnlyItsNamedMembersFromASubclass(Type baseType, string[] expected)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var abstractMembers = baseType.GetProperties(declared)
            .Where(p => p.GetMethod is { IsAbstract: true })
            .Select(p => p.Name)
            .Concat(baseType.GetMethods(declared)
                .Where(m => m.IsAbstract && !m.IsSpecialName)
                .Select(m => m.Name));

        abstractMembers.Should().BeEquivalentTo(expected);
    }
}
