<#
.SYNOPSIS
    Promotes every PublicAPI.Unshipped.txt under Source/ into its sibling PublicAPI.Shipped.txt.

.DESCRIPTION
    Run once per release, in the last commit before the vX.Y.Z tag (see CONTRIBUTING.md, "Releases
    are separate"). For each PublicAPI.Unshipped.txt it:
      1. removes from PublicAPI.Shipped.txt every line named by a *REMOVED* entry;
      2. appends every other non-empty, non-header Unshipped line to Shipped;
      3. sorts Shipped with ordinal ordering (duplicates dropped), keeping "#nullable enable" as
         the first line when the file had it;
      4. resets Unshipped to its header line(s) ("#nullable enable" kept if present).
    Line endings (CRLF or LF), a UTF-8 BOM and the presence of a final newline are preserved per
    file. A second run is a no-op: Unshipped is header-only, so nothing is appended or removed and
    the sort is already stable.

    Supports -WhatIf: reports what each project would change and writes nothing.

.PARAMETER Root
    Directory scanned for PublicAPI.Unshipped.txt files. Defaults to the repository's Source/.

.EXAMPLE
    pwsh build/publicapi/Promote-PublicApi.ps1 -WhatIf
    pwsh build/publicapi/Promote-PublicApi.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Root = (Join-Path $PSScriptRoot '..' '..' 'Source')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$NullableHeader = '#nullable enable'
$RemovedPrefix = '*REMOVED*'

function Read-ApiFile {
    param([string] $Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = [System.Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
    $newline = if ($text.Contains("`r`n")) { "`r`n" } elseif ($text.Contains("`n")) { "`n" } else { "`r`n" }
    $endsWithNewline = $text.EndsWith("`n")
    $lines = @($text -split "`r?`n" | ForEach-Object { $_.TrimEnd() })

    [pscustomobject]@{
        HasBom          = $hasBom
        Newline         = $newline
        EndsWithNewline = $endsWithNewline
        Lines           = $lines
    }
}

function Write-ApiFile {
    param([string] $Path, [string[]] $Lines, $Format)

    $text = ($Lines -join $Format.Newline)
    if ($Format.EndsWithNewline -and $Lines.Count -gt 0) {
        $text += $Format.Newline
    }

    $encoding = New-Object System.Text.UTF8Encoding($Format.HasBom)
    [System.IO.File]::WriteAllText($Path, $text, $encoding)
}

function Get-HeaderLines {
    param([string[]] $Lines)

    # The header is the leading run of '#' directives (in practice just "#nullable enable").
    $header = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        if ($line.StartsWith('#')) { $header.Add($line) } else { break }
    }
    return , $header.ToArray()
}

$unshippedFiles = @(Get-ChildItem -Path $Root -Filter 'PublicAPI.Unshipped.txt' -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Sort-Object FullName)

if ($unshippedFiles.Count -eq 0) {
    throw "No PublicAPI.Unshipped.txt found under '$Root'."
}

$report = foreach ($unshippedFile in $unshippedFiles) {
    $project = Split-Path -Leaf $unshippedFile.DirectoryName
    $shippedPath = Join-Path $unshippedFile.DirectoryName 'PublicAPI.Shipped.txt'
    if (-not (Test-Path $shippedPath)) {
        throw "$project has PublicAPI.Unshipped.txt but no sibling PublicAPI.Shipped.txt."
    }

    $unshipped = Read-ApiFile $unshippedFile.FullName
    $shipped = Read-ApiFile $shippedPath

    $unshippedHeader = Get-HeaderLines $unshipped.Lines
    $unshippedBody = @($unshipped.Lines | Select-Object -Skip $unshippedHeader.Count | Where-Object { $_ -ne '' })

    $removed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $added = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $unshippedBody) {
        if ($line.StartsWith($RemovedPrefix, [System.StringComparison]::Ordinal)) {
            [void] $removed.Add($line.Substring($RemovedPrefix.Length))
        }
        else {
            $added.Add($line)
        }
    }

    $shippedHasNullable = $shipped.Lines.Count -gt 0 -and $shipped.Lines[0] -eq $NullableHeader
    $shippedBody = @($shipped.Lines | Where-Object { $_ -ne '' -and $_ -ne $NullableHeader })

    $missing = @($removed | Where-Object { $shippedBody -cnotcontains $_ })
    foreach ($entry in $missing) {
        Write-Warning "$project : *REMOVED* entry not found in PublicAPI.Shipped.txt: $entry"
    }

    $kept = @($shippedBody | Where-Object { -not $removed.Contains($_) })
    $merged = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($line in $kept) { [void] $merged.Add($line) }
    foreach ($line in $added) { [void] $merged.Add($line) }

    $newShipped = @()
    if ($shippedHasNullable -or $unshippedHeader -contains $NullableHeader) {
        $newShipped += $NullableHeader
    }
    $newShipped += @($merged)

    $newUnshipped = @($unshippedHeader)

    $shippedChanged = (($shipped.Lines | Where-Object { $_ -ne '' }) -join "`n") -cne ($newShipped -join "`n")
    $unshippedChanged = $unshippedBody.Count -gt 0

    if ($shippedChanged -or $unshippedChanged) {
        if ($PSCmdlet.ShouldProcess($project, "promote $($added.Count) added / $($removed.Count) removed PublicAPI entries")) {
            Write-ApiFile $shippedPath $newShipped $shipped
            Write-ApiFile $unshippedFile.FullName $newUnshipped $unshipped
        }
    }

    [pscustomobject]@{
        Project         = $project
        ShippedBefore   = $shippedBody.Count
        UnshippedBefore = $unshippedBody.Count
        Added           = $added.Count
        Removed         = $removed.Count
        ShippedAfter    = $merged.Count
        UnshippedAfter  = 0
    }
}

$report | Format-Table -AutoSize | Out-String -Width 200 | Write-Output
