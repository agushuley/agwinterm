# Build the portable agwinterm exe inside Docker (no host .NET SDK required).
# Writes installer\Output\agwinterm-portable-<ver>-g<sha>-win-x64.exe and copies to
# ..\releases\ (ag_terms workspace), matching build-portable.ps1.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root = Split-Path -Parent $here
$releasesDir = Join-Path (Split-Path -Parent $root) "releases"

$sha = (& git -C $root rev-parse --short HEAD).Trim()
if ([string]::IsNullOrWhiteSpace($sha)) { throw "git rev-parse --short HEAD failed (build from a git checkout)" }

$outDir = Join-Path $here "Output"
New-Item -ItemType Directory -Force $outDir, $releasesDir | Out-Null

$image = "agwinterm-portable-build:local"
Write-Host "== docker build ($image) ==" -ForegroundColor Cyan
docker build `
  -f (Join-Path $root "docker\Dockerfile.portable") `
  --build-arg "GIT_SHA=$sha" `
  -t $image `
  $root
if ($LASTEXITCODE -ne 0) { throw "docker build failed" }

Write-Host "== docker run (publish + copy artifacts) ==" -ForegroundColor Cyan
docker run --rm `
  -v "${outDir}:/out" `
  -v "${releasesDir}:/releases" `
  $image
if ($LASTEXITCODE -ne 0) { throw "docker run failed" }

Get-ChildItem $outDir -Filter "agwinterm-portable-*-g$sha-win-x64.exe" |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 |
  ForEach-Object { Write-Host ("== host output: {0} ({1:N1} MB) ==" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
