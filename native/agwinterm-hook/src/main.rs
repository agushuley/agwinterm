//! Native WSL hook and Windows pipe receiver. Not installed automatically.
//! JSON stays on stdin; Linux routing/context is passed explicitly by the wrapper.
use serde_json::{Value, json};
use std::{
    env,
    io::{self, BufRead, BufReader, Read},
    time::Duration,
};

const MAX_EVENT: u64 = 4 * 1024 * 1024;

#[cfg(windows)]
use std::io::Write;

#[cfg(target_os = "linux")]
mod install;
#[cfg(target_os = "linux")]
mod linux;

#[cfg(any(windows, test))]
fn transcript_path(distro: &str, path: &str) -> Result<String, String> {
    if distro.is_empty()
        || distro.contains(['/', '\\'])
        || !path.starts_with('/')
        || path.contains('\\')
    {
        return Err("expected WSL distribution and absolute Linux transcript path".into());
    }
    Ok(format!(
        r"\\wsl.localhost\{}{}",
        distro,
        path.replace('/', "\\")
    ))
}

fn request(
    event: &Value,
    agent: &str,
    state: &str,
    target: &str,
    pid: u32,
    cwd: &str,
) -> Result<Value, String> {
    if !matches!(agent, "claude" | "codex" | "devin") {
        return Err("unknown agent".into());
    }
    let args = if state == "bind" {
        let resume = event["session_id"]
            .as_str()
            .filter(|s| !s.is_empty())
            .ok_or("missing session_id")?;
        if pid == 0 {
            return Err("missing Linux hook PID".into());
        }
        json!({"agent":agent,"resume":resume,"pid":pid,"cwd":event["cwd"].as_str().filter(|s| !s.is_empty()).unwrap_or(cwd)})
    } else {
        let status = match state {
            "stop" => {
                if event["last_assistant_message"]
                    .as_str()
                    .unwrap_or("")
                    .trim_end()
                    .ends_with('?')
                {
                    "blocked"
                } else {
                    "completed"
                }
            }
            "active" | "idle" | "blocked" | "completed" => state,
            _ => return Err("unknown status".into()),
        };
        json!({"status":status})
    };
    Ok(
        json!({"cmd":if state=="bind" {"session.bind"} else {"session.status"},"target":target,"args":args}),
    )
}

#[cfg(windows)]
fn run() -> Result<(), Box<dyn std::error::Error>> {
    // agent state target pipe Linux-PID Linux-cwd [WSL distribution]
    let args: Vec<String> = env::args().skip(1).collect();
    if args.first().is_some_and(|a| a == "--relay") {
        let pipe = args.get(1).ok_or("missing pipe")?;
        let mut input = Vec::new();
        io::stdin().take(MAX_EVENT + 1).read_to_end(&mut input)?;
        if input.len() as u64 > MAX_EVENT {
            return Err("request too large".into());
        }
        let envelope: Value = serde_json::from_slice(&input)?;
        if envelope["version"] != 1
            || !matches!(
                envelope["request"]["cmd"].as_str(),
                Some("session.bind" | "session.status")
            )
        {
            return Err("unsupported hook protocol".into());
        }
        return exchange(pipe, &envelope["request"]);
    }
    if args.len() < 6 {
        return Err("expected agent state target pipe linux-pid linux-cwd [distribution]".into());
    }
    if args[2].is_empty() {
        return Ok(());
    }
    if args[3].is_empty() || args[3].contains(['/', '\\']) {
        return Err("invalid pipe name".into());
    }
    let mut input = Vec::new();
    io::stdin().take(MAX_EVENT + 1).read_to_end(&mut input)?;
    if input.len() as u64 > MAX_EVENT {
        return Err("hook input too large".into());
    }
    let event: Value = serde_json::from_slice(&input)?;
    if args[0] == "codex"
        && event["transcript_path"]
            .as_str()
            .is_some_and(|s| !s.is_empty())
    {
        let path = transcript_path(
            args.get(6).ok_or("missing WSL distribution")?,
            event["transcript_path"].as_str().unwrap(),
        )?;
        let mut line = String::new();
        BufReader::new(std::fs::File::open(path)?)
            .take(MAX_EVENT)
            .read_line(&mut line)?;
        let metadata: Value = serde_json::from_str(&line)?;
        if metadata["payload"]
            .get("source")
            .is_some_and(|source| source != "cli")
        {
            return Ok(());
        }
    }
    let message = request(
        &event,
        &args[0],
        &args[1],
        &args[2],
        args[4].parse()?,
        &args[5],
    )?;
    exchange(&args[3], &message)
}

#[cfg(windows)]
fn exchange(name: &str, message: &Value) -> Result<(), Box<dyn std::error::Error>> {
    if name.is_empty() || name.contains(['/', '\\']) {
        return Err("invalid pipe name".into());
    }
    let mut pipe = std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(format!(r"\\.\pipe\{}", name))?;
    serde_json::to_writer(&mut pipe, &message)?;
    pipe.write_all(b"\n")?;
    pipe.flush()?;
    let mut response = String::new();
    BufReader::new(pipe)
        .take(MAX_EVENT)
        .read_line(&mut response)?;
    let reply: Value = serde_json::from_str(&response)?;
    if reply["ok"] != true {
        return Err(response.into());
    }
    Ok(())
}

fn main() {
    // A hard process deadline also bounds a blocked stdin/pipe write/read. Windows
    // closes all process handles on exit. Missing pipes fail immediately.
    let operation = env::args().nth(1).unwrap_or_default();
    let administrative = operation.starts_with("--");
    let deadline = if operation == "--install" {
        25
    } else if cfg!(windows) {
        2
    } else {
        3
    };
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_secs(deadline));
        std::process::exit(if administrative { 1 } else { 0 });
    });
    #[cfg(windows)]
    let result = run();
    #[cfg(target_os = "linux")]
    let result = linux::run();
    if let Err(error) = result {
        if administrative || env::var_os("AGWINTERM_HOOK_DEBUG").is_some() {
            eprintln!("agwinterm-hook: {error}");
        }
        if administrative {
            std::process::exit(1);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stop_and_unicode() {
        assert_eq!(
            request(
                &json!({"last_assistant_message":"Готово? \n"}),
                "codex",
                "stop",
                "pane",
                1,
                "/"
            )
            .unwrap()["args"]["status"],
            "blocked"
        );
        assert_eq!(
            request(&json!({}), "claude", "stop", "pane", 1, "/").unwrap()["args"]["status"],
            "completed"
        );
    }
    #[test]
    fn binding_preserves_json_strings() {
        let value = request(
            &json!({"session_id":"id\"\\","cwd":"/tmp/путь с пробелом"}),
            "codex",
            "bind",
            "pane",
            123,
            "/",
        )
        .unwrap();
        assert_eq!(value["args"]["pid"], 123);
        assert_eq!(value["args"]["cwd"], "/tmp/путь с пробелом");
        assert_eq!(value["args"]["resume"], "id\"\\");
    }
    #[test]
    fn rejects_incomplete_binding_and_unknown_actions() {
        assert!(request(&json!({}), "codex", "bind", "pane", 1, "/").is_err());
        assert!(request(&json!({}), "codex", "bogus", "pane", 1, "/").is_err());
        assert!(request(&json!({}), "bogus", "active", "pane", 1, "/").is_err());
    }
    #[test]
    fn linux_transcript_uses_the_selected_distribution() {
        assert_eq!(
            transcript_path("Ubuntu", "/home/a/path with spaces/log.jsonl").unwrap(),
            r"\\wsl.localhost\Ubuntu\home\a\path with spaces\log.jsonl"
        );
        assert!(transcript_path("", "/tmp/log").is_err());
        assert!(transcript_path("Ubuntu", "relative").is_err());
    }
}
