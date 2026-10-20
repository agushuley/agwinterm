Add-Type -Path "$PSHOME/UIAutomationTypes.dll"
Add-Type -Path "$PSHOME/UIAutomationClient.dll"
Add-Type -AssemblyName System.Drawing.Common
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WorkspaceSettingsProbe {
    private delegate bool Visitor(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, Visitor visitor, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr key, IntPtr detail);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr SendText(IntPtr window, uint message, IntPtr key, string text);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] private static extern IntPtr ReadText(IntPtr window, uint message, IntPtr key, StringBuilder text);
    private static string ClassName(IntPtr window) { var name = new StringBuilder(256); GetClassNameW(window, name, name.Capacity); return name.ToString(); }
    public static IntPtr Window(uint process, string className) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, data) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process && ClassName(window) == className && IsWindowVisible(window)) { result = window; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static IntPtr[] Edits(IntPtr parent) {
        var controls = new List<IntPtr>();
        EnumChildWindows(parent, (window, data) => { if (ClassName(window) == "Edit" && IsWindowVisible(window)) controls.Add(window); return true; }, IntPtr.Zero);
        controls.Sort((first, second) => GetDlgCtrlID(first).CompareTo(GetDlgCtrlID(second)));
        return controls.ToArray();
    }
    public static IntPtr Child(IntPtr parent, string className) {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(parent, (window, data) => { if (ClassName(window) == className && IsWindowVisible(window)) { result = window; return false; } return true; }, IntPtr.Zero);
        return result;
    }
    public static string Text(IntPtr window) {
        int length = (int)SendMessageW(window, 14, IntPtr.Zero, IntPtr.Zero);
        var text = new StringBuilder(length + 1); ReadText(window, 13, (IntPtr)text.Capacity, text); return text.ToString();
    }
    public static void SetText(IntPtr window, string text) { SendText(window, 12, IntPtr.Zero, text); }
    public static void Key(IntPtr window, int key) { SendMessageW(window, 0x100, (IntPtr)key, (IntPtr)1); }
    public static void WorkspaceMenu(IntPtr window) {
        double scale = GetDpiForWindow(window) / 96.0;
        var point = new Point { X = (int)(10 * scale), Y = (int)(55 * scale) }; ClientToScreen(window, ref point);
        SendMessageW(window, 0x7b, window, (IntPtr)((point.Y << 16) | (point.X & 0xffff)));
    }
}
'@

function Wait-SettingsUi([scriptblock]$Condition) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Settings-Node([string]$Name) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($settingsOwner)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-SettingsNode([string]$Name) {
    if (-not (Wait-SettingsUi { $null -ne (Settings-Node $Name) })) { throw "Settings UI node missing: $Name" }
    $node = Settings-Node $Name
    ($node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Settings-Shot([string]$Name, [IntPtr]$Handle = $settingsOwner) {
    $rect = [WorkspaceSettingsProbe+Rect]::new()
    $null = [WorkspaceSettingsProbe]::GetWindowRect($Handle, [ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $context = $graphics.GetHdc()
    try { $null = [WorkspaceSettingsProbe]::PrintWindow($Handle, $context, 2) }
    finally { $graphics.ReleaseHdc($context); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $OutputDirectory "$Name.png"), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

$settingsOwner = [IntPtr]::Zero
if (-not (Wait-SettingsUi {
    $script:settingsOwner = [WorkspaceSettingsProbe]::Window([uint32]$process.Id, 'AgwintermWin32')
    $settingsOwner -ne [IntPtr]::Zero
})) { throw 'Owned settings test window did not become visible.' }
$initialTree = Invoke-Api 'tree'
$uiWorkspace = [string]$initialTree.result.workspaces[0].id
$initialName = [string]$initialTree.result.workspaces[0].name
$initialSettings = (Invoke-Api 'workspace.settings' @{} $uiWorkspace).result.settings
$initialEnvironment = (Invoke-Api 'environment.get').result.env
$settingsUiPassed = $false
try {
    $null = Invoke-Api 'settings.open'
    Invoke-SettingsNode 'Environment tab'
    Check 'global Environment tab hosts one inline editor' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 1 })
    Check 'environment editing does not open a separate window' ([WorkspaceSettingsProbe]::Window([uint32]$process.Id, 'Agwinterm.EnvironmentSettings') -eq [IntPtr]::Zero)
    $environmentEdit = @([WorkspaceSettingsProbe]::Edits($settingsOwner))[0]
    [WorkspaceSettingsProbe]::SetText($environmentEdit, "TOKEN<<END`r`nabc=def`r`nbcd=`r`nqw=`r`nEND`r`nROOT=C:\new\tools`r`n")
    Settings-Shot 'themed-environment'
    Invoke-SettingsNode 'General tab'
    Check 'environment editor hides when switching tabs' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 0 })
    Invoke-SettingsNode 'Environment tab'
    Check 'environment draft survives tab switching' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 1 -and [WorkspaceSettingsProbe]::Text(@([WorkspaceSettingsProbe]::Edits($settingsOwner))[0]).Contains('qw=') })
    Invoke-SettingsNode 'Apply'
    Check 'inline global editor saves literal multiline values' (Wait-SettingsUi { (Invoke-Api 'environment.get').result.env.TOKEN -ceq "abc=def`nbcd=`nqw=" })
    [WorkspaceSettingsProbe]::SetText(@([WorkspaceSettingsProbe]::Edits($settingsOwner))[0], "TOKEN=first`r`ntoken=duplicate")
    Invoke-SettingsNode 'Apply'
    Check 'invalid inline text does not overwrite global values' ((Invoke-Api 'environment.get').result.env.TOKEN -ceq "abc=def`nbcd=`nqw=")
    Invoke-SettingsNode 'Cancel'
    Check 'Cancel destroys the inline edit controls' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 0 })

    $null = Invoke-Api 'settings.open'
    Invoke-SettingsNode 'Environment tab'
    Check 'reopening the tab regenerates heredoc' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 1 -and [WorkspaceSettingsProbe]::Text(@([WorkspaceSettingsProbe]::Edits($settingsOwner))[0]).Contains('TOKEN<<END_ENV') })
    [WorkspaceSettingsProbe]::Key(@([WorkspaceSettingsProbe]::Edits($settingsOwner))[0], 27)
    Check 'Escape from inline editor closes Settings' (Wait-SettingsUi {
        $null -eq (Settings-Node 'Environment tab, selected') -and @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 0
    })

    [WorkspaceSettingsProbe]::WorkspaceMenu($settingsOwner)
    [WorkspaceSettingsProbe]::Key($settingsOwner, 36)
    [WorkspaceSettingsProbe]::Key($settingsOwner, 13)
    Check 'workspace menu opens the themed Workspace Settings panel' (Wait-SettingsUi { $null -ne (Settings-Node 'Workspace Settings') })
    Check 'workspace General tab has name and directory text fields' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 2 })
    $generalEdits = @([WorkspaceSettingsProbe]::Edits($settingsOwner))
    [WorkspaceSettingsProbe]::SetText($generalEdits[0], 'Themed workspace')
    $pickerDirectory = Join-Path $appDirectory 'browse-start'
    $chosenDirectory = Join-Path $pickerDirectory 'chosen-folder'
    New-Item -ItemType Directory -Path $chosenDirectory -Force | Out-Null
    [WorkspaceSettingsProbe]::SetText($generalEdits[1], $pickerDirectory)
    Settings-Shot 'themed-workspace-general'
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($settingsOwner)
    $folderRow = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -like 'Default directory,*' })[0]
    ($folderRow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $folderDialog = [IntPtr]::Zero
    Check 'directory Browse opens an owned folder picker' (Wait-SettingsUi {
        $script:folderDialog = [WorkspaceSettingsProbe]::Window([uint32]$process.Id, '#32770')
        $folderDialog -ne [IntPtr]::Zero
    })
    Settings-Shot 'directory-picker' $folderDialog
    $null = [WorkspaceSettingsProbe]::SendMessageW($folderDialog, 0x111, [IntPtr]1, [IntPtr]::Zero)
    Check 'folder picker starts at the workspace directory draft' (Wait-SettingsUi {
        [WorkspaceSettingsProbe]::Window([uint32]$process.Id, '#32770') -eq [IntPtr]::Zero -and
        [WorkspaceSettingsProbe]::Text($generalEdits[1]) -eq $pickerDirectory
    })
    ($folderRow.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    if (-not (Wait-SettingsUi {
        $script:folderDialog = [WorkspaceSettingsProbe]::Window([uint32]$process.Id, '#32770')
        $folderDialog -ne [IntPtr]::Zero
    })) { throw 'Folder picker did not reopen.' }
    $folderTree = [WorkspaceSettingsProbe]::Child($folderDialog, 'SysTreeView32')
    if ($folderTree -eq [IntPtr]::Zero) { throw 'Folder picker has no navigation tree.' }
    $selectedFolder = [WorkspaceSettingsProbe]::SendMessageW($folderTree, 0x110a, [IntPtr]9, [IntPtr]::Zero)
    $null = [WorkspaceSettingsProbe]::SendMessageW($folderTree, 0x1102, [IntPtr]2, $selectedFolder)
    $childFolder = [IntPtr]::Zero
    if (-not (Wait-SettingsUi {
        $script:childFolder = [WorkspaceSettingsProbe]::SendMessageW($folderTree, 0x110a, [IntPtr]4, $selectedFolder)
        $childFolder -ne [IntPtr]::Zero
    })) { throw 'Chosen folder did not appear in the folder picker.' }
    $null = [WorkspaceSettingsProbe]::SendMessageW($folderTree, 0x110b, [IntPtr]9, $childFolder)
    $null = [WorkspaceSettingsProbe]::SendMessageW($folderDialog, 0x111, [IntPtr]1, [IntPtr]::Zero)
    Check 'folder picker updates the workspace directory draft' (Wait-SettingsUi { [WorkspaceSettingsProbe]::Text($generalEdits[1]) -eq $chosenDirectory })
    Invoke-SettingsNode 'Environment tab'
    Check 'workspace Environment tab has an inline editor' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 1 })
    [WorkspaceSettingsProbe]::SetText(@([WorkspaceSettingsProbe]::Edits($settingsOwner))[0], 'UI_LITERAL=literal "quotes" \n')
    Settings-Shot 'themed-workspace-environment'
    Invoke-SettingsNode 'General tab'
    Check 'workspace name and directory drafts survive tab switching' (Wait-SettingsUi { @([WorkspaceSettingsProbe]::Edits($settingsOwner)).Count -eq 2 -and [WorkspaceSettingsProbe]::Text(@([WorkspaceSettingsProbe]::Edits($settingsOwner))[0]) -eq 'Themed workspace' })
    Invoke-SettingsNode 'Apply'
    Check 'workspace Apply saves fields from both tabs together' (Wait-SettingsUi {
        $settings = (Invoke-Api 'workspace.settings' @{} $uiWorkspace).result.settings
        $settings.defaultDir -eq $chosenDirectory -and $settings.env.UI_LITERAL -ceq 'literal "quotes" \n'
    })
    Invoke-SettingsNode 'Use application directory default'
    Invoke-SettingsNode 'Apply'
    Check 'directory reset clears the override without losing env' (Wait-SettingsUi {
        $settings = (Invoke-Api 'workspace.settings' @{} $uiWorkspace).result.settings
        $null -eq $settings.defaultDir -and $settings.env.UI_LITERAL -ceq 'literal "quotes" \n'
    })
    [WorkspaceSettingsProbe]::SetText(@([WorkspaceSettingsProbe]::Edits($settingsOwner))[0], 'Cancelled workspace name')
    Invoke-SettingsNode 'Cancel'
    Check 'workspace Cancel discards unapplied name edits' ((Invoke-Api 'tree').result.workspaces[0].name -eq 'Themed workspace')
    $settingsUiPassed = $true
} finally {
    $folderDialog = [WorkspaceSettingsProbe]::Window([uint32]$process.Id, '#32770')
    if ($folderDialog -ne [IntPtr]::Zero) { $null = [WorkspaceSettingsProbe]::SendMessageW($folderDialog, 0x111, [IntPtr]2, [IntPtr]::Zero) }
    [WorkspaceSettingsProbe]::Key($settingsOwner, 27)
    if ($settingsUiPassed) {
        $null = Invoke-Api 'environment.set' @{ scope = 'global'; env = $initialEnvironment }
        $null = Invoke-Api 'workspace.settings' @{ settings = $initialSettings } $uiWorkspace
        $null = Invoke-Api 'workspace.rename' @{ name = $initialName } $uiWorkspace
    }
}
