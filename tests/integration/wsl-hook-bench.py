import json, os, pathlib, random, shutil, statistics, subprocess, sys, time
home, cli, target, pipe, output = sys.argv[1:]
repo = pathlib.Path(__file__).resolve().parents[2]
root = pathlib.Path(home)
assert root.parent == pathlib.Path('/tmp') and root.name.startswith('agwinterm-research-')
wrapper = root / '.local/bin/agwintermctl'
wrapper.parent.mkdir(parents=True, exist_ok=True)
wrapper.write_text('#!/bin/sh\nexec '+__import__('shlex').quote(cli)+' "$@"\n')
wrapper.chmod(0o700)
hook = root / 'hook.py'
shutil.copyfile(repo / 'tests/integration/fixtures/wsl-hook-python.py', hook)
os.environ.update(HOME=home, AGWINTERM_SESSION_ID=target, AGWINTERM_PIPE=pipe)
os.environ['AGWINTERM_HOOK_EXE']=str(repo / 'native/target/release/agwinterm-hook.exe')
native=['/bin/sh',str(repo / 'native/agwinterm-hook/hook.sh'),'codex']
linux_hook=root/'agwinterm-hook'
shutil.copyfile(repo/'artifacts/wsl-hook/agwinterm-hook-linux-x64',linux_hook)
linux_hook.chmod(0o700)
linux_native=[str(linux_hook),'codex']
payload=json.dumps({'session_id':'research-session','cwd':home,'last_assistant_message':'Done','padding':'x'*2048}).encode()
status=[cli,'session','status','completed','--target',target,'--pipe',pipe,'--json']
cases={
 'linux_true':['/bin/true'],
 'python_empty':['python3','-c','pass'],
 'python_imports_json':['python3','-c','import json,os,pathlib,subprocess,sys; json.load(sys.stdin)'],
 'windows_cmd':['/mnt/c/Windows/System32/cmd.exe','/d','/c','exit','0'],
 'windows_cli_status':status,
 'full_python_hook':['python3',str(hook),'codex','stop'],
 'full_rust_hook':native+['stop'],
 'linux_rust_hook':linux_native+['stop'],
}
samples={k:[] for k in cases}
def run(k):
 start=time.perf_counter_ns()
 p=subprocess.run(cases[k],input=payload,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=20)
 elapsed=(time.perf_counter_ns()-start)/1e6
 if p.returncode: raise RuntimeError((k,p.returncode,p.stderr.decode(errors='replace')))
 if k=='windows_cli_status': assert json.loads(p.stdout)['ok'],p.stdout
 return elapsed
for k in cases: run(k)
rng=random.Random(42)
for _ in range(40):
 keys=list(cases);rng.shuffle(keys)
 for k in keys:samples[k].append(run(k))
# The hook suppresses errors: explicitly verify its effect after resetting status.
subprocess.run([cli,'session','status','active','--target',target,'--pipe',pipe],check=True,stdout=subprocess.DEVNULL)
run('full_python_hook')
tree=json.loads(subprocess.check_output([cli,'tree','--pipe',pipe,'--json']))
matches=[s for w in tree['result']['workspaces'] for s in w['sessions'] if s['id']==target]
assert matches[0]['status']=='completed',matches
subprocess.run([cli,'session','status','active','--target',target,'--pipe',pipe],check=True,stdout=subprocess.DEVNULL)
run('full_rust_hook')
tree=json.loads(subprocess.check_output([cli,'tree','--pipe',pipe,'--json']))
assert next(s for w in tree['result']['workspaces'] for s in w['sessions'] if s['id']==target)['status']=='completed'
for name, command in [('python_bind',['python3',str(hook),'codex','bind']),('rust_bind',native+['bind']),('linux_rust_bind',linux_native+['bind'])]:
 if name=='rust_bind': payload=json.dumps({'session_id':'research-rust-session','cwd':home}).encode()
 if name=='linux_rust_bind': payload=json.dumps({'session_id':'research-linux-session','cwd':home}).encode()
 cases[name]=command
 samples[name]=[run(name) for _ in range(10)]
# Exercise Codex transcript filtering over the WSL UNC path, including nested agents.
transcript=root/'transcript with spaces.jsonl'
for source,expected in [('cli','completed'),('subagent','active'),(None,'active')]:
 transcript.write_text(json.dumps({'payload':{'source':source}})+'\n')
 subprocess.run([cli,'session','status','active','--target',target,'--pipe',pipe],check=True,stdout=subprocess.DEVNULL)
 payload=json.dumps({'transcript_path':str(transcript)}).encode()
 run('linux_rust_hook')
 if source=='cli':
  for label,command in [('linux_rust_transcript',linux_native+['stop']),('python_transcript',['python3',str(hook),'codex','stop'])]:
   cases[label]=command
   samples[label]=[run(label) for _ in range(40)]
 tree=json.loads(subprocess.check_output([cli,'tree','--pipe',pipe,'--json']))
 assert next(s for w in tree['result']['workspaces'] for s in w['sessions'] if s['id']==target)['status']==expected,(source,tree)
payload=b'{}'
# The installed native hot path must not need Python or other PATH executables.
saved_path=os.environ.get('PATH','');os.environ['PATH']=''
run('linux_rust_hook');os.environ['PATH']=saved_path
tree=json.loads(subprocess.check_output([cli,'tree','--pipe',pipe,'--json']))
assert next(s for w in tree['result']['workspaces'] for s in w['sessions'] if s['id']==target)['status']=='completed'
# Missing server: both the shell and receiver must finish promptly.
oldpipe=os.environ['AGWINTERM_PIPE'];os.environ['AGWINTERM_PIPE']='agwinterm-research-missing'
missing_ms=run('full_rust_hook');linux_missing_ms=run('linux_rust_hook');os.environ['AGWINTERM_PIPE']=oldpipe
assert missing_ms < 500,missing_ms
assert linux_missing_ms < 500,linux_missing_ms
result={'python':sys.version,'platform':os.uname().release,'hook_delivery_verified':True,'missing_server_ms':missing_ms,'linux_missing_server_ms':linux_missing_ms,
 'results':{k:{'median_ms':statistics.median(v),'p95_ms':sorted(v)[max(0,__import__('math').ceil(len(v)*.95)-1)],'min_ms':min(v),'samples_ms':v} for k,v in samples.items()}}
pathlib.Path(output).write_text(json.dumps(result,indent=2))
print(json.dumps({k:{a:round(b,2) for a,b in v.items() if a!='samples_ms'} for k,v in result['results'].items()},indent=2))

