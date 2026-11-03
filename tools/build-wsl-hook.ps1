# Builds static Linux hooks. Requires the selected Rust musl targets in WSL.
param([string]$Distro = 'Ubuntu', [ValidateSet('x64', 'arm64')][string[]]$Architecture = @('x64', 'arm64'))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$linuxRepo = (& wsl -d $Distro --exec wslpath -u $repo).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot access repository in WSL' }
$destination = Join-Path $repo 'artifacts/wsl-hook'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($arch in $Architecture) {
  $target = @{ x64 = 'x86_64-unknown-linux-musl'; arm64 = 'aarch64-unknown-linux-musl' }[$arch]
  & wsl -d $Distro --exec sh -c 'export PATH="$HOME/.cargo/bin:$PATH"; export RUSTFLAGS="-C linker=rust-lld"; cargo build --locked --release --manifest-path "$1/native/Cargo.toml" -p agwinterm-hook --target "$2" --target-dir "$1/artifacts/native-linux"' build $linuxRepo $target
  if ($LASTEXITCODE -ne 0) { throw "Linux hook build failed (requires Rust target $target)" }
  Copy-Item -LiteralPath (Join-Path $repo "artifacts/native-linux/$target/release/agwinterm-hook") -Destination (Join-Path $destination "agwinterm-hook-linux-$arch") -Force
}
