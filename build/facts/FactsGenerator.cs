using System.Diagnostics;
using System.Text.RegularExpressions;

// Runtime Regex is deliberate in this tiny build tool; source-generated [GeneratedRegex] is overkill.
// Suppressed in the shared file so it's clean both in the isolated build/facts project and when this
// file is linked into the workspace Tools/invtool (which runs analyzers).
#pragma warning disable SYSLIB1045

/// <summary>
/// Computes MMCA.Common/FACTS.md from source so the framework-wide facts (version, package count,
/// fitness-method/base counts) stop being hand-maintained and cannot drift. (The ADR count/range
/// moved with the ADRs to the Website repo's docs-src/adr/README.md, 2026-07-20.)
///
/// Dependency-free (BCL + regex only) so it carries no NuGet/audit/lock-file surface and needs no
/// restore in CI. This is the canonical generator; the workspace `Tools/invtool` links this same
/// file so its `facts` subcommand and this in-repo tool share one implementation.
///
/// Usage: facts &lt;MMCA.Common repo root&gt; [outputPath] [--check]
///   - default: render and write &lt;root&gt;/FACTS.md (or outputPath).
///   - --check: render and compare to the existing file; exit 1 on mismatch, write nothing
///     (wired into MMCA.Common CI as a drift gate).
/// </summary>
internal static class FactsGenerator
{
    public static void Run(string[] args)
    {
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        var check = args.Contains("--check", StringComparer.Ordinal);

        if (positional.Length < 1)
        {
            Console.Error.WriteLine("usage: facts <MMCA.Common repo root> [outputPath] [--check]");
            Environment.Exit(2);
            return;
        }

        var root = Path.GetFullPath(positional[0]);
        var outPath = positional.Length > 1 ? Path.GetFullPath(positional[1]) : Path.Combine(root, "FACTS.md");

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"facts: repo root not found: {root}");
            Environment.Exit(2);
            return;
        }

        // ---- compute the facts from source
        var version = GitTag(root);
        var packages = PackageList(root);                       // package ids, tier-then-name order
        var baseCounts = FitnessBaseCounts(root);               // baseClassName -> [Fact]/[Theory] count
        var fitBases = baseCounts.Count;
        var fitMethods = baseCounts.Values.Sum();
        var commonExecuted = CommonExecutedFitness(root, baseCounts);
        var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var pkgList = string.Join("\n", packages.Select((p, i) => $"{i + 1}. `{p}`"));

        var rendered = Template
            .Replace("{{AS_OF}}", asOf)
            .Replace("{{VERSION}}", version)
            .Replace("{{PKG_COUNT}}", packages.Count.ToString())
            .Replace("{{PKG_LIST}}", pkgList)
            .Replace("{{FIT_METHODS}}", fitMethods.ToString())
            .Replace("{{FIT_BASES}}", fitBases.ToString())
            .Replace("{{COMMON_EXEC}}", commonExecuted.ToString());

        Console.WriteLine("FACTS computed from source:");
        Console.WriteLine($"  version           = {version}");
        Console.WriteLine($"  packages          = {packages.Count}");
        Console.WriteLine($"  fitness methods   = {fitMethods} across {fitBases} *TestsBase classes");
        Console.WriteLine($"  Common executes   = {commonExecuted}");

        if (check)
        {
            var existing = File.Exists(outPath) ? File.ReadAllText(outPath) : "";
            if (Normalize(existing) == Normalize(rendered))
            {
                Console.WriteLine($"facts --check: {Norm(outPath)} is up to date.");
            }
            else
            {
                Console.Error.WriteLine($"facts --check: {Norm(outPath)} is STALE vs source — regenerate with `dotnet run --project build/facts -- .`");
                Environment.Exit(1);
            }
            return;
        }

        File.WriteAllText(outPath, rendered);
        Console.WriteLine($"WROTE {Norm(outPath)}");
    }

    // ---- git tag (MinVer source of the version)
    private static string GitTag(string repo)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "describe --tags --abbrev=0")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return "(unknown)";
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(stdout) ? "(unknown)" : stdout;
        }
        catch
        {
            return "(unknown)";
        }
    }

    // ---- packable packages: csproj under Source/ carrying an explicit <PackageId>, ordered Core→Presentation→Hosting then by name
    private static List<string> PackageList(string root)
    {
        var sourceDir = Path.Combine(root, "Source");
        var pkgs = new List<(int tier, string id)>();
        if (!Directory.Exists(sourceDir)) return new();

        foreach (var csproj in Directory.EnumerateFiles(sourceDir, "*.csproj", SearchOption.AllDirectories))
        {
            var n = Norm(csproj);
            if (n.Contains("/bin/") || n.Contains("/obj/")) continue;
            var m = Regex.Match(File.ReadAllText(csproj), @"<PackageId>\s*([^<\s]+)\s*</PackageId>");
            if (!m.Success) continue;
            var tier = n.Contains("/Core/") ? 0 : n.Contains("/Presentation/") ? 1 : n.Contains("/Hosting/") ? 2 : 3;
            pkgs.Add((tier, m.Groups[1].Value));
        }

        return pkgs.OrderBy(p => p.tier).ThenBy(p => p.id, StringComparer.Ordinal).Select(p => p.id).ToList();
    }

    // ---- fitness: each abstract *TestsBase class in MMCA.Common.Testing.Architecture -> its [Fact]/[Theory] count.
    //      Each base lives in its own file (one abstract *TestsBase per file), so a per-file count attributes cleanly.
    private static Dictionary<string, int> FitnessBaseCounts(string root)
    {
        var pkgDir = Path.Combine(root, "Source", "Hosting", "MMCA.Common.Testing.Architecture");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Directory.Exists(pkgDir)) return result;

        foreach (var file in CsFiles(pkgDir))
        {
            var text = File.ReadAllText(file);
            foreach (Match baseDecl in Regex.Matches(text, @"\babstract\s+(?:sealed\s+|partial\s+)*class\s+(\w+TestsBase)\b"))
                result[baseDecl.Groups[1].Value] = TestAttrCount(text);
        }
        return result;
    }

    // ---- how many fitness methods MMCA.Common's own build executes:
    //      sum of the method counts of the bases its arch-tests subclass, plus its directly-declared [Fact]/[Theory].
    private static int CommonExecutedFitness(string root, Dictionary<string, int> baseCounts)
    {
        var testsDir = Path.Combine(root, "Tests", "Architecture", "MMCA.Common.Architecture.Tests");
        if (!Directory.Exists(testsDir)) return 0;

        int inherited = 0, direct = 0;
        foreach (var file in CsFiles(testsDir))
        {
            var text = File.ReadAllText(file);
            direct += TestAttrCount(text);
            // base-list references to a *TestsBase: ": XTestsBase" or ", XTestsBase" (generics stripped by \w+).
            foreach (Match m in Regex.Matches(text, @"[,:]\s*(\w+TestsBase)\b"))
                if (baseCounts.TryGetValue(m.Groups[1].Value, out var c)) inherited += c;
        }
        return inherited + direct;
    }

    // ---- helpers
    private static IEnumerable<string> CsFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => { var n = Norm(f); return !n.Contains("/bin/") && !n.Contains("/obj/"); });

    // count [Fact] / [Theory] attribute usages (one per test method; tolerates [Theory(...)] / [Fact(DisplayName=...)]).
    private static int TestAttrCount(string text) => Regex.Matches(text, @"\[\s*(Fact|Theory)\b").Count;

    private static string Norm(string p) => p.Replace('\\', '/');

    // neutralize CRLF/LF + trailing whitespace so --check isn't a false "stale" across platforms / .gitattributes,
    // and the generation timestamp (the "As of" date) so --check gates on the computed facts, not the calendar day
    // it runs — otherwise every PR/push on a day after the last FACTS commit fails purely on the refreshed date.
    private static string Normalize(string s) =>
        Regex.Replace(s.Replace("\r\n", "\n").TrimEnd(), @"(?m)^_As of: \d{4}-\d{2}-\d{2}\b", "_As of: <date>");

    private const string Template = """
# MMCA.Common — Canonical Facts

**Single source of truth for the framework-wide facts that otherwise drift across dozens of docs.**
_As of: {{AS_OF}} (framework {{VERSION}}) — **generated from source by `build/facts`; do not hand-edit the numbers below.**_

> **Rule: link here, don't restate.** Other docs (scorecards, CLAUDE.md files, READMEs, the LinkedIn/Medium
> campaigns) must **reference** these facts rather than copy the numbers inline. A "thirteen packages"
> count or a "~68 fitness methods" figure typed into another file is drift waiting to happen — point at
> this file. The ADR table and its count/range are owned by the published ADR index
> (<https://ivanball.github.io/docs/adr/>, source `docs-src/adr/README.md` in the Website repo). Per-repo
> facts (test totals, scorecard indices) live in that repo's published scorecard, **not** here.

## Framework version
- **Current: `{{VERSION}}`** (MinVer-derived from the git tag at `main` HEAD).
- All consumers (**MMCA.ADC**, **MMCA.Store**, MMCA.Helpdesk) track this version in **lockstep** — every
  `MMCA.Common.*` entry in each consumer's `Directory.Packages.props` is bumped together (ADR-016; no phased
  rollout).

## Published packages — **{{PKG_COUNT}}**
Released in lockstep to GitHub Packages (the packable projects under `Source/` carrying a `<PackageId>`):

{{PKG_LIST}}

## Architecture Decision Records
The ADRs live in the Website repo (`docs-src/adr/`), published at
<https://ivanball.github.io/docs/adr/>. The **canonical index is that repo's `docs-src/adr/README.md`**:
it owns the range/count and the one-line summaries. Do not restate the `(001-NNN)` range elsewhere.

## Architecture fitness functions
- **{{FIT_METHODS}} test methods across {{FIT_BASES}} abstract `*TestsBase` classes**, shipped once in the
  `MMCA.Common.Testing.Architecture` package (ADR-015) and re-run as thin subclasses across all consuming
  repos (Common, ADC, Store).
- MMCA.Common's own build executes **{{COMMON_EXEC}}** of them (the methods of the bases its arch-tests
  subclass, plus its Common-only direct tests, e.g. `FrameworkSanityTests`/`SpecificationFitnessTests`).

## Governance rubric
- The 34-category evaluation rubric is canonical in the Website repo
  (`docs-src/governance/ArchitectureEvaluationCriteria.md`, published at
  <https://ivanball.github.io/docs/governance/>). Each repo's published scorecard is scored against it.

## Where the rest lives (don't duplicate)
- Per-repo scores/indices and test counts → that repo's published scorecard
  (<https://ivanball.github.io/docs/governance/>).
- Remediation status → that repo's published backlog (same location); cross-repo themes →
  workspace `Docs/Architecture/ArchitectureRemediation.md` (rollup, links only).
- Release notes / security model → `CHANGELOG.md` / `SECURITY.md` (this repo); versioning policy /
  FinOps → the published guides (<https://ivanball.github.io/docs/guides/>).

## Keeping this current
This file is **generated from source** and **gated in CI** (the `facts` job runs `--check` and fails the
build if the committed file drifts from source). Regenerate it at each framework release with:

```bash
dotnet run --project build/facts -- .
```

The figures are computed directly: version from the git tag, package count from packable `Source/*`
projects, and the fitness counts from `MMCA.Common.Testing.Architecture`.
The workspace `Tools/invtool -- facts ./MMCA.Common` shares this same generator and produces an identical file.
""";
}
