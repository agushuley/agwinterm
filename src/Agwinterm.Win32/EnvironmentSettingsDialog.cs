using System.Runtime.InteropServices;
using System.Text;
using Agwinterm.Pty;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal sealed class EnvironmentSettingsDialog : IDisposable
{
    private const string ClassName = "Agwinterm.EnvironmentSettings";
    private static readonly WndProc RootProc = WindowProc;
    private static readonly Dictionary<IntPtr, EnvironmentSettingsDialog> Windows = new();
    private static EnvironmentSettingsDialog? _creating;
    private static bool _registered;
    private readonly IntPtr _owner;
    private readonly string? _name;
    private readonly WorkspaceOptions _options;
    private readonly IReadOnlyDictionary<string, string> _originalEnv;
    private readonly string[] _profiles;
    private readonly Func<string, WorkspaceOptions, IReadOnlyDictionary<string, string>, string?> _apply;
    private readonly Action _closed;
    private readonly WndProc _childProc;
    private readonly Dictionary<IntPtr, IntPtr> _originalProcs = new();
    private IntPtr _hwnd, _nameEdit, _directoryEdit, _profileList, _envEdit, _error, _cancel;
    private bool _disposed;
    private int _scale = 96;

    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetNextDlgTabItem(IntPtr hwnd, IntPtr control, bool previous);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);

    public EnvironmentSettingsDialog(IntPtr owner, string? name, WorkspaceOptions options,
        IReadOnlyDictionary<string, string> environment, string[] profiles,
        Func<string, WorkspaceOptions, IReadOnlyDictionary<string, string>, string?> apply, Action closed)
    {
        _owner = owner; _name = name; _options = options; _originalEnv = environment;
        _profiles = profiles; _apply = apply; _closed = closed; _childProc = ChildProc;
    }

    private int Scale(int value) => (int)Math.Round(value * _scale / 96.0);

    public void Show()
    {
        try
        {
            if (!_registered)
            {
                var cls = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = RootProc,
                    hInstance = GetModuleHandleW(null), hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                    hbrBackground = (IntPtr)6, lpszClassName = ClassName };
                if (RegisterClassExW(ref cls) == 0) throw new InvalidOperationException("Environment settings class registration failed.");
                _registered = true;
            }
            _scale = (int)GetDpiForWindow(_owner);
            if (_scale <= 0) _scale = 96;
            GetWindowRect(_owner, out var ownerRect);
            _creating = this;
            try
            {
                _hwnd = CreateWindowExW(0x10000, ClassName, _name is null ? "Environment Variables" : "Workspace Settings",
                    WS_POPUP | WS_CAPTION | WS_SYSMENU, ownerRect.left + Scale(24), ownerRect.top + Scale(24),
                    Scale(700), Scale(_name is null ? 520 : 660), _owner, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            }
            finally { _creating = null; }
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Environment settings window creation failed.");
            int top = 12;
            if (_name is not null)
            {
                Add("STATIC", "Workspace name", 0, 0, 12, top, 650, 20);
                _nameEdit = Add("EDIT", _name, WS_TABSTOP | ES_AUTOHSCROLL | 0x800000, 101, 12, top + 22, 650, 25);
                top += 54;
                Add("STATIC", "Default directory (empty = inherit)", 0, 0, 12, top, 650, 20);
                _directoryEdit = Add("EDIT", _options.DefaultDir ?? "", WS_TABSTOP | ES_AUTOHSCROLL | 0x800000, 102, 12, top + 22, 650, 25);
                top += 54;
                Add("STATIC", "Default shell profile", 0, 0, 12, top, 650, 20);
                _profileList = Add("COMBOBOX", "", WS_TABSTOP | CBS_DROPDOWNLIST | WS_VSCROLL, 103, 12, top + 22, 650, 240);
                SendMessageW(_profileList, CB_ADDSTRING, IntPtr.Zero, "Use application default");
                foreach (string profile in _profiles) SendMessageW(_profileList, CB_ADDSTRING, IntPtr.Zero, profile);
                int selected = Array.FindIndex(_profiles, profile => profile.Equals(_options.DefaultProfile, StringComparison.OrdinalIgnoreCase));
                if (_options.DefaultProfile is not null && selected < 0)
                {
                    SendMessageW(_profileList, CB_ADDSTRING, IntPtr.Zero, _options.DefaultProfile + " (missing)");
                    selected = _profiles.Length;
                }
                SendMessageW(_profileList, CB_SETCURSEL, (IntPtr)(selected + 1), IntPtr.Zero);
                top += 54;
            }
            Add("STATIC", "Environment variables: NAME=value or NAME<<MARKER … MARKER", 0, 0, 12, top, 650, 22);
            top += 24;
            _envEdit = Add("EDIT", EnvironmentOverrides.FormatEditor(_originalEnv), WS_TABSTOP | WS_VSCROLL | 0x100000 | 0x800000 | 0x4 | 0x40 | 0x80 | 0x1000,
                104, 12, top, 650, 240);
            SendMessageW(_envEdit, 0xc5, (IntPtr)int.MaxValue, IntPtr.Zero);
            top += 248;
            Add("STATIC", "Literal values; no substitutions. Stored as plaintext. Applies to new processes only.", 0, 0, 12, top, 650, 34);
            top += 34;
            _error = Add("STATIC", "", 0, 0, 12, top, 650, 44);
            top += 46;
            Add("BUTTON", "Apply", WS_TABSTOP | 1, 1, 450, top, 100, 28);
            _cancel = Add("BUTTON", "Cancel", WS_TABSTOP, 2, 562, top, 100, 28);
            var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(MonitorFromRect(ref ownerRect, MONITOR_DEFAULTTONEAREST), ref monitor);
            int width = Scale(700), height = Scale(top + 78);
            int left = Math.Clamp(ownerRect.left + Scale(24), monitor.rcWork.left, Math.Max(monitor.rcWork.left, monitor.rcWork.right - width));
            int windowTop = Math.Clamp(ownerRect.top + Scale(24), monitor.rcWork.top, Math.Max(monitor.rcWork.top, monitor.rcWork.bottom - height));
            SetWindowPos(_hwnd, IntPtr.Zero, left, windowTop, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
            EnableWindow(_owner, false);
            ShowWindow(_hwnd, SW_SHOW); Activate();
            SetFocus(_nameEdit != IntPtr.Zero ? _nameEdit : _envEdit);
        }
        catch { Dispose(); throw; }
    }

    private IntPtr Add(string type, string text, uint style, int id, int x, int y, int width, int height)
    {
        var control = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style, Scale(x), Scale(y), Scale(width), Scale(height),
            _hwnd, (IntPtr)id, GetModuleHandleW(null), IntPtr.Zero);
        if (control == IntPtr.Zero) throw new InvalidOperationException("Environment settings control creation failed.");
        SendMessageW(control, WM_SETFONT, GetStockObject(17), (IntPtr)1);
        if ((style & WS_TABSTOP) != 0)
            _originalProcs[control] = SetWindowLongPtrW(control, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_childProc));
        return control;
    }

    private static string Text(IntPtr control)
    {
        var text = new StringBuilder(GetWindowTextLengthW(control) + 1);
        GetWindowTextW(control, text, text.Capacity);
        return text.ToString();
    }

    private void Apply()
    {
        try
        {
            string text = Text(_envEdit);
            var environment = EnvironmentOverrides.ParseEditor(text, _originalEnv);
            var options = _options;
            string name = _name ?? "";
            if (_name is not null)
            {
                name = Text(_nameEdit);
                string directory = Text(_directoryEdit);
                int selected = (int)SendMessageW(_profileList, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                options = options with { DefaultDir = directory.Length == 0 ? null : Path.GetFullPath(directory),
                    DefaultProfile = selected <= 0 ? null : selected > _profiles.Length ? _options.DefaultProfile : _profiles[selected - 1] };
            }
            string? error = _apply(name, options, environment);
            if (error is not null) { SetWindowTextW(_error, error); return; }
            Dispose();
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetWindowTextW(_error, error is FormatException ? error.Message : "Could not save settings. Check the directory and file permissions.");
        }
    }

    public void Activate() { if (_hwnd != IntPtr.Zero) SetForegroundWindow(_hwnd); }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr key, IntPtr detail)
    {
        if (!Windows.TryGetValue(hwnd, out var dialog))
        {
            dialog = _creating;
            if (dialog is null) return DefWindowProcW(hwnd, message, key, detail);
            dialog._hwnd = hwnd; Windows[hwnd] = dialog;
        }
        if (message == WM_CLOSE) { dialog.Dispose(); return IntPtr.Zero; }
        if (message == WM_COMMAND && ((long)key >> 16 & 0xffff) == 0)
        {
            if (((long)key & 0xffff) == 1) dialog.Apply();
            else if (((long)key & 0xffff) == 2) dialog.Dispose();
            return IntPtr.Zero;
        }
        if (message == 0x82) { Windows.Remove(hwnd); dialog._hwnd = IntPtr.Zero; dialog.Dispose(); }
        return DefWindowProcW(hwnd, message, key, detail);
    }

    private IntPtr ChildProc(IntPtr hwnd, uint message, IntPtr key, IntPtr detail)
    {
        var original = _originalProcs[hwnd];
        if (message == 0x87) return CallWindowProcW(original, hwnd, message, key, detail) | (IntPtr)4;
        if (message == WM_KEYDOWN)
        {
            if ((int)key == VK_ESCAPE) { Dispose(); return IntPtr.Zero; }
            if ((int)key == VK_RETURN && hwnd != _envEdit)
            {
                if (hwnd == _cancel) Dispose(); else Apply();
                return IntPtr.Zero;
            }
            if ((int)key == VK_TAB) { SetFocus(GetNextDlgTabItem(_hwnd, hwnd, GetKeyState(VK_SHIFT) < 0)); return IntPtr.Zero; }
        }
        if (message == WM_CHAR && ((int)key == VK_TAB || (int)key == VK_ESCAPE)) return IntPtr.Zero;
        return CallWindowProcW(original, hwnd, message, key, detail);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (control, original) in _originalProcs) SetWindowLongPtrW(control, GWLP_WNDPROC, original);
        _originalProcs.Clear();
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        EnableWindow(_owner, true);
        _closed();
    }
}
