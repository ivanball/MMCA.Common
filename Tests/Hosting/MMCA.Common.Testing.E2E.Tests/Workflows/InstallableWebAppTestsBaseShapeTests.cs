using System.Reflection;
using AwesomeAssertions;
using MMCA.Common.Testing.E2E.Workflows.Pwa;

namespace MMCA.Common.Testing.E2E.Tests.Workflows;

/// <summary>
/// Guards the shape of <see cref="InstallableWebAppTestsBase"/>. Its facts drive a browser against a
/// consumer's running app, so they cannot run here; what IS checkable headlessly is that the base still
/// declares both facts its consumers inherit and asks a subclass for only the app's name and theme
/// colour. A fact silently dropped from the base would otherwise stop running in every consumer.
/// </summary>
public sealed class InstallableWebAppTestsBaseShapeTests
{
    /// <summary>A fact dropped or renamed in the base stops running in every consumer.</summary>
    [Fact]
    public void Base_DeclaresBothInstallabilityFacts()
    {
        var facts = typeof(InstallableWebAppTestsBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>(inherit: true) is not null)
            .Select(m => m.Name);

        facts.Should().BeEquivalentTo(
            [
                "Home_LinksAnInstallableManifest_WhoseIconsAllResolve",
                "TheServiceWorker_IsServedAtTheSiteRoot_AsUncachedJavaScript",
            ],
            "every fact the base ships runs in each consumer's sealed subclass; dropping or renaming one silently stops it running everywhere");
    }

    /// <summary>A new abstract member would break every consumer subclass at the next bump.</summary>
    [Fact]
    public void Base_RequiresOnlyTheAppNameAndThemeColourFromASubclass()
    {
        var abstractMembers = typeof(InstallableWebAppTestsBase)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetMethod is { IsAbstract: true })
            .Select(p => p.Name);

        abstractMembers.Should().BeEquivalentTo("ExpectedName", "ExpectedThemeColor");
    }
}
