$ErrorActionPreference='Stop'
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$linuxRepo=(& wsl -d Ubuntu --exec wslpath -u $repo).Trim()
New-Item -ItemType Directory -Force -Path (Join-Path $repo 'artifacts/wsl-hook-research') | Out-Null
$exe=Join-Path $repo 'src/Agwinterm.Win32/bin/x64/Release/net10.0-windows/win-x64/Agwinterm.Win32.exe'
$ctl=Join-Path $repo 'src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe'
$id='agwinterm-research-'+[guid]::NewGuid().ToString('N')
$state=Join-Path $env:LOCALAPPDATA $id
$linuxHome='/tmp/'+$id
$process=$null
# Verify the hard deadline against a server that accepts but never replies.
$stalled=[IO.Pipes.NamedPipeServerStream]::new($id+'-stall',[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
$client=$null
try {
 $connected=$stalled.WaitForConnectionAsync()
 $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $repo 'native/target/release/agwinterm-hook.exe'))
 $start.UseShellExecute=$false
 $start.CreateNoWindow=$true
 $start.RedirectStandardInput=$true
 foreach($arg in @('codex','active','test-pane',($id+'-stall'),'1','/tmp')){$start.ArgumentList.Add($arg)}
 $watch=[Diagnostics.Stopwatch]::StartNew()
 $client=[Diagnostics.Process]::Start($start)
 $client.StandardInput.Write('{}')
 $client.StandardInput.Close()
 if(-not $connected.Wait(1500)){throw 'Test client never connected'}
 if(-not $client.WaitForExit(4000)){throw 'Hook deadline failed'}
 if($watch.ElapsedMilliseconds -lt 1700 -or $watch.ElapsedMilliseconds -gt 3500){throw 'Unexpected hook deadline'}
 Write-Host 'PASS stalled pipe is bounded by the two-second deadline'
} finally {
 if($client -and -not $client.HasExited){$client.Kill();$client.WaitForExit()}
 $stalled.Dispose()
}
try {
 New-Item -ItemType Directory -Path $state | Out-Null
 @{default='WSL';profiles=@(@{name='WSL';command='wsl.exe';args=@('-d','Ubuntu','--exec','bash','--noprofile','-i');cwd='/tmp';env=@{HOME=$linuxHome}})} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $state 'profiles.json') -Encoding utf8
 $process=Start-Process -FilePath $exe -ArgumentList @('--app-id',$id,'--pipe',$id,'--no-restore','--default-session-host','inprocess') -WindowStyle Hidden -PassThru
 for($i=0;$i -lt 30;$i++) {
  Start-Sleep -Milliseconds 300
  $raw=(& $ctl tree --pipe $id --json) -join "`n"
  try {$tree=$raw|ConvertFrom-Json;if($tree.ok -and $tree.result.workspaces[0].sessions[0].id){break}}catch{}
 }
 if(-not $tree.result.workspaces[0].sessions[0].id){throw "No session: $raw"}
 $session=$tree.result.workspaces[0].sessions[0].id
 & wsl -d Ubuntu --exec bash -c 'exec -a codex python3 "$@"' bench "$linuxRepo/tests/integration/wsl-hook-bench.py" $linuxHome "$linuxRepo/src/Agwinterm.Ctl/bin/Release/net10.0-windows/agwintermctl.exe" $session $id "$linuxRepo/artifacts/wsl-hook-research/results.json"
 if($LASTEXITCODE -ne 0){throw 'Benchmark failed'}
 $saved=Get-ChildItem -LiteralPath (Join-Path $state 'windows') -Filter '*.json' | ForEach-Object {Get-Content -LiteralPath $_.FullName -Raw}
 if(-not ($saved -join '').Contains('codex resume research-linux-session')){throw 'Linux native binding not persisted'}
 Write-Host 'PASS native binding persisted'
} finally {
 if($process -and -not $process.HasExited){Stop-Process -Id $process.Id -Force;$process.WaitForExit()}
 $expected=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA $id))
 if($state -eq $expected -and $id -match '^agwinterm-research-[a-f0-9]{32}$') {
  if(Test-Path -LiteralPath $state){Remove-Item -LiteralPath $state -Recurse -Force}
  & wsl -d Ubuntu --exec python3 -c 'import pathlib,shutil,sys; p=pathlib.Path(sys.argv[1]); assert p.parent==pathlib.Path("/tmp") and p.name.startswith("agwinterm-research-"); shutil.rmtree(p,ignore_errors=True)' $linuxHome
 }
}

