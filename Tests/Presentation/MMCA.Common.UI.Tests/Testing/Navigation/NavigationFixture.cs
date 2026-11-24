using System.Reflection;
using MMCA.Common.UI.Common;
using MMCA.Common.UI.Common.Interfaces;

namespace MMCA.Common.UI.Tests.Testing.Navigation;

/// <summary>The role constants the fixture application's pages and modules name.</summary>
public static class FixtureRoleNames
{
    public const string Admin = "Admin";

    public const string Member = "Member";
}

/// <summary>The permission constants the fixture application's <c>[HasPagePermission]</c> guard names.</summary>
public static class FixturePermissions
{
    public const string ReportsView = "reports:view";
}

/// <summary>
/// A UI module the guard gate finds through REFLECTION: the fixture tree holds a source file
/// declaring a class of this name inside a folder whose project file is named after this test
/// assembly, so the gate loads this type instead of reading the text. <see cref="Items"/> is what
/// it then reports, which lets a test prove the reflection path was the one taken.
/// </summary>
public sealed class ReflectedFixtureUIModule : IUIModule
{
    /// <summary>Gets or sets the items the next instance reports (set by a test before it runs the gate).</summary>
    public static IReadOnlyList<NavItem> Items { get; set; } = [];

    /// <inheritdoc />
    public IReadOnlyList<NavItem> NavItems => Items;

    /// <inheritdoc />
    public Assembly Assembly => typeof(ReflectedFixtureUIModule).Assembly;
}

/// <summary>
/// A throwaway application tree in a temp directory: a repo with four routable pages (one per
/// contract form, one guarded from its code-behind), a source-read UI module, and the sibling
/// Website checkout holding the navigation document. Each test edits one file to introduce exactly
/// the drift it asserts the gate catches.
/// </summary>
internal sealed class NavigationFixture : IDisposable
{
    public const string DocFileName = "fixture-NavigationFlow.md";

    public static readonly string CleanContract = string.Join(
        '\n',
        "# Fixture navigation",
        string.Empty,
        "## Route Contract",
        string.Empty,
        "| Route | Page | Access |",
        "| --- | --- | --- |",
        "| `/app-home` | AppHome | Anonymous |",
        "| `/dashboard` | Dashboard | Authenticated |",
        "| `/admin` | AdminPanel | Admin |",
        "| `/reports/{Id:int}` | Reports | Permission: reports:view |",
        string.Empty,
        "## Something else",
        string.Empty,
        "| Route | Page | Access |",
        "| --- | --- | --- |",
        "| `/not-part-of-the-contract` | - | Anonymous |");

    public static readonly string CleanModule = string.Join(
        '\n',
        "namespace Fixture.App.UI;",
        string.Empty,
        "public sealed class AppUIModule : IUIModule",
        "{",
        "    public IReadOnlyList<NavItem> NavItems { get; } =",
        "    [",
        "        new(\"Home\", \"/app-home\", Icons.Home, typeof(Strings)),",
        "        new(\"Dashboard\", \"/dashboard\", Icons.Dashboard, typeof(Strings), Section: NavSection.User),",
        "        new(\"Admin\", \"/admin\", Icons.Admin, typeof(Strings), RequiredRole: FixtureRoleNames.Admin),",
        "        new(\"Report\", \"/reports/1\", Icons.Report, typeof(Strings), RequiredPermission: \"reports:view\", Section: NavSection.Admin),",
        "    ];",
        "}");

    public NavigationFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "mmca-nav-" + Guid.NewGuid().ToString("N"));
        RepoRoot = Path.Combine(Root, "repo");

        WritePage("AppHome", "@page \"/app-home\"\n@attribute [AllowAnonymous]\n<h1>Home</h1>\n");
        WritePage("Dashboard", "@page \"/dashboard\"\n@attribute [Authorize]\n<h1>Dashboard</h1>\n");
        WritePage("AdminPanel", "@page \"/admin\"\n<h1>Admin</h1>\n");
        WriteRepoFile(
            "Source/App.UI/Pages/AdminPanel.razor.cs",
            "namespace Fixture.App.UI.Pages;\n\n// The guard sits on the partial class.\n[Authorize(Roles = FixtureRoleNames.Admin)]\npublic partial class AdminPanel\n{\n}\n");
        WritePage("Reports", "@page \"/reports/{Id:int}\"\n@attribute [HasPagePermission(FixturePermissions.ReportsView)]\n<h1>Reports</h1>\n");

        // Build output is never source: a page here must not count.
        WriteRepoFile("Source/App.UI/obj/Debug/Stale.razor", "@page \"/stale\"\n@attribute [AllowAnonymous]\n");

        WriteRepoFile("Source/App.UI/App.UI.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Razor\" />\n");
        WriteModule(CleanModule);
        WriteContract(CleanContract);
    }

    public string Root { get; }

    public string RepoRoot { get; }

    public string DocPath => Path.Combine(Root, "Website", "docs-src", "guides", DocFileName);

    public void WritePage(string name, string content) => WriteRepoFile($"Source/App.UI/Pages/{name}.razor", content);

    public void WriteModule(string content) => WriteRepoFile("Source/App.UI/AppUIModule.cs", content);

    public void WriteContract(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DocPath)!);
        File.WriteAllText(DocPath, content);
    }

    public void DeleteContract() => File.Delete(DocPath);

    /// <summary>
    /// Adds a module the gate must load by reflection: its folder's project file carries this test
    /// assembly's name, and its source text declares <see cref="ReflectedFixtureUIModule"/> with a
    /// NavItems block that differs from what the type reports.
    /// </summary>
    public void AddReflectedModule()
    {
        WriteRepoFile($"Source/Reflected/{typeof(ReflectedFixtureUIModule).Assembly.GetName().Name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        WriteRepoFile(
            "Source/Reflected/ReflectedFixtureUIModule.cs",
            "public sealed class ReflectedFixtureUIModule : IUIModule\n{\n    public IReadOnlyList<NavItem> NavItems { get; } =\n    [\n        new(\"Home\", \"/app-home\", Icons.Home, typeof(Strings)),\n    ];\n}\n");
    }

    public void WriteRepoFile(string relativePath, string content)
    {
        var path = Path.Combine(RepoRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless; failing the test over cleanup is not.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: cleanup is best effort.
        }
    }
}
