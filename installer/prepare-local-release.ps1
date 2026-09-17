# Advance the source-controlled local-release suffix once before a new local release.
# Usage: .\installer\prepare-local-release.ps1
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root = Split-Path -Parent $here
$iss = Join-Path $here "agwinterm.iss"

& git -C $root diff --quiet -- installer/agwinterm.iss
if ($LASTEXITCODE -ne 0) { throw "Commit or discard existing installer/agwinterm.iss changes before preparing a local release." }
& git -C $root diff --cached --quiet -- installer/agwinterm.iss
if ($LASTEXITCODE -ne 0) { throw "Unstage installer/agwinterm.iss before preparing a local release." }

$text = [System.IO.File]::ReadAllText($iss)
$appMatch = [regex]::Match($text, '(?m)^#define\s+AppVersion\s+"(?<base>\d+\.\d+\.\d+)-ah\.(?<build>\d+)"$')
if (-not $appMatch.Success) { throw "AppVersion must use <upstream-version>-ah.<n>." }

$base = $appMatch.Groups['base'].Value
$current = [int]$appMatch.Groups['build'].Value
if ($current -eq [int]::MaxValue) { throw "Local release counter is exhausted." }
$next = $current + 1
$appVersion = "$base-ah.$next"
$peVersion = "$base.$next"

$expectedPe = "#define VersionInfoVersion `"$base.$current`""
if (-not $text.Contains($expectedPe, [System.StringComparison]::Ordinal)) {
    throw "VersionInfoVersion must match AppVersion's local release counter."
}

$updated = $text.Replace($appMatch.Value, "#define AppVersion `"$appVersion`"")
$updated = $updated.Replace($expectedPe, "#define VersionInfoVersion `"$peVersion`"")
$updated = [regex]::Replace($updated, '(?m)^; PE file version: four numeric fields; the fourth is the ah build index \(here \d+\)\.$', "; PE file version: four numeric fields; the fourth is the ah build index (here $next).")

if ($updated -eq $text) { throw "No local release version was updated." }
if ($PSCmdlet.ShouldProcess($iss, "advance local release to $appVersion")) {
    [System.IO.File]::WriteAllText($iss, $updated)
    Write-Host "Prepared local release $appVersion. Commit installer/agwinterm.iss before building."
}
