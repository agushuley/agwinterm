use super::*;
use std::{
    fs,
    path::Path,
    process::{Command, Stdio},
};

fn stat_fields(stat: &str) -> Result<(u32, u64), Box<dyn std::error::Error>> {
    let end = stat.rfind(')').ok_or("invalid proc stat")?;
    let fields: Vec<_> = stat[end + 1..].split_whitespace().collect();
    Ok((
        fields.get(1).ok_or("missing parent PID")?.parse()?,
        fields.get(19).ok_or("missing start time")?.parse()?,
    ))
}

fn ancestry(pid: u32, pane: &str) -> Result<Value, Box<dyn std::error::Error>> {
    let identity = format!("AGWINTERM_SESSION_ID={pane}");
    if !fs::read(format!("/proc/{pid}/environ"))?
        .split(|b| *b == 0)
        .any(|s| s == identity.as_bytes())
    {
        return Err("hook does not belong to the pane".into());
    }
    let mut rows = Vec::new();
    let mut current = pid;
    for _ in 0..64 {
        let root = format!("/proc/{current}");
        let (parent, created) = stat_fields(&fs::read_to_string(format!("{root}/stat"))?)?;
        let bytes = fs::read(format!("{root}/cmdline"))?;
        let argv: Vec<_> = bytes
            .split(|b| *b == 0)
            .filter(|s| !s.is_empty())
            .map(String::from_utf8_lossy)
            .collect();
        let name = argv
            .first()
            .and_then(|s| Path::new(s.as_ref()).file_name())
            .and_then(|s| s.to_str())
            .unwrap_or("");
        let command = argv
            .iter()
            .map(|s| format!("\"{}\"", s.replace('"', "\\\"")))
            .collect::<Vec<_>>()
            .join(" ");
        rows.push(json!({"Pid":current,"ParentPid":parent,"Name":name,"CommandLine":command,"Created":created}));
        if parent <= 1 || parent == current {
            return Ok(json!(rows));
        }
        current = parent;
    }
    Err("ancestry exceeds 64 processes".into())
}

fn foreground() -> Result<Value, Box<dyn std::error::Error>> {
    let mut input = Vec::new();
    io::stdin().take(MAX_EVENT + 1).read_to_end(&mut input)?;
    if input.len() as u64 > MAX_EVENT {
        return Err("foreground request too large".into());
    }
    let request: Value = serde_json::from_slice(&input)?;
    let panes: Vec<String> = serde_json::from_value(request["panes"].clone())?;
    let mut denied: Vec<String> = serde_json::from_value(request["denied"].clone())?;
    denied.extend(
        [
            "sh", "bash", "zsh", "fish", "dash", "nu", "env", "login", "init", "systemd",
        ]
        .map(str::to_owned),
    );
    let mut result = serde_json::Map::new();
    for entry in fs::read_dir("/proc")? {
        let Ok(entry) = entry else {
            continue;
        };
        let Some(pid) = entry
            .file_name()
            .to_str()
            .and_then(|s| s.parse::<u32>().ok())
        else {
            continue;
        };
        // Processes can exit or become inaccessible while we inspect them; skip those rows.
        let inspect = || -> Result<Option<(String, String)>, Box<dyn std::error::Error>> {
            let root = entry.path();
            let stat = fs::read_to_string(root.join("stat"))?;
            let fields: Vec<_> = stat[stat.rfind(')').ok_or("invalid stat")? + 1..]
                .split_whitespace()
                .collect();
            if fields.get(4).ok_or("missing terminal")?.parse::<i64>()? == 0
                || fields
                    .get(5)
                    .ok_or("missing foreground group")?
                    .parse::<i64>()?
                    != i64::from(pid)
            {
                return Ok(None);
            }
            let environment = fs::read(root.join("environ"))?;
            let Some(pane) = panes.iter().find(|pane| {
                let identity = format!("AGWINTERM_SESSION_ID={pane}");
                environment
                    .split(|b| *b == 0)
                    .any(|s| s == identity.as_bytes())
            }) else {
                return Ok(None);
            };
            let command = fs::read(root.join("cmdline"))?;
            let argv: Vec<_> = std::str::from_utf8(&command)?
                .split('\0')
                .filter(|s| !s.is_empty())
                .collect();
            let Some(first) = argv.first() else {
                return Ok(None);
            };
            let name = Path::new(first)
                .file_name()
                .and_then(|s| s.to_str())
                .unwrap_or("");
            if denied.iter().any(|s| s == name) {
                return Ok(None);
            }
            Ok(Some((
                pane.clone(),
                argv.iter()
                    .map(|s| crate::install::quote(s))
                    .collect::<Vec<_>>()
                    .join(" "),
            )))
        };
        if let Ok(Some((pane, command))) = inspect() {
            result.insert(pane, json!(command));
        }
    }
    Ok(Value::Object(result))
}

pub(super) fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args().skip(1).collect();
    if args.first().is_some_and(|s| s == "--install") {
        return crate::install::run();
    }
    if args.first().is_some_and(|s| s == "--foreground") {
        println!("{}", foreground()?);
        return Ok(());
    }
    if args.first().is_some_and(|s| s == "--ancestry") {
        let pid = args.get(1).ok_or("missing PID")?.parse()?;
        let pane = args.get(2).ok_or("missing pane")?;
        println!("{}", ancestry(pid, pane)?);
        return Ok(());
    }
    let pane = env::var("AGWINTERM_SESSION_ID").unwrap_or_default();
    if pane.is_empty() {
        return Ok(());
    }
    let agent = args.first().ok_or("missing agent")?;
    let state = args.get(1).ok_or("missing state")?;
    if agent == "claude"
        && (env::var("DEVIN_PROJECT_DIR").is_ok_and(|s| !s.is_empty())
            || env::var("CLAUDE_CODE_ENTRYPOINT").is_ok_and(|s| s != "cli")
            || env::var("CLAUDE_CODE_SESSION_ATTENDED").as_deref() == Ok("0"))
    {
        return Ok(());
    }
    let mut input = Vec::new();
    io::stdin().take(MAX_EVENT + 1).read_to_end(&mut input)?;
    if input.len() as u64 > MAX_EVENT {
        return Err("hook input too large".into());
    }
    let event: Value = serde_json::from_slice(&input)?;
    if agent == "codex"
        && let Some(path) = event["transcript_path"].as_str().filter(|s| !s.is_empty())
    {
        let mut line = String::new();
        BufReader::new(fs::File::open(path)?)
            .take(MAX_EVENT)
            .read_line(&mut line)?;
        let metadata: Value = serde_json::from_str(&line)?;
        if metadata["payload"]
            .get("source")
            .is_some_and(|s| s != "cli")
        {
            return Ok(());
        }
    }
    let cwd = env::var("DEVIN_PROJECT_DIR")
        .ok()
        .filter(|s| !s.is_empty())
        .unwrap_or(env::current_dir()?.to_string_lossy().into_owned());
    let pid = std::process::id();
    let mut message = request(&event, agent, state, &pane, pid, &cwd)?;
    if state == "bind" {
        message["args"]["wsl"] = json!({"Version":1,"Distribution":env::var("WSL_DISTRO_NAME")?,"Processes":ancestry(pid,&pane)?});
    }
    let envelope = json!({"version":1,"request":message});
    if args.get(2).is_some_and(|s| s == "--report") {
        println!("{envelope}");
        return Ok(());
    }
    let bridge = env::var("AGWINTERM_HOOK_EXE")
        .or_else(|_| fs::read_to_string(env::current_exe()?.with_extension("windows-bridge")))?;
    let bridge = bridge.trim_end_matches(['\r', '\n']);
    let pipe = env::var("AGWINTERM_PIPE").unwrap_or_else(|_| "agwinterm".into());
    let mut child = Command::new(bridge)
        .args(["--relay", &pipe])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .spawn()?;
    let mut stdin = child.stdin.take().ok_or("missing bridge stdin")?;
    serde_json::to_writer(&mut stdin, &envelope)?;
    drop(stdin);
    if !child.wait()?.success() {
        return Err("Windows hook bridge failed".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stat_accepts_spaces_and_parentheses_in_process_name() {
        let fields = format!("42 (a b) c) S 17 {} 12345", vec!["0"; 17].join(" "));
        assert_eq!(stat_fields(&fields).unwrap(), (17, 12345));
        assert!(stat_fields("42 broken").is_err());
    }
}
