param(
    [string]$Exe = "$PSScriptRoot/../../src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe",
    [string]$OutputDirectory = "$PSScriptRoot/../../artifacts/validation/workspace-settings/integration"
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$identity = 'agwinterm-settings-' + [guid]::NewGuid().ToString('N')
$pipeName = $identity
$appDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $identity
if (Test-Path -LiteralPath $appDirectory) { throw 'Unique app-data directory already exists.' }
New-Item -ItemType Directory -Path $appDirectory | Out-Null
$process = $null
$checks = 0

function Invoke-Api([string]$Command, $Arguments = @{}, [string]$Target = '') {
    $connection = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
    try {
        $connection.Connect(5000)
        $writer = [IO.StreamWriter]::new($connection, [Text.UTF8Encoding]::new($false), 1024, $true)
        $reader = [IO.StreamReader]::new($connection, [Text.UTF8Encoding]::new($false), $false, 1024, $true)
        try {
            $writer.AutoFlush = $true
            $writer.WriteLine((@{ cmd = $Command; args = $Arguments; target = $Target } | ConvertTo-Json -Depth 12 -Compress))
            $read = $reader.ReadLineAsync()
            if (-not $read.Wait(15000)) { throw 'API reply timed out.' }
            return $read.Result | ConvertFrom-Json
        } finally { $reader.Dispose(); $writer.Dispose() }
    } finally { $connection.Dispose() }
}

function Check([string]$Name, [bool]$Passed) {
    if (-not $Passed) { throw "FAIL: $Name" }
    $script:checks++
    Write-Host "PASS: $Name"
}

function Start-TestApp([bool]$Restore) {
    $arguments = @('--app-id', $identity, '--pipe', $pipeName)
    if (-not $Restore) { $arguments += '--no-restore' }
    $script:process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        try { if ((Invoke-Api 'ping').ok) { return } } catch { }
        if ($process.HasExited) { throw 'Test app exited before answering.' }
    }
    throw 'Test app did not answer.'
}

function Wait-Report([string]$Pane) {
    $path = Join-Path $appDirectory "report-$Pane.json"
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if (Test-Path -LiteralPath $path) {
            try { return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Child environment report did not arrive.'
}

function Wait-Session([string]$Session) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $tree = Invoke-Api 'tree'
        foreach ($workspace in $tree.result.workspaces) {
            foreach ($node in $workspace.sessions) { if ($node.id -eq $Session) { return $node } }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Session did not appear.'
}

try {
    $reportScript = @'
$report = [ordered]@{}
foreach ($name in 'LAYER','GLOBAL_ONLY','PROFILE_ONLY','WORKSPACE_ONLY','SESSION_ONLY','TOKEN','EMPTY','AGWINTERM_SESSION_ID') {
    $report[$name] = [Environment]::GetEnvironmentVariable($name)
}
$report.cwd = [Environment]::CurrentDirectory
$report.profile = 'workspace-profile'
$path = Join-Path '__APP_DIR__' ('report-' + $env:AGWINTERM_SESSION_ID + '.json')
[IO.File]::WriteAllText($path, ($report | ConvertTo-Json -Compress))
'@
    $reportScript = $reportScript.Replace('__APP_DIR__', $appDirectory.Replace("'", "''"))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($reportScript))
    $profiles = @{
        default = 'base'
        profiles = @(
            @{ name = 'base'; command = 'cmd.exe'; args = @('/d', '/k') },
            @{ name = 'workspace-profile'; command = 'powershell.exe'; args = @('-NoProfile', '-NoExit', '-EncodedCommand', $encoded); env = @{ LAYER = 'profile'; PROFILE_ONLY = 'yes' } }
        )
    }
    $profiles | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $appDirectory 'profiles.json') -Encoding utf8
    "confirm-close-session = false`nrestore-commands = false`nsession-host = in-process" |
        Set-Content -LiteralPath (Join-Path $appDirectory 'agwinterm.conf') -Encoding utf8
    @{ env = @{ LAYER = 'global'; GLOBAL_ONLY = 'yes' } } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $appDirectory 'environment.json') -Encoding utf8
    Start-TestApp $false
    Check 'isolated app-data directory is used' (Test-Path -LiteralPath (Join-Path $appDirectory 'agwinterm.conf'))
    $root = Join-Path $appDirectory 'project'
    New-Item -ItemType Directory -Path $root | Out-Null
    $created = Invoke-Api 'workspace.new' @{ name = 'project'; defaultDir = $root; defaultProfile = 'workspace-profile'; env = @{ LAYER = 'workspace'; WORKSPACE_ONLY = 'yes'; TOKEN = "abc=def`nbcd=`nqw=" } }
    Check 'workspace options accepted' $created.ok
    $workspaceId = [string]$created.result
    $settings = Invoke-Api 'workspace.settings' @{} $workspaceId
    Check 'workspace options read back' ($settings.ok -and $settings.result.settings.defaultProfile -eq 'workspace-profile')
    $started = Invoke-Api 'session.new' @{ workspace = $workspaceId; env = @{ LAYER = 'session'; SESSION_ONLY = 'yes'; EMPTY = '' } }
    Check 'session overrides accepted' $started.ok
    $sessionId = [string]$started.result
    $null = Wait-Session $sessionId
    $report = Wait-Report $sessionId
    Check 'session wins over workspace profile global' ($report.LAYER -eq 'session')
    Check 'all lower-level values are present' ($report.GLOBAL_ONLY -eq 'yes' -and $report.PROFILE_ONLY -eq 'yes' -and $report.WORKSPACE_ONLY -eq 'yes')
    Check 'literal multiline value survives spawning' ($report.TOKEN -ceq "abc=def`nbcd=`nqw=")
    Check 'workspace default profile selected' ($report.profile -eq 'workspace-profile')
    Check 'workspace default cwd selected' ($report.cwd -eq $root)
    Check 'tree does not expose env values' ((Invoke-Api 'tree' | ConvertTo-Json -Depth 12) -notmatch 'abc=def')
    $bad = Invoke-Api 'environment.set' @{ scope = 'workspace'; env = @{ AGWINTERM_PIPE = 'forbidden' } } $workspaceId
    Check 'reserved overrides are refused' (-not $bad.ok)
    $badSettings = Invoke-Api 'workspace.settings' @{ settings = @{ defaultProfile = 'missing'; env = @{ LAYER = 'bad' } } } $workspaceId
    Check 'invalid settings are refused atomically' (-not $badSettings.ok -and (Invoke-Api 'workspace.settings' @{} $workspaceId).result.settings.env.LAYER -eq 'workspace')
    $split = Invoke-Api 'session.split' @{ op = 'on' } $sessionId
    Check 'split created' $split.ok
    $splitReport = Wait-Report ([string]$split.result)
    Check 'split inherits session overrides and profile' ($splitReport.LAYER -eq 'session' -and $splitReport.SESSION_ONLY -eq 'yes')
    $duplicate = Invoke-Api 'session.duplicate' @{} $sessionId
    Check 'duplicate created' $duplicate.ok
    $duplicateReport = Wait-Report ([string]$duplicate.result)
    Check 'duplicate inherits session overrides' ($duplicateReport.LAYER -eq 'session')
    $updated = Invoke-Api 'environment.set' @{ scope = 'global'; env = @{ GLOBAL_ONLY = 'updated'; LAYER = 'global-new' } }
    Check 'global overrides can be updated' $updated.ok
    $future = Invoke-Api 'session.new' @{ workspace = $workspaceId }
    Check 'new session accepted after global update' $future.ok
    $futureReport = Wait-Report ([string]$future.result)
    Check 'new process sees current global and workspace overrides' ($futureReport.GLOBAL_ONLY -eq 'updated' -and $futureReport.LAYER -eq 'workspace')
    Check 'existing child report is unchanged' ((Wait-Report $sessionId).GLOBAL_ONLY -eq 'yes')
    $profileUpdate = Invoke-Api 'environment.set' @{ scope = 'profile'; env = @{ PROFILE_ONLY = 'updated-profile' } } 'workspace-profile'
    Check 'profile env API persists existing JSON format' ($profileUpdate.ok -and (Get-Content -LiteralPath (Join-Path $appDirectory 'profiles.json') -Raw | ConvertFrom-Json).profiles[1].env.PROFILE_ONLY -eq 'updated-profile')
    $sessionUpdate = Invoke-Api 'environment.set' @{ scope = 'session'; env = @{ LAYER = 'future-session'; SESSION_ONLY = 'yes' } } $sessionId
    Check 'session env API updates future overrides' $sessionUpdate.ok
    $readback = Invoke-Api 'environment.get' @{ scope = 'session' } $sessionId
    Check 'session overrides read back' ($readback.ok -and $readback.result.env.LAYER -eq 'future-session')
    $clear = Invoke-Api 'workspace.default-dir' @{ clear = $true } $workspaceId
    $afterClear = Invoke-Api 'workspace.settings' @{} $workspaceId
    Check 'legacy directory API preserves profile and env' ($clear.ok -and $afterClear.result.settings.defaultProfile -eq 'workspace-profile' -and $afterClear.result.settings.env.LAYER -eq 'workspace')
    Start-Sleep -Milliseconds 800
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(15000)) { throw 'App did not exit cleanly.' }
    Get-ChildItem -LiteralPath $appDirectory -Filter 'report-*.json' | Remove-Item
    Start-TestApp $true
    $null = Wait-Session $sessionId
    $restored = Wait-Report $sessionId
    Check 'restore retains session overrides and current lower layers' ($restored.LAYER -eq 'future-session' -and $restored.GLOBAL_ONLY -eq 'updated' -and $restored.PROFILE_ONLY -eq 'updated-profile')
    $result = @{ checks = $checks; exitCode = 0 }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding utf8
    Write-Host "PASS: $checks workspace settings checks"
} finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
    }
    Copy-Item -LiteralPath $appDirectory -Destination (Join-Path $OutputDirectory $identity) -Recurse -Force
    Remove-Item -LiteralPath $appDirectory -Recurse -Force
}
