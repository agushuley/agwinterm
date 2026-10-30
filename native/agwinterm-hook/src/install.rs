//! WSL integration installation. JSON is handled here, never by the bootstrap shell.
use serde_json::{Value, json};
use std::{
    env, fs,
    io::{self, Read, Write},
    os::unix::fs::{OpenOptionsExt, PermissionsExt},
    path::{Path, PathBuf},
    process::Command,
    sync::atomic::{AtomicU64, Ordering},
};

type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
static TEMP_ID: AtomicU64 = AtomicU64::new(0);

pub(crate) fn quote(value: &str) -> String {
    // Match shlex.quote for migration of previously generated Python hook commands.
    if !value.is_empty()
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"_@%+=:,./-".contains(&b))
    {
        value.to_owned()
    } else {
        format!("'{}'", value.replace('\'', "'\"'\"'"))
    }
}

fn read_optional(path: &Path) -> Result<Option<String>> {
    match fs::read_to_string(path) {
        Ok(s) => Ok(Some(s)),
        Err(e) if e.kind() == io::ErrorKind::NotFound && fs::symlink_metadata(path).is_err() => {
            Ok(None)
        }
        Err(e) => Err(e.into()),
    }
}

fn write(path: &Path, bytes: &[u8], executable: bool) -> Result<()> {
    fs::create_dir_all(path.parent().ok_or("missing parent")?)?;
    // Preserve symlinked dotfiles by updating their referent, not replacing the link.
    let destination = match fs::symlink_metadata(path) {
        Ok(m) if m.file_type().is_symlink() => fs::canonicalize(path)?,
        _ => path.to_path_buf(),
    };
    let mode = if executable {
        0o700
    } else {
        fs::metadata(&destination)
            .map(|m| m.permissions().mode() & 0o777)
            .unwrap_or(0o600)
    };
    let parent = destination.parent().ok_or("missing destination parent")?;
    let temporary = parent.join(format!(
        ".agwinterm-{}-{}",
        std::process::id(),
        TEMP_ID.fetch_add(1, Ordering::Relaxed)
    ));
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o600)
        .open(&temporary)?;
    let result = (|| -> Result<()> {
        file.write_all(bytes)?;
        file.set_permissions(fs::Permissions::from_mode(mode))?;
        file.sync_all()?;
        fs::rename(&temporary, &destination)?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

fn backup(path: &Path, original: &str) -> Result<()> {
    let name = path
        .file_name()
        .ok_or("missing filename")?
        .to_string_lossy();
    for number in 1..10000 {
        let suffix = if number == 1 {
            ".agwinterm.bak".to_owned()
        } else {
            format!(".agwinterm-{number}.bak")
        };
        match fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .mode(0o600)
            .open(path.with_file_name(format!("{name}{suffix}")))
        {
            Ok(mut file) => {
                file.write_all(original.as_bytes())?;
                file.sync_all()?;
                return Ok(());
            }
            Err(e) if e.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(e.into()),
        }
    }
    Err("too many backup files".into())
}

fn block(path: &Path, name: &str, text: &str) -> Result<()> {
    let begin = format!("# >>> agwinterm {name} >>>");
    let end = format!("# <<< agwinterm {name} <<<");
    let original = read_optional(path)?.unwrap_or_default();
    let mut old = original.clone();
    if let Some(start) = old.find(&begin) {
        let finish = old[start..]
            .find(&end)
            .ok_or("incomplete agwinterm profile block; file untouched")?
            + start
            + end.len();
        old = format!(
            "{}{}",
            &old[..start],
            old[finish..].trim_start_matches('\n')
        );
    }
    let new = format!(
        "{}\n{}",
        old.trim_end_matches('\n'),
        if text.is_empty() {
            String::new()
        } else {
            format!("{begin}\n{text}\n{end}\n")
        }
    );
    if new != original {
        write(path, new.as_bytes(), false)?;
    }
    Ok(())
}

fn string_end(bytes: &[u8], start: usize) -> Result<usize> {
    let mut i = start + 1;
    while i < bytes.len() {
        match bytes[i] {
            b'\\' => i += 2,
            b'"' => return Ok(i + 1),
            _ => i += 1,
        }
    }
    Err("unterminated JSON string".into())
}

fn parse_config(text: &str, jsonc: bool) -> Result<Value> {
    if !jsonc {
        return Ok(serde_json::from_str(text)?);
    }
    let mut bytes = text.as_bytes().to_vec();
    let mut i = 0;
    // Blank comments so tokens do not concatenate, preserving UTF-8 inside strings.
    while i < bytes.len() {
        if bytes[i] == b'"' {
            i = string_end(&bytes, i)?;
        } else if bytes[i..].starts_with(b"//") {
            while i < bytes.len() && bytes[i] != b'\n' {
                bytes[i] = b' ';
                i += 1;
            }
        } else if bytes[i..].starts_with(b"/*") {
            let end = bytes[i + 2..]
                .windows(2)
                .position(|s| s == b"*/")
                .ok_or("unclosed JSONC comment")?
                + i
                + 4;
            bytes[i..end].fill(b' ');
            i = end;
        } else {
            i += 1;
        }
    }
    i = 0;
    while i < bytes.len() {
        if bytes[i] == b'"' {
            i = string_end(&bytes, i)?;
            continue;
        }
        if bytes[i] == b','
            && bytes[i + 1..]
                .iter()
                .find(|b| !b.is_ascii_whitespace())
                .is_some_and(|b| matches!(b, b'}' | b']'))
        {
            bytes[i] = b' ';
        }
        i += 1;
    }
    Ok(serde_json::from_slice(&bytes)?)
}

fn merge_hooks(config: &mut Value, agent: &str, data: &Path) -> Result<()> {
    let object = config.as_object_mut().ok_or("expected config object")?;
    let hooks = object
        .entry("hooks")
        .or_insert_with(|| json!({}))
        .as_object_mut()
        .ok_or("expected hooks object")?;
    let mut events = vec![
        ("UserPromptSubmit", "active"),
        ("PostToolUse", "active"),
        ("Stop", "stop"),
        ("PermissionRequest", "blocked"),
        ("SessionStart", "bind"),
    ];
    if agent == "claude" {
        events.push(("Notification", "blocked"));
    }
    for (event, state) in events {
        let previous = format!(
            "python3 {} {agent} {state}",
            quote(&data.join("hook.py").to_string_lossy())
        );
        let command = format!(
            "{} {agent} {state}",
            quote(&data.join("agwinterm-hook").to_string_lossy())
        );
        let entries = hooks
            .entry(event)
            .or_insert_with(|| json!([]))
            .as_array_mut()
            .ok_or("expected hook array")?;
        let mut found = false;
        for entry in entries.iter_mut() {
            if let Some(installed) = entry.get_mut("hooks").and_then(Value::as_array_mut) {
                for hook in installed {
                    if hook["command"] == previous || hook["command"] == command {
                        hook["command"] = json!(command);
                        found = true;
                    }
                }
            }
        }
        if !found {
            let mut entry = json!({"hooks":[{"type":"command","command":command}]});
            if event == "Notification" {
                entry["matcher"] = json!("permission_prompt");
            }
            entries.push(entry);
        }
    }
    Ok(())
}

fn config_file(path: &Path, agent: &str, data: &Path) -> Result<()> {
    let old = read_optional(path)?;
    let mut config = match &old {
        Some(s) => parse_config(s, agent == "devin")?,
        None => json!({}),
    };
    merge_hooks(&mut config, agent, data)?;
    let merged = serde_json::to_string_pretty(&config)? + "\n";
    if old.as_deref() == Some(&merged) {
        return Ok(());
    }
    if let Some(old) = old {
        backup(path, &old)?;
    }
    write(path, merged.as_bytes(), false)
}

fn linux_path(windows: &str) -> Result<PathBuf> {
    let output = Command::new("wslpath").args(["-u", windows]).output()?;
    if !output.status.success() {
        return Err(String::from_utf8_lossy(&output.stderr).into_owned().into());
    }
    Ok(PathBuf::from(
        String::from_utf8(output.stdout)?.trim_end_matches('\n'),
    ))
}

fn env_path(name: &str, default: PathBuf) -> PathBuf {
    env::var_os(name)
        .filter(|s| !s.is_empty())
        .map(PathBuf::from)
        .unwrap_or(default)
}

pub(super) fn run() -> Result<()> {
    let mut input = Vec::new();
    io::stdin()
        .take(super::MAX_EVENT + 1)
        .read_to_end(&mut input)?;
    if input.len() as u64 > super::MAX_EVENT {
        return Err("installer payload too large".into());
    }
    let payload: Value = serde_json::from_slice(&input)?;
    let field = |name: &str| -> Result<&str> {
        payload[name]
            .as_str()
            .ok_or_else(|| format!("missing installer field {name}").into())
    };
    if payload["version"] != 1 {
        return Err("unsupported installer protocol".into());
    }
    let kind = field("kind")?;
    let action = field("action")?;
    if !matches!(
        (kind, action),
        ("cli", "install" | "remove") | ("hooks" | "skill" | "shell", "install")
    ) {
        return Err("unsupported installation action".into());
    }
    let home = PathBuf::from(env::var_os("HOME").ok_or("HOME is missing")?);
    if !home.is_absolute() {
        return Err("HOME must be an absolute path".into());
    }
    let data = home.join(".local/share/agwinterm");
    let bin = home.join(".local/bin");
    let zsh = env_path("ZDOTDIR", home.clone()).join(".zshrc");
    let claude = env_path("CLAUDE_CONFIG_DIR", home.join(".claude"));
    let codex = env_path("CODEX_HOME", home.join(".codex"));
    let config = env_path("XDG_CONFIG_HOME", home.join(".config"));
    // Validate binaries before modifying any profile or config.
    let cli = if matches!(kind, "cli" | "hooks") && action == "install" {
        let path = linux_path(field("windowsCli")?)?;
        if !path.is_file() {
            return Err("agwintermctl.exe is missing".into());
        }
        Some(path)
    } else {
        None
    };
    let bridge = if kind == "hooks" {
        let path = linux_path(field("windowsBridge")?)?;
        if !path.is_file() {
            return Err("agwinterm-hook.exe is missing".into());
        }
        Some(path)
    } else {
        None
    };
    if let Some(cli) = cli {
        write(
            &bin.join("agwintermctl"),
            format!("#!/bin/sh\nexec {} \"$@\"\n", quote(&cli.to_string_lossy())).as_bytes(),
            true,
        )?;
        for rc in [home.join(".profile"), home.join(".bashrc"), zsh.clone()] {
            block(&rc, "cli", "export PATH=\"$HOME/.local/bin:$PATH\"")?;
        }
    }
    match kind {
        "cli" if action == "remove" => {
            let wrapper = bin.join("agwintermctl");
            if let Some(text) = read_optional(&wrapper)? {
                if !text.starts_with("#!/bin/sh\nexec ")
                    || !text.contains("agwintermctl.exe")
                    || !text.ends_with(" \"$@\"\n")
                {
                    return Err("refusing to remove an unrelated agwintermctl".into());
                }
                fs::remove_file(wrapper)?;
            }
            for rc in [home.join(".profile"), home.join(".bashrc"), zsh] {
                if rc.exists() {
                    block(&rc, "cli", "")?;
                }
            }
        }
        "cli" => {}
        "shell" => {
            write(&data.join("shell.sh"), field("shell")?.as_bytes(), false)?;
            for rc in [home.join(".bashrc"), zsh] {
                block(
                    &rc,
                    "shell",
                    "[ -r \"$HOME/.local/share/agwinterm/shell.sh\" ] && . \"$HOME/.local/share/agwinterm/shell.sh\"",
                )?;
            }
        }
        "skill" => {
            let text = format!(
                "{}\n\n## WSL\nUse agwintermctl from ~/.local/bin. Local file arguments consumed by the Windows CLI need `wslpath -w`. Linux processes use Linux PIDs. Install integrations from the WSL pane.\n",
                field("skill")?
            );
            for base in [claude, codex] {
                write(
                    &base.join("skills/agwinterm/SKILL.md"),
                    text.as_bytes(),
                    false,
                )?;
            }
        }
        "hooks" => {
            write(
                &data.join("agwinterm-hook"),
                &fs::read(env::current_exe()?)?,
                true,
            )?;
            write(
                &data.join("agwinterm-hook.windows-bridge"),
                format!("{}\n", bridge.unwrap().display()).as_bytes(),
                false,
            )?;
            println!("Hook runtime: native Rust");
            for (agent, path) in [
                ("claude", claude.join("settings.json")),
                ("codex", codex.join("hooks.json")),
                ("devin", config.join("devin/config.json")),
            ] {
                match config_file(&path, agent, &data) {
                    Ok(()) => println!("{agent}: {}", path.display()),
                    Err(error) => println!("{agent}: refused; existing config untouched: {error}"),
                }
            }
            println!("Codex: approve the installed hooks in /hooks.");
        }
        _ => unreachable!(),
    }
    println!(
        "WSL {}: {kind} {action} complete; restart the shell.",
        env::var("WSL_DISTRO_NAME").unwrap_or_default()
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn jsonc_keeps_strings_and_rejects_broken_comments() {
        let value = parse_config(
            "{// comment\n\"url\":\"https://x/a,}\",/*✓*/\"a\":[1,],}",
            true,
        )
        .unwrap();
        assert_eq!(value["url"], "https://x/a,}");
        assert_eq!(value["a"], json!([1]));
        assert!(parse_config("{/* broken", true).is_err());
        assert!(parse_config("{\"a\":1,,}", true).is_err());
    }
    #[test]
    fn migrates_only_our_commands_and_keeps_options() {
        let data = Path::new("/home/a b/.local/share/agwinterm");
        let previous = format!(
            "python3 {} codex stop",
            quote(&data.join("hook.py").to_string_lossy())
        );
        let mut config = json!({"custom":true,"hooks":{"Stop":[{"matcher":"mine","hooks":[{"command":previous,"timeout":7},{"command":"echo mine"}]}]}});
        merge_hooks(&mut config, "codex", data).unwrap();
        let once = config.clone();
        merge_hooks(&mut config, "codex", data).unwrap();
        assert_eq!(config, once);
        assert_eq!(config["hooks"]["Stop"][0]["hooks"][0]["timeout"], 7);
        assert_eq!(
            config["hooks"]["Stop"][0]["hooks"][1]["command"],
            "echo mine"
        );
        assert!(config["custom"].as_bool().unwrap());
    }
}
