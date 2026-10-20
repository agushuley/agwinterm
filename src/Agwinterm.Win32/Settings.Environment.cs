using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private Workspace? _settingsWorkspace;
    private string _settingsName = "", _settingsDirectory = "", _settingsProfile = "", _settingsEnvironment = "", _settingsMessage = "";
    private bool _settingsFailed;
    private IReadOnlyDictionary<string, string> _settingsOriginalEnv = EnvironmentOverrides.Empty;

    private void BeginEnvironmentEditing(Workspace? workspace)
    {
        _settingsWorkspace = workspace;
        _settingsName = workspace?.Name ?? "";
        _settingsDirectory = workspace?.Options.DefaultDir ?? "";
        _settingsProfile = workspace?.Options.DefaultProfile ?? "";
        _settingsOriginalEnv = workspace is null ? ReadGlobalEnvironment() : workspace.Options.Env ?? EnvironmentOverrides.Empty;
        _settingsEnvironment = EnvironmentOverrides.FormatEditor(_settingsOriginalEnv);
        _settingsMessage = ""; _settingsFailed = false;
    }

    private void OpenWorkspaceSettings(Workspace workspace)
    {
        if (_palette != PaletteKind.None) ClosePalette();
        BeginEnvironmentEditing(workspace);
        BuildSettingsRows();
        _setOpen = true; _setTab = 0; _setScroll = 0; _ddRow = null; _setDragRow = null;
        _setFocus = FocusableRows().FirstOrDefault();
        _setNav = -1; _setFocusKb = false;
        _uia.Announce("Workspace Settings, General tab. Changes are saved with Apply.");
        SetFocus(_hwnd); RequestRedraw();
    }

    private void BuildWorkspaceSettingsRows()
    {
        _setRows.Add(new SetRow { Kind = SW.Section, Tab = 0, Label = "Workspace" });
        _setRows.Add(new SetRow { Kind = SW.Text, Tab = 0, Label = "Name", Text = _settingsName, WriteValue = value => _settingsName = value });
        _setRows.Add(new SetRow
        {
            Kind = SW.Path, Tab = 0, Label = "Default directory", Text = _settingsDirectory,
            ReadValue = () => _settingsDirectory, WriteValue = value => _settingsDirectory = value
        });
        _setRows.Add(new SetRow
        {
            Kind = SW.Button, Tab = 0, Label = "Use application directory default",
            OnClick = () =>
            {
                var row = _setRows.First(row => row.Kind == SW.Path);
                SetSettingsText(row, ""); RequestRedraw();
            }
        });
        _setRows.Add(new SetRow
        {
            Kind = SW.Dropdown, Tab = 0, Label = "Default shell profile",
            Opts = new[] { "Use application default" }.Concat(_profileCfg.Profiles.Select(profile => profile.Name)).ToArray(),
            Vals = new[] { "" }.Concat(_profileCfg.Profiles.Select(profile => profile.Name)).ToArray(),
            ReadValue = () => _settingsProfile, WriteValue = value => _settingsProfile = value
        });
        _setRows.Add(new SetRow { Kind = SW.Info, Tab = 0, Label = "Defaults apply to new sessions. Existing processes are not changed." });
        AddEnvironmentSettingsRows(1);
    }

    private void AddEnvironmentSettingsRows(int tab)
    {
        _setRows.Add(new SetRow { Kind = SW.Section, Tab = tab, Label = "Environment variables" });
        _setRows.Add(new SetRow
        {
            Kind = SW.Info, Tab = tab,
            Label = "NAME=value or NAME<<MARKER … MARKER. Literal values: no escapes or substitutions. Stored as plaintext."
        });
        _setRows.Add(new SetRow
        {
            Kind = SW.Text, Tab = tab, Label = "Variables for new processes", Multiline = true,
            Text = _settingsEnvironment, WriteValue = value => _settingsEnvironment = value
        });
        int footerTab = _settingsWorkspace is null ? tab : -1;
        _setRows.Add(new SetRow { Kind = SW.Button, Tab = footerTab, Label = "Apply", Footer = true, OnClick = ApplyEnvironmentSettings });
        _setRows.Add(new SetRow { Kind = SW.Button, Tab = footerTab, Label = "Cancel", Footer = true, OnClick = CloseSettings });
    }

    private void ApplyEnvironmentSettings()
    {
        try
        {
            var environment = EnvironmentOverrides.ParseEditor(_settingsEnvironment, _settingsOriginalEnv);
            if (_settingsWorkspace is null)
            {
                EnvironmentOverrides.Save(GlobalEnvironmentPath, environment);
                _globalEnv = environment;
            }
            else
            {
                var workspace = _settingsWorkspace;
                if (!_workspaces.Contains(workspace)) throw new FormatException("Workspace no longer exists.");
                if (_settingsName.Length == 0) throw new FormatException("Workspace name cannot be empty.");
                var options = workspace.Options with
                {
                    DefaultDir = _settingsDirectory.Length == 0 ? null : Path.GetFullPath(_settingsDirectory),
                    DefaultProfile = _settingsProfile.Length == 0 ? null : _settingsProfile,
                    Env = environment.Count == 0 ? null : environment
                };
                if (ValidateWorkspaceOptions(options) is { } error) throw new FormatException(error);
                workspace.Name = _settingsName;
                workspace.Options = SnapshotWorkspaceOptions(options);
                SaveState(); EmitEvent("tree");
            }
            _settingsOriginalEnv = environment;
            _settingsMessage = "Saved. Applies to new processes only."; _settingsFailed = false;
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _settingsMessage = error is FormatException ? error.Message : "Could not save settings. Check the directory and file permissions.";
            _settingsFailed = true;
        }
        _uia.Announce(_settingsMessage); RequestRedraw();
    }

    private string SettingsRowValue(SetRow row) => row.ReadValue?.Invoke() ?? ConfigValue(row.Key);

    private float SettingsRowHeight(SetRow row) => row.Kind switch
    {
        SW.Section => SetSecH,
        SW.Diag => 150f,
        SW.Profile => 46f,
        SW.Text when row.Multiline => MathF.Max(100f, _setPaneBottom - _setPaneTop - 112f),
        SW.Text => 68f,
        SW.Path when row.WriteValue is not null => 68f,
        SW.Info when row.Tab == (_settingsWorkspace is null ? SetTabNames.Length - 1 : 1) => 64f,
        _ => SetRowH
    };

    private void BrowseSettingsDirectory(SetRow row)
    {
        SetFocus(_hwnd);
        string? directory = PickFolder(SettingsRowValue(row));
        if (directory is null) return;
        if (row.WriteValue is not null) SetSettingsText(row, directory);
        else { ConfigSetInternal("new-session-dir", directory); ConfigSetInternal("new-session-dir-mode", "custom"); }
        RequestRedraw();
    }
}
