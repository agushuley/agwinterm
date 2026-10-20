using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

internal partial class Program
{
    private WndProc? _settingsTextProc;
    private IntPtr _settingsEditBrush;
    private int _settingsEditColor = -1;

    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr window);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr window, string theme, string? idList);

    private IDWriteTextFormat SettingsParagraph()
    {
        if (_settingsParagraph is null)
        {
            _settingsParagraph = NewChromeFormat("Segoe UI", 11.5f, center: false);
            _settingsParagraph.WordWrapping = WordWrapping.Wrap;
            _settingsParagraph.ParagraphAlignment = ParagraphAlignment.Near;
        }
        return _settingsParagraph;
    }

    private void DrawSettingsTextRow(ID2D1HwndRenderTarget target, ID2D1SolidColorBrush brush, SetRow row,
        float left, float right, float top, float height)
    {
        brush.Color = ChromeText;
        target.DrawText(row.Label, _uiSmall, new Rect(left, top, right - left, 22f), brush);
        float fieldTop = top + 26f, fieldHeight = height - 32f;
        var field = new RoundedRectangle { Rect = new Rect(left, fieldTop, right - left, fieldHeight), RadiusX = 5f, RadiusY = 5f };
        brush.Color = Mix(PalBg, ChromeText, 0.06f); target.FillRoundedRectangle(field, brush);
        brush.Color = ReferenceEquals(_setFocus, row) ? ChromeAccent : PalBorder; target.DrawRoundedRectangle(field, brush, 1f);
        row.Hx0 = left; row.Hx1 = right; row.Hy0 = fieldTop; row.Hy1 = fieldTop + fieldHeight;
        PositionSettingsText(row, left + 6f, fieldTop + 4f, right - left - 12f, fieldHeight - 8f);
    }

    private void PositionSettingsText(SetRow row, float left, float top, float width, float height)
    {
        if (_ddRow is not null) { HideSettingsText(row); return; }
        if (row.Edit == IntPtr.Zero)
        {
            _settingsTextProc ??= SettingsTextProc;
            uint style = WS_CHILD | WS_TABSTOP | ES_AUTOHSCROLL;
            if (row.Multiline) style |= 0x4 | 0x40 | 0x1000 | WS_VSCROLL | 0x100000;
            row.Edit = CreateWindowExW(0, "EDIT", row.Text, style, 0, 0, 1, 1, _hwnd,
                (IntPtr)(400 + _setRows.IndexOf(row)), GetModuleHandleW(null), IntPtr.Zero);
            if (row.Edit == IntPtr.Zero) throw new InvalidOperationException("Settings text control creation failed.");
            SendMessageW(row.Edit, 0xc5, (IntPtr)int.MaxValue, IntPtr.Zero);
            row.EditProc = SetWindowLongPtrW(row.Edit, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_settingsTextProc));
            SetWindowTheme(row.Edit, PalBg.R + PalBg.G + PalBg.B > 1.5f ? "Explorer" : "DarkMode_Explorer", null);
        }
        int dpi = (int)_dpi;
        if (row.EditDpi != dpi)
        {
            var font = CreateFontW(-Math.Max(1, (int)Math.Round(13 * Scale)), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0,
                row.Multiline ? "Consolas" : "Segoe UI");
            SendMessageW(row.Edit, WM_SETFONT, font, (IntPtr)1);
            if (row.EditFont != IntPtr.Zero) DeleteObject(row.EditFont);
            row.EditFont = font; row.EditDpi = dpi;
        }
        var rect = new RECT { left = ToDevice(left), top = ToDevice(top), right = ToDevice(left + width), bottom = ToDevice(top + height) };
        int clipTop = Math.Max(0, ToDevice(_setPaneTop) - rect.top);
        int clipBottom = Math.Min(rect.bottom - rect.top, ToDevice(_setPaneBottom) - rect.top);
        if (clipBottom <= clipTop) { HideSettingsText(row); return; }
        bool moved = !row.EditRect.Equals(rect);
        if (moved)
            SetWindowPos(row.Edit, IntPtr.Zero, rect.left, rect.top, Math.Max(1, rect.right - rect.left), Math.Max(1, rect.bottom - rect.top), SWP_NOZORDER | SWP_NOACTIVATE);
        if (moved || row.EditClipTop != clipTop || row.EditClipBottom != clipBottom)
        {
            var region = CreateRectRgn(0, clipTop, Math.Max(1, rect.right - rect.left), clipBottom);
            if (SetWindowRgn(row.Edit, region, true) == 0) DeleteObject(region);
            row.EditRect = rect; row.EditClipTop = clipTop; row.EditClipBottom = clipBottom;
        }
        if (!row.EditVisible) { ShowWindow(row.Edit, SW_SHOWNOACTIVATE); row.EditVisible = true; }
        if (_setFocusKb && _setNav < 0 && ReferenceEquals(_setFocus, row) && GetFocus() == _hwnd) SetFocus(row.Edit);
    }

    private void HideSettingsText(SetRow row)
    {
        if (!row.EditVisible) return;
        if (GetFocus() == row.Edit) SetFocus(_hwnd);
        ShowWindow(row.Edit, 0); row.EditVisible = false;
    }

    private void SetSettingsText(SetRow row, string text)
    {
        row.Text = text; row.WriteValue?.Invoke(text);
        if (row.Edit != IntPtr.Zero) SetWindowTextW(row.Edit, text);
    }

    private bool SettingsTextChanged(IntPtr control, int notification)
    {
        var row = _setRows.FirstOrDefault(row => row.Edit != IntPtr.Zero && row.Edit == control);
        if (row is null) return false;
        if (notification == 0x300) CaptureSettingsText(row);
        return true;
    }

    private void CaptureSettingsText(SetRow row)
    {
        var text = new StringBuilder(GetWindowTextLengthW(row.Edit) + 1);
        GetWindowTextW(row.Edit, text, text.Capacity);
        if (row.Text == text.ToString()) return;
        row.Text = text.ToString(); row.WriteValue?.Invoke(row.Text);
        if (_settingsWorkspace is null) { _settingsMessage = ""; _settingsFailed = false; }
        RequestRedraw();
    }

    private IntPtr SettingsTextColor(IntPtr context)
    {
        var background = Mix(PalBg, ChromeText, 0.06f);
        int color = RGB((int)(background.R * 255), (int)(background.G * 255), (int)(background.B * 255));
        if (_settingsEditBrush == IntPtr.Zero || color != _settingsEditColor)
        {
            if (_settingsEditBrush != IntPtr.Zero) DeleteObject(_settingsEditBrush);
            _settingsEditBrush = CreateSolidBrush(color); _settingsEditColor = color;
        }
        SetTextColor(context, RGB((int)(ChromeText.R * 255), (int)(ChromeText.G * 255), (int)(ChromeText.B * 255)));
        SetBkColor(context, color);
        return _settingsEditBrush;
    }

    private IntPtr SettingsTextProc(IntPtr control, uint message, IntPtr key, IntPtr detail)
    {
        var row = _setRows.FirstOrDefault(row => row.Edit == control);
        if (row is null) return DefWindowProcW(control, message, key, detail);
        IntPtr original = row.EditProc;
        if (message == 0xc)
        {
            var result = CallWindowProcW(original, control, message, key, detail);
            CaptureSettingsText(row);
            return result;
        }
        if (message == 0x87) return CallWindowProcW(original, control, message, key, detail) | (IntPtr)4;
        if (message == WM_SETFOCUS)
        {
            _setFocus = row; _setNav = -1;
            _uia.Announce(row.Kind == SW.Path ? row.Label + ", Alt+Down to browse" : row.Label);
            ScrollSetFocusIntoView(); RequestRedraw();
        }
        if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            int value = (int)key;
            if (row.Kind == SW.Path && value == VK_DOWN && KeyDown(VK_MENU))
            {
                SetFocus(_hwnd); BrowseSettingsDirectory(row); return IntPtr.Zero;
            }
            if (value is VK_TAB or VK_ESCAPE || (KeyDown(VK_CONTROL) && value is VK_PRIOR or VK_NEXT))
            {
                SetFocus(_hwnd); SettingsKey(value); return IntPtr.Zero;
            }
            if (value == 'A' && KeyDown(VK_CONTROL)) { SendMessageW(control, 0xb1, IntPtr.Zero, (IntPtr)(-1)); return IntPtr.Zero; }
            if (value == VK_RETURN && !row.Multiline) { SetFocus(_hwnd); MoveSetFocusLinear(1); return IntPtr.Zero; }
        }
        if (message == WM_CHAR && ((int)key is 1 or VK_TAB or VK_ESCAPE || ((int)key == VK_RETURN && !row.Multiline))) return IntPtr.Zero;
        return CallWindowProcW(original, control, message, key, detail);
    }

    private void DestroySettingsTextControls()
    {
        foreach (var row in _setRows)
        {
            if (row.Edit != IntPtr.Zero)
            {
                SetWindowLongPtrW(row.Edit, GWLP_WNDPROC, row.EditProc);
                DestroyWindow(row.Edit); row.Edit = IntPtr.Zero; row.EditVisible = false;
            }
            if (row.EditFont != IntPtr.Zero) { DeleteObject(row.EditFont); row.EditFont = IntPtr.Zero; row.EditDpi = 0; }
        }
        if (_settingsEditBrush != IntPtr.Zero) { DeleteObject(_settingsEditBrush); _settingsEditBrush = IntPtr.Zero; }
        _settingsParagraph?.Dispose(); _settingsParagraph = null;
    }
}
