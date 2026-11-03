# Shared by the installer and the single-file portable build. CI supplies Linux artifacts;
# local builds compile missing targets through WSL.
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
foreach ($arch in @('x64', 'arm64')) {
  $source = Join-Path $root "artifacts/wsl-hook/agwinterm-hook-linux-$arch"
  if (-not (Test-Path -LiteralPath $source)) {
    & (Join-Path $PSScriptRoot 'build-wsl-hook.ps1') -Architecture $arch
  }
  Copy-Item -LiteralPath $source -Destination $Destination -Force
}
Copy-Item -LiteralPath (Join-Path $root 'native/target/release/agwinterm-hook.exe') -Destination $Destination -Force
