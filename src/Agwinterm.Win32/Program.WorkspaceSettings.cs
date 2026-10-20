using System.Text.Json;
using Agwinterm.Pty;

namespace Agwinterm.Win32;

internal partial class Program
{
    private static IReadOnlyDictionary<string, string> _globalEnv = EnvironmentOverrides.Empty;
    private static string GlobalEnvironmentPath => Path.Combine(AppDir, "environment.json");
    private EnvironmentSettingsDialog? _environmentDialog;

    private IReadOnlyDictionary<string, string> ReadGlobalEnvironment()
    {
        try { _globalEnv = EnvironmentOverrides.Load(GlobalEnvironmentPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException or KeyNotFoundException)
        {
            ShowToast("Cannot load environment.json; using the last valid global overrides. Check its JSON and environment names.", 6000);
        }
        return _globalEnv;
    }

    private static WorkspaceOptions SnapshotWorkspaceOptions(WorkspaceOptions options)
        => options with { Env = options.Env is null ? null : EnvironmentOverrides.Snapshot(options.Env) };

    private string? ValidateWorkspaceOptions(WorkspaceOptions options)
    {
        if (options.DefaultDir is not null && WorkspaceDefaultDirs.ValidateSet(options.DefaultDir) is { } error) return error;
        if (options.DefaultProfile is not null && !_profileCfg.Profiles.Any(profile => profile.Name.Equals(options.DefaultProfile, StringComparison.OrdinalIgnoreCase)))
            return "Workspace default profile was not found; nothing changed.";
        return null;
    }

    public string WorkspaceSettings(string? target, WorkspaceOptions? options) => InvokeOnUiQueued(() =>
    {
        var workspace = string.IsNullOrEmpty(target) || target == "active" ? ActiveWorkspace() : FindWs(target);
        if (workspace is null) return ISessionHost.RefusePrefix + WorkspaceDefaultDirs.NoWorkspace;
        if (options is not null)
        {
            if (ValidateWorkspaceOptions(options) is { } error) return ISessionHost.RefusePrefix + error;
            workspace.Options = SnapshotWorkspaceOptions(options);
            SaveState(); EmitEvent("tree"); RequestRedraw();
        }
        return JsonSerializer.Serialize(new { workspace = workspace.Id, settings = new
        {
            defaultDir = workspace.Options.DefaultDir,
            defaultProfile = workspace.Options.DefaultProfile,
            env = workspace.Options.Env ?? EnvironmentOverrides.Empty
        } });
    });

    public string EnvironmentGet(string scope, string? target) => InvokeOnUiQueued(() =>
    {
        IReadOnlyDictionary<string, string>? env;
        switch (scope)
        {
            case "global": env = EnvironmentOverrides.Load(GlobalEnvironmentPath); break;
            case "profile":
                var profile = _profileCfg.Profiles.FirstOrDefault(profile => profile.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (profile is null) return ISessionHost.RefusePrefix + "profile not found";
                env = profile.Env; break;
            case "workspace":
                var workspace = string.IsNullOrEmpty(target) || target == "active" ? ActiveWorkspace() : FindWs(target);
                if (workspace is null) return ISessionHost.RefusePrefix + WorkspaceDefaultDirs.NoWorkspace;
                env = workspace.Options.Env; break;
            case "session":
                var session = Find(target);
                if (session is null) return ISessionHost.RefusePrefix + "session not found";
                env = session.Env; break;
            default: return ISessionHost.RefusePrefix + "scope must be global, profile, workspace or session";
        }
        return JsonSerializer.Serialize(new { scope, env = env ?? EnvironmentOverrides.Empty });
    });

    public string EnvironmentSet(string scope, string? target, IReadOnlyDictionary<string, string> env) => InvokeOnUiQueued(() =>
    {
        var snapshot = EnvironmentOverrides.Snapshot(env);
        switch (scope)
        {
            case "global": EnvironmentOverrides.Save(GlobalEnvironmentPath, snapshot); _globalEnv = snapshot; break;
            case "profile":
                var profile = _profileCfg.Profiles.FirstOrDefault(profile => profile.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (profile is null) return ISessionHost.RefusePrefix + "profile not found";
                var config = ShellProfiles.Load(AppDir);
                var stored = config.Profiles.FirstOrDefault(profile => profile.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                if (stored is null) return ISessionHost.RefusePrefix + "profile not found";
                stored.Env = new Dictionary<string, string>(snapshot, StringComparer.OrdinalIgnoreCase);
                ShellProfiles.SaveChecked(AppDir, config);
                _profileCfg = config;
                break;
            case "workspace":
                var workspace = string.IsNullOrEmpty(target) || target == "active" ? ActiveWorkspace() : FindWs(target);
                if (workspace is null) return ISessionHost.RefusePrefix + WorkspaceDefaultDirs.NoWorkspace;
                workspace.Options = workspace.Options with { Env = snapshot.Count == 0 ? null : snapshot };
                SaveState(); break;
            case "session":
                var session = Find(target);
                if (session is null) return ISessionHost.RefusePrefix + "session not found";
                session.Env = snapshot; SaveState(); break;
            default: return ISessionHost.RefusePrefix + "scope must be global, profile, workspace or session";
        }
        return JsonSerializer.Serialize(new { scope, env = snapshot });
    });

    private void OpenEnvironmentSettings(Workspace? workspace = null)
    {
        if (_environmentDialog is not null) { _environmentDialog.Activate(); return; }
        if (_setOpen) CloseSettings();
        var original = workspace?.Options.Env ?? (workspace is null ? ReadGlobalEnvironment() : EnvironmentOverrides.Empty);
        _environmentDialog = new EnvironmentSettingsDialog(_hwnd, workspace?.Name,
            workspace?.Options ?? new(), original, _profileCfg.Profiles.Select(profile => profile.Name).ToArray(),
            (name, options, environment) =>
            {
                if (workspace is null)
                {
                    EnvironmentOverrides.Save(GlobalEnvironmentPath, environment); _globalEnv = environment;
                }
                else
                {
                    if (!_workspaces.Contains(workspace)) return "Workspace no longer exists.";
                    if (name.Length == 0) return "Workspace name cannot be empty.";
                    if (ValidateWorkspaceOptions(options) is { } error) return error;
                    workspace.Name = name; workspace.Options = SnapshotWorkspaceOptions(options with { Env = environment.Count == 0 ? null : environment });
                    SaveState(); EmitEvent("tree"); RequestRedraw();
                }
                return null;
            }, () => { _environmentDialog = null; });
        _environmentDialog.Show();
    }
}
