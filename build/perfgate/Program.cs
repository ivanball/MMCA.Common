// Latency/allocation regression gate over BenchmarkDotNet results (rubric section 12).
//
// Usage: dotnet run --project build/perfgate -- <bdnResultsDir> <baselineJsonPath>
//
// Reads every *-report-full-compressed.json BenchmarkDotNet artifact in <bdnResultsDir> and applies
// the committed baseline rules:
//   - allocationCeilingsBytes: per-benchmark managed bytes per operation must not exceed the ceiling
//     (allocations are deterministic across machines, so ceilings are tight).
//   - ratioFloors: mean(slowBenchmark) / mean(fastBenchmark) must stay >= minRatio (a
//     machine-independent effectiveness invariant, e.g. the compiled-expression cache staying three
//     orders of magnitude ahead of the recompile anti-pattern).
// Every benchmark named by a rule must be present in the results (a silently-missing benchmark would
// otherwise turn the gate vacuous). Exits 1 with a violation list on any failure.

using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: perfgate <bdnResultsDir> <baselineJsonPath>");
    return 2;
}

var resultsDir = args[0];
var baselinePath = args[1];

var artifactFiles = Directory.Exists(resultsDir)
    ? Directory.GetFiles(resultsDir, "*-report-full-compressed.json", SearchOption.TopDirectoryOnly)
    : [];
if (artifactFiles.Length == 0)
{
    Console.Error.WriteLine($"FAIL: no *-report-full-compressed.json artifacts found in '{resultsDir}'. Run the benchmarks with --exporters json first.");
    return 1;
}

// benchmark method name -> (mean ns, allocated bytes/op)
var measured = new Dictionary<string, (double MeanNs, double AllocatedBytes)>(StringComparer.Ordinal);
foreach (var file in artifactFiles)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    foreach (var benchmark in doc.RootElement.GetProperty("Benchmarks").EnumerateArray())
    {
        var method = benchmark.GetProperty("Method").GetString()!;
        var mean = benchmark.GetProperty("Statistics").GetProperty("Mean").GetDouble();
        var allocated = benchmark.TryGetProperty("Memory", out var memory)
            && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes)
            ? bytes.GetDouble()
            : double.NaN;
        measured[method] = (mean, allocated);
    }
}

using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));
var violations = new List<string>();

if (baseline.RootElement.TryGetProperty("allocationCeilingsBytes", out var ceilings))
{
    foreach (var rule in ceilings.EnumerateObject())
    {
        if (!measured.TryGetValue(rule.Name, out var m))
        {
            violations.Add($"benchmark '{rule.Name}' (allocation ceiling) is missing from the results; the gate would be vacuous");
        }
        else if (double.IsNaN(m.AllocatedBytes))
        {
            violations.Add($"benchmark '{rule.Name}' reported no allocation data; keep [MemoryDiagnoser] on the suite");
        }
        else if (m.AllocatedBytes > rule.Value.GetDouble())
        {
            violations.Add($"benchmark '{rule.Name}' allocates {m.AllocatedBytes:F0} B/op, above the committed ceiling of {rule.Value.GetDouble():F0} B/op");
        }
    }
}

if (baseline.RootElement.TryGetProperty("ratioFloors", out var floors))
{
    foreach (var rule in floors.EnumerateArray())
    {
        var slow = rule.GetProperty("slowBenchmark").GetString()!;
        var fast = rule.GetProperty("fastBenchmark").GetString()!;
        var minRatio = rule.GetProperty("minRatio").GetDouble();

        if (!measured.TryGetValue(slow, out var s) || !measured.TryGetValue(fast, out var f))
        {
            violations.Add($"ratio floor {slow}/{fast}: one or both benchmarks missing from the results; the gate would be vacuous");
            continue;
        }

        var ratio = s.MeanNs / f.MeanNs;
        if (ratio < minRatio)
        {
            violations.Add($"ratio floor {slow}/{fast}: measured {ratio:F1}x, below the committed floor of {minRatio:F0}x (the fast path lost its edge; likely a broken cache or an accidental hot-path regression)");
        }
    }
}

foreach (var (name, m) in measured.OrderBy(kv => kv.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"  {name,-36} mean = {m.MeanNs,14:F1} ns   allocated = {m.AllocatedBytes,8:F0} B/op");
}

if (violations.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {violations.Count} perf-baseline violation(s):");
    foreach (var v in violations)
    {
        Console.Error.WriteLine($"  - {v}");
    }

    return 1;
}

Console.WriteLine($"OK: {measured.Count} benchmarks within the committed baseline ({Path.GetFileName(baselinePath)}).");
return 0;
