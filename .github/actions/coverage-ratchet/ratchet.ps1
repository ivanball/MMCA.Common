# Coverage ratchet (ADR-147): fails on NEW risk, never on existing debt.
#   -Check crap         a method not in the baseline above -Threshold CRAP fails, and a baselined
#                       method whose CRAP rose past its recorded value + 0.5 fails.
#   -Check entrypoints  an entry-point method (class matches -EntrypointClasses) at 0% line coverage
#                       across every report fails unless the baseline lists it.
#   -Mode write-baseline writes the baseline from the current reports and exits 0.
# Stale baseline entries (now under the threshold, now covered, or gone) are warnings, so improving
# coverage never turns a build red. Fails CLOSED: no report, an unreadable report, a missing baseline in
# check mode, or zero methods matching -Include is an error. CRAP = cx^2 * (1 - cov)^3 + cx, with cov
# the method's best line rate across the reports. Async state machines (Outer.<Name>d__N.MoveNext) are
# folded into the method they implement; lambdas and display classes are skipped; methods are keyed
# by short class name so a namespace move does not churn the baseline.
param(
  [Parameter(Mandatory)] [ValidateSet('crap', 'entrypoints')] [string]$Check,
  [Parameter(Mandatory)] [string]$Reports,
  [Parameter(Mandatory)] [string]$Include,
  [string]$Exclude = '(\.Tests$|\.IntegrationTests$|\.Testing|Migrations)',
  [Parameter(Mandatory)] [string]$Baseline,
  [double]$Threshold = 30,
  [string]$EntrypointClasses = 'Controller$',
  [ValidateSet('check', 'write-baseline')] [string]$Mode = 'check'
)
$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture

function Stop-Closed([string]$Message) {
  Write-Host "::error::coverage-ratchet: $Message"
  exit 1
}

# Resolve report files: comma, space or newline separated files or folders (folders are searched for *.cobertura.xml).
$files = @()
foreach ($item in ($Reports -split '[,\s]+' | Where-Object { $_ })) {
  if (Test-Path -LiteralPath $item -PathType Container) {
    $files += @(Get-ChildItem -LiteralPath $item -Recurse -File -Filter '*.cobertura.xml' | ForEach-Object FullName)
  } elseif (Test-Path -LiteralPath $item -PathType Leaf) {
    $files += (Resolve-Path -LiteralPath $item).Path
  } else {
    $files += @(Get-ChildItem -Path $item -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
  }
}
if (-not $files) { Stop-Closed "no coverage report found at '$Reports'" }

if (-not ('CoverageRatchetParser' -as [type])) {
  Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Globalization; using System.Text.RegularExpressions; using System.Xml;
public sealed class RatchetRow { public string Class, Name; public double Cx, LineRate; public bool StateMachine; }
public static class CoverageRatchetParser {
  static readonly Regex Sm = new Regex(@"^(?<outer>.+?)\.<(?<m>[A-Za-z_][A-Za-z0-9_]*)>d__\d+$", RegexOptions.CultureInvariant);
  public static void Read(string path, Regex include, Regex exclude, Dictionary<string, RatchetRow> rows) {
    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
    using var r = XmlReader.Create(path, settings);
    string cls = null, file = null; bool asmOk = false;
    while (r.Read()) {
      if (r.NodeType != XmlNodeType.Element) continue;
      if (r.Name == "package") { var asm = r.GetAttribute("name") ?? ""; asmOk = include.IsMatch(asm) && !exclude.IsMatch(asm); }
      else if (r.Name == "class") { cls = r.GetAttribute("name") ?? ""; file = r.GetAttribute("filename") ?? ""; }
      else if (r.Name == "method" && asmOk) {
        if (file.EndsWith(".g.cs", StringComparison.Ordinal) || file.EndsWith(".generated.cs", StringComparison.Ordinal) || file.Contains("/obj/") || file.Contains("\\obj\\")) continue;
        var name = r.GetAttribute("name") ?? ""; var owner = cls; var sm = false;
        var m = Sm.Match(cls);
        if (m.Success && name == "MoveNext") { owner = m.Groups["outer"].Value; name = m.Groups["m"].Value; sm = true; }
        else if (cls.Contains("<") || name.StartsWith("<", StringComparison.Ordinal)) continue;
        var dot = owner.LastIndexOf('.');
        var row = new RatchetRow {
          Class = dot < 0 ? owner : owner.Substring(dot + 1), Name = name, StateMachine = sm,
          Cx = double.Parse(r.GetAttribute("complexity") ?? "1", CultureInfo.InvariantCulture),
          LineRate = double.Parse(r.GetAttribute("line-rate") ?? "0", CultureInfo.InvariantCulture) };
        Merge(rows, row);
      }
    }
  }
  static void Merge(Dictionary<string, RatchetRow> rows, RatchetRow x) {
    var key = x.Class + "::" + x.Name;
    if (!rows.TryGetValue(key, out var e)) { rows[key] = x; return; }
    // The state machine holds the body; across reports the best line rate wins (union of the tiers).
    if (x.StateMachine && !e.StateMachine) { rows[key] = x; return; }
    if (e.StateMachine && !x.StateMachine) return;
    e.Cx = Math.Max(e.Cx, x.Cx); e.LineRate = Math.Max(e.LineRate, x.LineRate);
  }
}
'@
}

$rows = [System.Collections.Generic.Dictionary[string, RatchetRow]]::new()
$includeRx = [regex]::new($Include); $excludeRx = [regex]::new($Exclude)
foreach ($f in $files) {
  try { [CoverageRatchetParser]::Read($f, $includeRx, $excludeRx, $rows) }
  catch { Stop-Closed "could not read coverage report '$f': $($_.Exception.Message)" }
}
if ($rows.Count -eq 0) { Stop-Closed "no methods matched include '$Include' (exclude '$Exclude') in $($files.Count) report(s)" }

function Format-Value([double]$v) { [math]::Round($v, 1).ToString('0.#', $inv) }

# Current measurement: key -> value (CRAP, or 0 for an untested entry point).
$current = [ordered]@{}
$classRx = [regex]::new($EntrypointClasses)
foreach ($kv in ($rows.GetEnumerator() | Sort-Object Key)) {
  $row = $kv.Value
  if ($Check -eq 'crap') {
    $crap = $row.Cx * $row.Cx * [math]::Pow(1 - $row.LineRate, 3) + $row.Cx
    if ($crap -gt $Threshold) { $current[$kv.Key] = $crap }
  } elseif ($classRx.IsMatch($row.Class) -and $row.Name -notmatch '^(\.ctor|\.cctor|get_|set_)' -and $row.LineRate -eq 0) {
    $current[$kv.Key] = 0.0
  }
}

if ($Mode -eq 'write-baseline') {
  $lines = @($current.GetEnumerator() | ForEach-Object { "$($_.Key)`t$(Format-Value $_.Value)" })
  $dir = Split-Path -Parent $Baseline
  if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
  [IO.File]::WriteAllText($Baseline, $(if ($lines) { ($lines -join "`n") + "`n" } else { '' }))
  Write-Host "coverage-ratchet: wrote $($lines.Count) $Check baseline entries to $Baseline from $($rows.Count) methods in $($files.Count) report(s)."
  exit 0
}

if (-not (Test-Path -LiteralPath $Baseline -PathType Leaf)) { Stop-Closed "baseline '$Baseline' not found; create it with mode write-baseline" }
$base = @{}
foreach ($line in [IO.File]::ReadAllLines($Baseline)) {
  if (-not $line.Trim() -or $line.StartsWith('#')) { continue }
  $parts = $line -split "`t"
  $base[$parts[0]] = if ($parts.Count -gt 1) { [double]::Parse($parts[1], $inv) } else { 0.0 }
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($kv in $current.GetEnumerator()) {
  $row = $rows[$kv.Key]
  $detail = "cx $(Format-Value $row.Cx), line coverage $([math]::Round($row.LineRate * 100))%"
  if (-not $base.ContainsKey($kv.Key)) {
    $violations.Add($(if ($Check -eq 'crap') { "NEW   $($kv.Key)  CRAP $(Format-Value $kv.Value) > $Threshold ($detail)" } else { "NEW   $($kv.Key)  entry point with no coverage in any report ($detail)" }))
  } elseif ($Check -eq 'crap' -and $kv.Value -gt $base[$kv.Key] + 0.5) {
    $violations.Add("ROSE  $($kv.Key)  CRAP $(Format-Value $kv.Value), baseline $(Format-Value $base[$kv.Key]) ($detail)")
  }
}
$stale = @($base.Keys | Where-Object { -not $current.Contains($_) } | Sort-Object)

Write-Host "coverage-ratchet ($Check): $($rows.Count) methods from $($files.Count) report(s); $($current.Count) over the line, $($base.Count) baselined, $($violations.Count) violation(s), $($stale.Count) stale."
foreach ($v in $violations) { Write-Host "::error::coverage-ratchet $v"; Write-Host "  $v" }
foreach ($s in $stale) { Write-Host "::warning::coverage-ratchet: $s is in the baseline but no longer over the line (improved, covered or gone); remove it from $Baseline" ; Write-Host "  STALE $s" }

if ($env:GITHUB_STEP_SUMMARY) {
  $summary = @("### Coverage ratchet: $Check", '', "| Methods | Over the line | Baselined | Violations | Stale |", '|---|---|---|---|---|', "| $($rows.Count) | $($current.Count) | $($base.Count) | $($violations.Count) | $($stale.Count) |", '')
  if ($violations.Count) { $summary += @('Violations:', '') + ($violations | ForEach-Object { "- ``$_``" }) + '' }
  if ($stale) { $summary += @('Stale baseline entries (remove them):', '') + ($stale | ForEach-Object { "- ``$_``" }) }
  Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value ($summary -join "`n")
}

if ($violations.Count) { exit 1 }
exit 0
