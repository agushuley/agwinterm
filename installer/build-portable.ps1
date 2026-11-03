# Build the PORTABLE agwinterm: one self-contained single-file exe, no installer needed.
# Output: installer\Output\agwinterm-portable-<ver>-g<sha>-win-x64.exe
# Themes, icon, CLI and WSL helpers are embedded. Tools are extracted lazily to a
# content-addressed directory under %LOCALAPPDATA%\agwinterm\portable-tools.
$ErrorActionPreference = "Stop"
$here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$root   = Split-Path -Parent $here
$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }
$stage  = Join-Path $here "stage-portable"
$outDir = Join-Path $here "Output"

# version = the installer's AppVersion define (single source of truth)
$iss = Get-Content (Join-Path $here "agwinterm.iss") -Raw
if ($iss -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in agwinterm.iss" }
$ver = $Matches[1]
$sha = (& git -C $root rev-parse --short HEAD).Trim()
if ([string]::IsNullOrWhiteSpace($sha)) { throw "git rev-parse --short HEAD failed (build from a git checkout)" }

Write-Host "== clean stage ==" -ForegroundColor Cyan
# Verify the cleanup target before removing a previous build.
$stage = [IO.Path]::GetFullPath($stage)
if ($stage -ne [IO.Path]::GetFullPath((Join-Path $root 'installer/stage-portable'))) { throw 'Unexpected portable stage path' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, $outDir | Out-Null

$tools = Join-Path $stage 'tools'
New-Item -ItemType Directory -Path $tools | Out-Null
& $dotnet publish (Join-Path $root 'src/Agwinterm.Ctl/Agwinterm.Ctl.csproj') `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$ver -o $tools
if ($LASTEXITCODE -ne 0) { throw 'portable CLI publish failed' }
& cargo build --locked --release --manifest-path (Join-Path $root 'native/Cargo.toml') -p agwinterm-hook
if ($LASTEXITCODE -ne 0) { throw 'portable Windows hook build failed' }
& (Join-Path $root 'tools/stage-wsl-hooks.ps1') -Destination $tools
$archive = Join-Path $stage 'tools.zip'
$toolFiles = @('agwintermctl.exe', 'agwinterm-hook.exe', 'agwinterm-hook-linux-x64', 'agwinterm-hook-linux-arm64') |
  ForEach-Object { Join-Path $tools $_ }
Compress-Archive -LiteralPath $toolFiles -DestinationPath $archive

Write-Host "== publish single-file portable exe (v$ver) ==" -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\Agwinterm.Win32\Agwinterm.Win32.csproj") `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:SatelliteResourceLanguages=en `
  -p:DebugType=none `
  -p:Version=$ver `
  "-p:PortableToolsArchive=$archive" `
  -o $stage
if ($LASTEXITCODE -ne 0) { throw "portable publish failed" }

$exe = Join-Path $stage "Agwinterm.Win32.exe"
if (-not (Test-Path $exe)) { throw "publish produced no Agwinterm.Win32.exe" }

# Ship ONLY the exe — themes/assets land next to it in the stage as loose copies, but the
# embedded fallbacks make the bare exe self-sufficient.
$portableName = "agwinterm-portable-$ver-g$sha-win-x64.exe"
$shortName = "agwinterm-$ver-g$sha-win-x64.exe"
$dst = Join-Path $outDir $portableName
Copy-Item $exe $dst -Force
Write-Host ("== done: {0} ({1:N1} MB) ==" -f $dst, ((Get-Item $dst).Length/1MB)) -ForegroundColor Green

# A local release is also made available at the shared workspace level. CI keeps its release
# artifacts under installer\Output, so it must not create an untracked workspace copy.
if (-not $env:GITHUB_ACTIONS) {
  $releasesDir = Join-Path (Split-Path -Parent $root) "releases"
  New-Item -ItemType Directory -Force $releasesDir | Out-Null
  foreach ($name in @($portableName, $shortName)) {
    $localArtifact = Join-Path $releasesDir $name
    if (Test-Path $localArtifact) { throw "local release artifact already exists: $localArtifact" }
    Copy-Item $dst $localArtifact
    Write-Host ("== local release: {0} ==" -f $localArtifact) -ForegroundColor Green
  }
}
