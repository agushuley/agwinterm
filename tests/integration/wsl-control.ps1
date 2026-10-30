param([string]$Distro = 'Ubuntu', [switch]$NativeHooks, [string]$LinuxUser = '', [switch]$NoPython, [switch]$Portable)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$exe = Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl = Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
if ($Portable) {
    $exe = (Get-ChildItem (Join-Path $repo 'installer/Output/agwinterm-portable-*-win-x64.exe') |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
    if (-not $exe) { throw 'Build the portable executable first' }
    $ctl = Join-Path $repo 'installer/stage-portable/tools/agwintermctl.exe'
    $NativeHooks = $true
}
$id = 'agwinterm-wsl-test-' + [Guid]::NewGuid().ToString('N')
$state = Join-Path $env:LOCALAPPDATA $id
$linuxHome = '/tmp/' + $id
$selectors = @('-d', $Distro)
if ($LinuxUser) { $selectors += @('-u', $LinuxUser) }
$process = $null
function Ctl([string[]]$Arguments) {
    $raw = (& $ctl @Arguments --pipe $id --json) -join "`n"
    try { $reply = $raw | ConvertFrom-Json } catch { throw "CLI $Arguments returned: $raw" }
    if (-not $reply.ok) { throw ($reply | ConvertTo-Json -Compress) }
    return $reply.result
}
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Host "PASS $Message"
}
try {
    # The installer normally places the CLI next to the app; a solution build keeps separate outputs.
    if (-not $Portable) {
        Get-ChildItem -LiteralPath (Split-Path $ctl) -Filter 'agwintermctl.*' | Copy-Item -Destination (Split-Path $exe)
    }
    if ($NativeHooks -and -not $Portable) {
        Copy-Item -LiteralPath (Join-Path $repo 'native/target/release/agwinterm-hook.exe') -Destination (Split-Path $exe) -Force
        foreach ($arch in @('x64', 'arm64')) {
            Copy-Item -LiteralPath (Join-Path $repo "artifacts/wsl-hook/agwinterm-hook-linux-$arch") -Destination (Split-Path $exe) -Force
        }
    }
    New-Item -ItemType Directory -Path $state | Out-Null
    & wsl @selectors --exec mkdir -p $linuxHome
    if ($LASTEXITCODE -ne 0) { throw 'Could not create disposable Linux HOME' }
    $profileEnv = @{ HOME = $linuxHome; ZDOTDIR = $linuxHome; CLAUDE_CONFIG_DIR = "$linuxHome/.claude"; CODEX_HOME = "$linuxHome/.codex"; XDG_CONFIG_HOME = "$linuxHome/.config" }
    if ($NoPython) {
        & wsl @selectors --exec sh -c 'mkdir -p "$1/tools"; for tool in wslpath uname mktemp cp chmod rm rmdir mkdir sh bash sleep env; do ln -s "$(command -v "$tool")" "$1/tools/$tool"; done' setup $linuxHome
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create Python-free PATH' }
        $profileEnv.PATH = "$linuxHome/tools"
    }
    @{ default = 'WSL test'; profiles = @(@{ name = 'WSL test'; command = 'wsl.exe'; args = $selectors + @('--exec', 'bash', '--noprofile', '-i'); cwd = $linuxHome; env = $profileEnv }) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $state 'profiles.json') -Encoding utf8
    'new-session-dir = current' | Set-Content -LiteralPath (Join-Path $state 'agwinterm.conf')
    $process = Start-Process -FilePath $exe -ArgumentList @('--app-id', $id, '--pipe', $id, '--no-restore', '--default-session-host', 'inprocess') -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 300
        try { $null = Ctl @('ping'); break } catch { if ($i -eq 29) { throw } }
    }
    $tree = Ctl @('tree')
    $session = $tree.workspaces[0].sessions[0].id
    try { $null = Ctl @('session', 'type', ('printf "TEST_HOME=%s\n" "$HOME"' + [char]13), '--target', $session) }
    catch { throw ("Initial shell failed: " + (Ctl @('session', 'text', '--target', $session))) }
    Start-Sleep -Seconds 1
    $initial = Ctl @('session', 'text', '--target', $session)
    Check (($initial -replace '\s', '').Contains("TEST_HOME=$linuxHome")) 'Linux HOME is isolated before any installer runs'
    if ($NoPython) {
        $null = Ctl @('session', 'type', ('if command -v python3; then printf "PYTHON_%s\n" PRESENT; else printf "PYTHON_%s\n" ABSENT; fi' + [char]13), '--target', $session)
        Start-Sleep -Seconds 1
        Check ((Ctl @('session', 'text', '--target', $session)).Contains('PYTHON_ABSENT')) 'Python is absent from the integration PATH'
    }
    foreach ($kind in @('cli', 'shell', 'hooks', 'skill')) {
        $result = Ctl @('install', $kind, '--target', $session)
        Check ($result -match 'WSL' -and $result -notmatch 'refused') "WSL $kind installer uses Linux HOME"
        if ($kind -eq 'hooks') { Check ($result.Contains("$linuxHome/.claude/settings.json")) 'Hook destinations remain inside test HOME' }
        if ($kind -eq 'hooks' -and $NativeHooks) { Check ($result.Contains('Hook runtime: native Rust')) 'Installer selects the native Linux runtime' }
    }
    $null = Ctl @('session', 'type', ('source "$HOME/.bashrc"; printf "ENV=%s/%s\n" "$AGWINTERM_ENABLED" "$AGWINTERM_PIPE"; agwintermctl ping' + [char]13), '--target', $session)
    Start-Sleep -Seconds 2
    $screen = Ctl @('session', 'text', '--target', $session)
    Check (($screen -replace '\s', '') -match "ENV=1/$id") 'Identity crosses WSLENV'
    Check ($screen -match 'agwinterm \d') 'Linux CLI reaches the private Windows control pipe'
    $null = Ctl @('session', 'type', ('mkdir -p "$HOME/path with spaces"; cd "$HOME/path with spaces"' + [char]13), '--target', $session)
    Start-Sleep -Seconds 1
    $copy = Ctl @('session', 'duplicate', '--target', $session)
    Start-Sleep -Seconds 1
    $null = Ctl @('session', 'type', ('printf "PWD=%s\n" "$PWD"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $screen = Ctl @('session', 'text', '--target', $copy)
    Check (($screen -replace '\s', '').Contains("PWD=$linuxHome/pathwithspaces")) "Duplicate opens the Linux cwd ($screen)"
    $saved = Get-ChildItem -LiteralPath (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    Check (($saved -join '').Contains("$linuxHome/path with spaces")) 'Persistence retains Linux cwd'
    $fixture = (& wsl -d $Distro --exec wslpath -u (Join-Path $PSScriptRoot 'wsl-agent.py')).Trim()
    # Python is only the test's fake agent, explicitly outside the integration PATH.
    $agentCommand = "bash -c 'exec -a codex /usr/bin/python3 `"$fixture`" --dangerously-bypass-approvals-and-sandbox'"
    $null = Ctl @('session', 'type', ($agentCommand + [char]13), '--target', $copy)
    Start-Sleep -Seconds 3
    $screen = Ctl @('session', 'text', '--target', $copy)
    Check ($screen.Contains('WSL_HOOKS_FINISHED')) 'Linux agent hooks finish successfully'
    $saved = Get-ChildItem -LiteralPath (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    Check (($saved -join '').Contains('codex resume wsl-test-conversation --dangerously-bypass-approvals-and-sandbox')) 'Linux ancestry binds the session ID and permission flags'
    $tree = Ctl @('tree')
    $copiedSession = $tree.workspaces.sessions | Where-Object id -eq $copy
    Check ($copiedSession.status -eq 'completed') 'Linux Stop hook updates pane status'
    $overlay = Ctl @('session', 'overlay', 'open', '--command', 'printf "OVERLAY_%s\n" "LINUX"; sleep 2', '--wait', '--target', $copy)
    Start-Sleep -Seconds 1
    $overlayText = Ctl @('session', 'overlay', 'text', '--target', $copy)
    Check ($overlayText.text -match 'OVERLAY_LINUX') 'Overlay commands run in Linux'
    $null = Ctl @('session', 'overlay', 'close', '--target', $copy)
    $null = Ctl @('session', 'bind', 'none', '--target', $copy)
    $null = Ctl @('config', 'set', 'restore-commands', 'true')
    $null = Ctl @('session', 'type', ('sleep 15' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $capture = Ctl @('restore', 'capture', '--target', $copy)
    Check (($capture | ConvertTo-Json -Depth 10).Contains('sleep 15')) 'Foreground capture reads Linux processes'
    Check $capture.replayOnRestore 'Captured commands are enabled for restart'
    Start-Sleep -Seconds 1
    $saved = Get-ChildItem -LiteralPath (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
    Check (-not ($saved -join '').Contains('wsl-test-conversation')) 'Test agent binding is cleared before restart'
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit()
    $process = Start-Process -FilePath $exe -ArgumentList @('--app-id', $id, '--pipe', $id, '--default-session-host', 'inprocess') -WindowStyle Hidden -PassThru
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 300
        try { $null = Ctl @('ping'); break } catch { if ($i -eq 29) { throw } }
    }
    # A fresh capture proves a Linux child is running in the restored pane, rather than
    # merely finding the old command in persisted scrollback. In-process shells cannot be adopted.
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        $capture = Ctl @('restore', 'capture', '--target', $copy)
        if (($capture | ConvertTo-Json -Depth 10).Contains('sleep 15')) { break }
    }
    Check (($capture | ConvertTo-Json -Depth 10).Contains('sleep 15')) 'App restart replays the captured Linux command'
    # Let the restored child finish; a raw Ctrl+C through ConPTY is not reliable.
    Start-Sleep -Seconds 16
    $null = Ctl @('session', 'type', ('printf "RESTORED_%s=%s\n" "CWD" "$PWD"' + [char]13), '--target', $copy)
    Start-Sleep -Seconds 1
    $screen = Ctl @('session', 'text', '--target', $copy)
    Check (($screen -replace '\s', '').Contains("RESTORED_CWD=$linuxHome/pathwithspaces")) "App restart restores the distribution and Linux directory ($screen)"
    Write-Host 'WSL control integration passed.'
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    # Generated paths are confined to test namespaces; never accept a user-supplied deletion path.
    $expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA $id))
    if ($state -eq $expected -and $id -match '^agwinterm-wsl-test-[a-f0-9]{32}$') {
        if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Recurse -Force }
        & wsl @selectors --exec python3 -c 'import shutil,sys; p=sys.argv[1]; assert p.startswith("/tmp/agwinterm-wsl-test-") and "/" not in p[5:]; shutil.rmtree(p)' $linuxHome
    }
}

