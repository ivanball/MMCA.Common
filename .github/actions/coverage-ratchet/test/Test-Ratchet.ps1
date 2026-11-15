# Self-test for ratchet.ps1 (spec assertions 10 to 12, plus the entry-point check). Runs the script as
# a separate pwsh process so exit codes are tested for real, against fixture.cobertura.xml.
# Exit 0 when every case passes, 1 otherwise. Run: pwsh .github/actions/coverage-ratchet/test/Test-Ratchet.ps1
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$script = Join-Path (Split-Path $here -Parent) 'ratchet.ps1'
$fixture = Join-Path $here 'fixture.cobertura.xml'
$work = (New-Item -ItemType Directory -Force (Join-Path ([IO.Path]::GetTempPath()) "ratchet-test-$([guid]::NewGuid().ToString('N'))")).FullName
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Ratchet([string[]]$Arguments) {
  $out = & pwsh -NoProfile -File $script @Arguments 2>&1 | Out-String
  [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $out }
}
function Assert-Case([string]$Name, [bool]$Ok, [string]$Detail) {
  if ($Ok) { Write-Host "PASS  $Name" } else { Write-Host "FAIL  $Name`n$Detail"; $failures.Add($Name) }
}
function New-Baseline([string[]]$Lines) {
  $p = Join-Path $work "baseline-$([guid]::NewGuid().ToString('N')).txt"
  [IO.File]::WriteAllText($p, (($Lines -join "`n") + "`n")); $p
}
$common = @('-Reports', $fixture, '-Include', '^MMCA\.Fixture\.', '-Exclude', '\.Tests$')

try {
  # 10a. A new method above the threshold fails, and is named.
  $empty = New-Baseline @()
  $r = Invoke-Ratchet ($common + @('-Check', 'crap', '-Baseline', $empty))
  $ran = $r.Out -match 'OrderService::Risky'
  Assert-Case 'new method above threshold fails' ($r.Exit -ne 0 -and $ran) $r.Out
  Assert-Case 'test assemblies are excluded' ($ran -and $r.Out -notmatch 'HugeUntestedTestHelper') $r.Out
  Assert-Case 'lambdas are not reported as methods' ($ran -and $r.Out -notmatch 'b__3_0') $r.Out

  # 10b. The same methods in the baseline pass.
  $ok = New-Baseline @("OrderService::LoadAsync`t72", "OrderService::Risky`t110")
  $r = Invoke-Ratchet ($common + @('-Check', 'crap', '-Baseline', $ok))
  Assert-Case 'baselined methods pass' ($r.Exit -eq 0) $r.Out

  # 10c. A baselined method whose CRAP rose past baseline + 0.5 fails.
  $low = New-Baseline @("OrderService::LoadAsync`t72", "OrderService::Risky`t100")
  $r = Invoke-Ratchet ($common + @('-Check', 'crap', '-Baseline', $low))
  Assert-Case 'rising CRAP on a baselined method fails' ($r.Exit -ne 0 -and $r.Out -match 'OrderService::Risky') $r.Out
  $within = New-Baseline @("OrderService::LoadAsync`t72", "OrderService::Risky`t109.7")
  $r = Invoke-Ratchet ($common + @('-Check', 'crap', '-Baseline', $within))
  Assert-Case 'a rise within the 0.5 tolerance passes' ($r.Exit -eq 0) $r.Out

  # Stale entry: warns, does not fail.
  $stale = New-Baseline @("OrderService::LoadAsync`t72", "OrderService::Risky`t110", "Gone::Method`t50")
  $r = Invoke-Ratchet ($common + @('-Check', 'crap', '-Baseline', $stale))
  Assert-Case 'a stale baseline entry warns but passes' ($r.Exit -eq 0 -and $r.Out -match 'Gone::Method') $r.Out

  # 11. write-baseline folds the state machine into its method.
  $written = Join-Path $work 'written.txt'
  $r = Invoke-Ratchet ($common + @('-Check', 'crap', '-Baseline', $written, '-Mode', 'write-baseline'))
  $text = if (Test-Path $written) { Get-Content $written -Raw } else { '' }
  Assert-Case 'write-baseline exits 0' ($r.Exit -eq 0) $r.Out
  Assert-Case 'state machine reported as Outer::Name' ($text -match '(?m)^OrderService::LoadAsync\t72' -and $text -notmatch 'd__7') $text
  Assert-Case 'baseline holds only methods above the threshold' ($text -match 'OrderService::Risky' -and $text -notmatch 'OrderService::Safe') $text

  # 12a. A missing report fails closed, and says why.
  $r = Invoke-Ratchet @('-Check', 'crap', '-Reports', (Join-Path $work 'nope.xml'), '-Include', '^MMCA\.', '-Baseline', $empty)
  Assert-Case 'missing report fails closed' ($r.Exit -ne 0 -and $r.Out -match 'no coverage report') $r.Out

  # 12b. Zero matched methods fails closed, and says why.
  $r = Invoke-Ratchet @('-Check', 'crap', '-Reports', $fixture, '-Include', '^Nothing\.Matches\.', '-Baseline', $empty)
  Assert-Case 'zero matched methods fails closed' ($r.Exit -ne 0 -and $r.Out -match 'no methods matched') $r.Out

  # Entry points: an untested controller action fails and is named; the tested one and the ctor are not.
  $r = Invoke-Ratchet ($common + @('-Check', 'entrypoints', '-Baseline', $empty))
  $epRan = $r.Out -match 'OrdersController::GetAsync'
  Assert-Case 'untested entry point fails' ($r.Exit -ne 0 -and $epRan) $r.Out
  Assert-Case 'tested entry point and ctor are not flagged' ($epRan -and $r.Out -notmatch 'OrdersController::ListAsync' -and $r.Out -notmatch '\.ctor') $r.Out
  $ep = New-Baseline @("OrdersController::GetAsync`t0")
  $r = Invoke-Ratchet ($common + @('-Check', 'entrypoints', '-Baseline', $ep))
  Assert-Case 'baselined entry point passes' ($r.Exit -eq 0) $r.Out
}
finally {
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

if ($failures.Count) { Write-Host "`n$($failures.Count) case(s) failed: $($failures -join '; ')"; exit 1 }
Write-Host "`nAll ratchet cases passed."
