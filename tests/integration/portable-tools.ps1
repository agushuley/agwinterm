# Verify the actual single-file release, isolated from its build-stage sidecars.
param([string]$Executable)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $Executable) {
    $Executable = (Get-ChildItem (Join-Path $repo 'installer/Output/agwinterm-portable-*-win-x64.exe') |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Executable) { throw 'Build the portable executable first' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('agwinterm-portable-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $exe = Join-Path $root 'portable.exe'
    Copy-Item -LiteralPath $Executable -Destination $exe
    $destination = Join-Path $root "tools space ' quote"
    $process = Start-Process -FilePath $exe -ArgumentList @('--extract-tools', "`"$destination`"") -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Portable extraction timed out' }
    if ($process.ExitCode -ne 0) { throw "Portable extraction failed: $($process.ExitCode)" }
    $directories = @(Get-ChildItem -LiteralPath $destination -Directory)
    if ($directories.Count -ne 1) { throw 'Expected exactly one extracted toolset' }
    $tools = $directories[0].FullName
    foreach ($name in @('agwintermctl.exe', 'agwinterm-hook.exe', 'agwinterm-hook-linux-x64', 'agwinterm-hook-linux-arm64')) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $tools $name)).Hash
        $expected = (Get-FileHash -LiteralPath (Join-Path $repo "installer/stage-portable/tools/$name")).Hash
        if ($actual -ne $expected) { throw "Portable tool differs from build input: $name" }
    }
    foreach ($pair in @(@('x64', 62), @('arm64', 183))) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $tools "agwinterm-hook-linux-$($pair[0])"))
        if ($bytes[0] -ne 127 -or [Text.Encoding]::ASCII.GetString($bytes, 1, 3) -ne 'ELF' -or
            $bytes[4] -ne 2 -or $bytes[5] -ne 1 -or [BitConverter]::ToUInt16($bytes, 18) -ne $pair[1]) {
            throw "Wrong Linux binary architecture: $($pair[0])"
        }
    }
    $help = & (Join-Path $tools 'agwintermctl.exe') --help
    if ($LASTEXITCODE -ne 0 -or -not ($help -match 'agwintermctl')) { throw 'Extracted CLI cannot run standalone' }
    Write-Host 'PASS standalone portable contains a runnable CLI, Windows bridge, x64 and ARM64 Linux helpers'
}
finally {
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolved -Leaf) -match '^agwinterm-portable-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
