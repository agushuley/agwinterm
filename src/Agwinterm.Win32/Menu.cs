using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.DCommon;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// Popup menus hosted in REAL top-level windows (WS_POPUP + drop shadow), so they can extend
/// beyond the main window's bounds like a native menu — but rendered with the app theme
/// (Direct2D + the chrome palette). Two callers: the sidebar's right-click context menu, and the
/// title bar's menu bar (MenuBar.cs), whose dropdowns are the same popup with a bar to switch
/// between. A row can carry a flyout (<see cref="PalItem.Submenu"/>: File ▸ Open Window, Open
/// Recent), which opens as a second level beside it.
///
/// <para>Input model mirrors native menus: level 0 captures the mouse while open, so every mouse
/// message arrives there and is routed by SCREEN position — a click inside a level runs its row,
/// a click on the flyout's parent row keeps the flyout, a click on the bar switches or closes,
/// a click anywhere else dismisses and is swallowed; losing capture dismisses. The main window
/// keeps keyboard focus (WS_EX_NOACTIVATE) and forwards Up/Down/Left/Right/Enter/Esc via
/// <see cref="MenuKeyDown"/>.</para>
/// </summary>
internal partial class Program
{
    private const string MenuClassName = "agwinterm-menu";
    private static bool _menuClassReady;
    private static WndProc? _menuProcKeep;                            // GC-rooted class wndproc
    private static readonly Dictionary<IntPtr, Program> _menuByHwnd = new();

    /// <summary>One open level: the dropdown / context menu (level 0, which holds the mouse
    /// capture) or a flyout beside a submenu row of the level below it.</summary>
    private sealed class MenuLevel
    {
        public IntPtr Hwnd;
        public ID2D1HwndRenderTarget? Rt;
        public ID2D1SolidColorBrush? Brush;
        public List<PalItem> Items = new();
        public int Sel = -1;            // hover/keyboard selection
        public float W, H;              // DIPs, like the rest of the layout
        public int X, Y;                // screen px of the window's origin
        public float Gutter;            // check-mark column width (0 when no row is checkable)
        public int ParentRow = -1;      // the row of the level below that this flyout hangs off
    }
    private readonly List<MenuLevel> _menuLevels = new();
    private bool _menuClosing;
    /// <summary>The dropdown / context menu's window while one is up (level 0), else zero.</summary>
    private IntPtr _menuHwnd => _menuLevels.Count > 0 ? _menuLevels[0].Hwnd : IntPtr.Zero;

    private const float MenuRowH = 30f, MenuSepH = 9f, MenuPadY = 5f, MenuPadX = 12f, MenuCheckW = 20f;

    /// <summary>A visual separator row (drawn as a hairline; never selectable).</summary>
    private static PalItem MenuSeparator() => new() { Label = "-" };
    private static bool IsSep(PalItem it) => it.Label == "-" && it.Run is null && it.Submenu is null;
    /// <summary>A row the pointer or the keyboard can land on: it runs, or it opens a flyout, and
    /// its enablement predicate (if any) agrees right now.</summary>
    private static bool MenuActionable(PalItem it) => (it.Run is not null || it.Submenu is not null) && it.Enabled?.Invoke() != false;

    /// <summary>Show <paramref name="items"/> as a popup menu at screen point (sx, sy).</summary>
    /// <param name="flipTop">Screen y the popup must end ABOVE when it opens upward — a bar
    /// dropdown passes the title bar's top, so the labels the pointer must still reach stay
    /// uncovered; a context menu leaves it null and flips at its own anchor.</param>
    private void ShowMenuWindow(List<PalItem> items, int sx, int sy, int? flipTop = null)
    {
        CloseMenuWindow();
        if (items.Count == 0) return;
        OpenMenuLevel(items, sx, sy, parentRow: -1, flipTop);
        if (_menuLevels.Count > 0) SetCapture(_menuLevels[0].Hwnd);   // native-menu input model: all mouse routes here until dismissed
    }

    private void OpenMenuLevel(List<PalItem> items, int sx, int sy, int parentRow, int? flipTop = null)
    {
        var lv = new MenuLevel { Items = items, ParentRow = parentRow };
        lv.Gutter = items.Any(it => it.Checked) ? MenuCheckW : 0f;

        // Size to content, clamped; height is exact (a menu is short by construction).
        float maxW = 0f;
        foreach (var it in items)
        {
            if (IsSep(it)) continue;
            float w = MeasureText(it.Label, _uiFont)
                + (it.Hint.Length > 0 ? MeasureText(it.Hint, _uiSmall) + 28f : 0f)
                + (it.Submenu is not null ? 24f : 0f);
            maxW = MathF.Max(maxW, w);
        }
        lv.W = Math.Clamp(maxW + lv.Gutter + MenuPadX * 2f + 8f, 180f, 480f);
        lv.H = MenuPadY * 2f;
        foreach (var it in items) lv.H += IsSep(it) ? MenuSepH : MenuRowH;
        int wpx = (int)(lv.W * Scale), hpx = (int)(lv.H * Scale);

        // Clamp to the monitor work area. Below the anchor when it fits; else above it — above the
        // BAR for a dropdown (flipTop), as a native menu bar flips, never over the labels — else,
        // when it fits nowhere (a small screen, a tall menu), as far down as the work area allows:
        // the old "up from the anchor" put a File dropdown over the whole bar on a 768-px screen,
        // and the pointer could not reach View. A flyout that would run off the right edge opens to
        // the LEFT of its parent instead.
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromPoint(new POINT { x = sx, y = sy }, MONITOR_DEFAULTTONEAREST), ref mi);
        int x = sx;
        if (parentRow >= 0 && _menuLevels.Count > 0 && sx + wpx > mi.rcWork.right)
            x = _menuLevels[^1].X - wpx + (int)(4f * Scale);
        x = Math.Clamp(x, mi.rcWork.left, Math.Max(mi.rcWork.left, mi.rcWork.right - wpx));
        int top = flipTop ?? sy;
        int y = sy + hpx <= mi.rcWork.bottom ? sy
              : top - hpx >= mi.rcWork.top ? top - hpx
              : Math.Max(mi.rcWork.top, mi.rcWork.bottom - hpx);
        lv.X = x; lv.Y = y;

        if (!_menuClassReady)
        {
            _menuProcKeep = MenuProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW | CS_DROPSHADOW,
                lpfnWndProc = _menuProcKeep,
                hInstance = _hInstance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                lpszClassName = MenuClassName,
            };
            if (RegisterClassExW(ref wc) == 0) return;
            _menuClassReady = true;
        }

        lv.Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, MenuClassName, "",
            WS_POPUP, x, y, wpx, hpx, _hwnd, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (lv.Hwnd == IntPtr.Zero) return;
        _menuByHwnd[lv.Hwnd] = this;

        var props = new RenderTargetProperties
        {
            Type = RenderTargetType.Default,
            PixelFormat = new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
            DpiX = _dpi,
            DpiY = _dpi,
        };
        // PixelSize is the SURFACE (device pixels); W/H are DIPs like the rest of the layout.
        var hp = new HwndRenderTargetProperties { Hwnd = lv.Hwnd, PixelSize = new SizeI(wpx, hpx), PresentOptions = PresentOptions.None };
        lv.Rt = _d2d.CreateHwndRenderTarget(props, hp);
        lv.Rt.Dpi = new Vortice.Mathematics.Size(_dpi, _dpi);   // properties alone do not stick (see CreateRenderTarget)
        lv.Rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        lv.Brush = lv.Rt.CreateSolidColorBrush(ChromeText);
        _menuLevels.Add(lv);
        ShowWindow(lv.Hwnd, SW_SHOWNOACTIVATE);
    }

    /// <summary>Open the flyout of level 0's row <paramref name="row"/> (a <see cref="PalItem.Submenu"/>
    /// row) beside it; a flyout already open on that row stays. Returns the flyout, or null.</summary>
    private MenuLevel? OpenMenuFlyout(int row)
    {
        if (_menuLevels.Count == 0) return null;
        var root = _menuLevels[0];
        if (row < 0 || row >= root.Items.Count || root.Items[row].Submenu is not { } build || !MenuActionable(root.Items[row])) return null;
        if (_menuLevels.Count > 1)
        {
            if (_menuLevels[1].ParentRow == row) return _menuLevels[1];
            CloseMenuFlyout();
        }
        var items = build();
        if (items.Count == 0) return null;
        root.Sel = row;
        int sx = root.X + (int)((root.W - 4f) * Scale);
        int sy = root.Y + (int)((MenuRowTop(root, row) - MenuPadY) * Scale);
        OpenMenuLevel(items, sx, sy, parentRow: row);
        InvalidateRect(root.Hwnd, IntPtr.Zero, false);
        return _menuLevels.Count > 1 ? _menuLevels[1] : null;
    }

    private void CloseMenuFlyout()
    {
        while (_menuLevels.Count > 1) DestroyMenuLevel(_menuLevels[^1]);
        if (_menuLevels.Count > 0) InvalidateRect(_menuLevels[0].Hwnd, IntPtr.Zero, false);
    }

    private void DestroyMenuLevel(MenuLevel lv)
    {
        _menuLevels.Remove(lv);
        _menuByHwnd.Remove(lv.Hwnd);
        lv.Brush?.Dispose(); lv.Brush = null;
        lv.Rt?.Dispose(); lv.Rt = null;
        if (lv.Hwnd != IntPtr.Zero) DestroyWindow(lv.Hwnd);
        lv.Hwnd = IntPtr.Zero;
    }

    private void CloseMenuWindow()
    {
        if (_menuLevels.Count == 0 || _menuClosing) return;
        _menuClosing = true;
        bool held = GetCapture() == _menuLevels[0].Hwnd;
        while (_menuLevels.Count > 0) DestroyMenuLevel(_menuLevels[^1]);
        if (held) ReleaseCapture();
        _menuClosing = false;
        // The bar's label stops showing as open, and its keyboard focus goes with the menu: a caller
        // that wants the bar focused after the close (Esc) re-focuses it explicitly.
        if (_menuBarOpen >= 0 || _menuBarFocus >= 0) { _menuBarOpen = -1; _menuBarFocus = -1; RequestRedraw(); }
    }

    /// <summary>Top of row <paramref name="i"/> within a level, in DIPs from the window's top.</summary>
    private static float MenuRowTop(MenuLevel lv, int i)
    {
        float y = MenuPadY;
        for (int k = 0; k < i && k < lv.Items.Count; k++) y += IsSep(lv.Items[k]) ? MenuSepH : MenuRowH;
        return y;
    }

    /// <summary>Actionable row index at a level-local DIP point, or -1 (separators / disabled rows aren't hits).</summary>
    private static int MenuIndexAt(MenuLevel lv, float mx, float my)
    {
        if (mx < 0 || mx >= lv.W) return -1;
        float y = MenuPadY;
        for (int i = 0; i < lv.Items.Count; i++)
        {
            float h = IsSep(lv.Items[i]) ? MenuSepH : MenuRowH;
            if (my >= y && my < y + h) return MenuActionable(lv.Items[i]) ? i : -1;
            y += h;
        }
        return -1;
    }

    /// <summary>The topmost level under a screen point, with the point in that level's DIPs.</summary>
    private (MenuLevel lv, float x, float y)? MenuLevelAt(POINT sp)
    {
        for (int i = _menuLevels.Count - 1; i >= 0; i--)
        {
            var lv = _menuLevels[i];
            float x = (sp.x - lv.X) / Scale, y = (sp.y - lv.Y) / Scale;
            if (x >= 0 && x < lv.W && y >= 0 && y < lv.H) return (lv, x, y);
        }
        return null;
    }

    private void MenuMoveSel(MenuLevel lv, int dir)
    {
        int n = lv.Items.Count, i = lv.Sel;
        for (int step = 0; step < n; step++)
        {
            i = ((i + dir) % n + n) % n;
            if (MenuActionable(lv.Items[i])) { lv.Sel = i; break; }
        }
        MenuSelChanged(lv);
    }

    private void MenuSelectEdge(MenuLevel lv, bool first)
    {
        int n = lv.Items.Count;
        for (int k = 0; k < n; k++)
        {
            int i = first ? k : n - 1 - k;
            if (MenuActionable(lv.Items[i])) { lv.Sel = i; break; }
        }
        MenuSelChanged(lv);
    }

    /// <summary>A level's selection moved: repaint it, and tell a screen reader which row Enter
    /// would now run — the rows are UIA MenuItems (MenuBar.cs), and a reader follows focus events,
    /// not the Focused property.</summary>
    private void MenuSelChanged(MenuLevel lv)
    {
        InvalidateRect(lv.Hwnd, IntPtr.Zero, false);
        if (_menuBarOpen >= 0 && lv.Sel >= 0 && Uia.ClientsListening) _uia.RaiseFocus(Uia.NodeKind.MenuItem, MenuRowUiaIndex(lv, lv.Sel));
    }

    /// <summary>Run the selected row of the top level: a flyout row opens its flyout (first row
    /// selected, for the keyboard), a command row closes the menu and runs.</summary>
    private void MenuRunSelected()
    {
        if (_menuLevels.Count == 0) return;
        var top = _menuLevels[^1];
        if (top.Sel < 0 || top.Sel >= top.Items.Count || !MenuActionable(top.Items[top.Sel])) return;
        var it = top.Items[top.Sel];
        if (it.Submenu is not null)
        {
            if (_menuLevels.Count == 1 && OpenMenuFlyout(top.Sel) is { } fly) MenuSelectEdge(fly, first: true);
            return;
        }
        var run = it.Run!;
        CloseMenuWindow();
        run();
    }

    /// <summary>Keyboard while the menu is up (the main window keeps focus and forwards here):
    /// ↑/↓ move, ←/→ leave or enter a flyout (or switch bar menus), Enter runs, Esc closes one
    /// level; everything else is swallowed like a native menu. Alt+letter switches bar menus.</summary>
    private bool MenuKeyDown(int vk)
    {
        if (_menuLevels.Count == 0) return false;
        var top = _menuLevels[^1];
        bool inFlyout = _menuLevels.Count > 1;
        if ((KeyDown(VK_MENU) || _altContext) && _menuBarOpen >= 0 && MnemonicMenu(vk) is >= 0 and var mn)
        { OpenMenuBar(mn, keyboard: true); return true; }
        switch (vk)
        {
            case VK_MENU: CloseMenuWindow(); return true;   // Alt while a menu is up closes it, as a native menu does
            case VK_ESCAPE:
                if (inFlyout) CloseMenuFlyout();
                else
                {
                    int was = _menuBarOpen;
                    CloseMenuWindow();
                    if (was >= 0) FocusMenuBar(was);   // native: Esc closes the dropdown, the bar keeps the focus
                }
                return true;
            case VK_UP: MenuMoveSel(top, -1); return true;
            case VK_DOWN: MenuMoveSel(top, 1); return true;
            case VK_HOME: MenuSelectEdge(top, first: true); return true;
            case VK_END: MenuSelectEdge(top, first: false); return true;
            case VK_LEFT:
                if (inFlyout) CloseMenuFlyout();
                else if (_menuBarOpen >= 0) OpenMenuBar(MenuBarNeighbour(_menuBarOpen, -1), keyboard: true);
                return true;
            case VK_RIGHT:
                if (!inFlyout && top.Sel >= 0 && top.Sel < top.Items.Count && top.Items[top.Sel].Submenu is not null)
                { if (OpenMenuFlyout(top.Sel) is { } fly) MenuSelectEdge(fly, first: true); }
                else if (!inFlyout && _menuBarOpen >= 0) OpenMenuBar(MenuBarNeighbour(_menuBarOpen, 1), keyboard: true);
                return true;
            case VK_RETURN: case VK_SPACE: MenuRunSelected(); return true;
            default: return true;
        }
    }

    private void RenderMenuLevel(MenuLevel lv)
    {
        if (lv.Rt is null || lv.Brush is null) return;
        var rt = lv.Rt; var brush = lv.Brush;
        rt.BeginDraw();
        rt.Clear(PalBg);
        float y = MenuPadY, tx = MenuPadX + lv.Gutter;
        for (int i = 0; i < lv.Items.Count; i++)
        {
            var it = lv.Items[i];
            if (IsSep(it))
            {
                brush.Color = ChromeBorder;
                rt.DrawLine(new System.Numerics.Vector2(MenuPadX - 4f, y + MenuSepH / 2f),
                            new System.Numerics.Vector2(lv.W - MenuPadX + 4f, y + MenuSepH / 2f), brush, 1f);
                y += MenuSepH;
                continue;
            }
            bool live = MenuActionable(it);
            bool selected = i == lv.Sel && live;
            if (selected) { brush.Color = PalSel; rt.FillRectangle(new Rect(3f, y, lv.W - 6f, MenuRowH), brush); }
            var textColor = !live ? ChromeDim : (selected ? SbActiveText : ChromeText);
            if (it.Checked)
            {
                brush.Color = textColor;
                rt.DrawText("✓", _uiFont, new Rect(MenuPadX - 2f, y + (MenuRowH - 18f) / 2f, lv.Gutter, 18f), brush);
            }
            brush.Color = textColor;
            float reserve = it.Hint.Length > 0 ? 60f : (it.Submenu is not null ? 24f : 0f);
            rt.DrawText(it.Label, _uiFont, new Rect(tx, y + (MenuRowH - 18f) / 2f, lv.W - tx - MenuPadX - reserve, 18f), brush, DrawTextOptions.Clip);
            if (it.Submenu is not null)
            {
                brush.Color = live ? ChromeDim : WithA(ChromeDim, 0.6f);
                rt.DrawText("›", _uiFont, new Rect(lv.W - MenuPadX - 10f, y + (MenuRowH - 18f) / 2f, 12f, 18f), brush);
            }
            else if (it.Hint.Length > 0)
            {
                float hw = MeasureText(it.Hint, _uiSmall);
                brush.Color = live ? ChromeDim : WithA(ChromeDim, 0.6f);
                rt.DrawText(it.Hint, _uiSmall, new Rect(lv.W - MenuPadX - hw, y + (MenuRowH - 15f) / 2f, hw + 2f, 15f), brush);
            }
            y += MenuRowH;
        }
        brush.Color = PalBorder;
        rt.DrawRectangle(new Rect(0.5f, 0.5f, lv.W - 1f, lv.H - 1f), brush, 1f);
        rt.EndDraw();
    }

    /// <summary>Pointer moved (routed here from wherever it is, since level 0 holds the capture):
    /// select the row under it, open a flyout for a submenu row, close the flyout when the pointer
    /// returns to another level-0 row, or switch bar menus when it crosses onto another label.</summary>
    private void MenuHover(POINT sp)
    {
        if (MenuLevelAt(sp) is { } hit)
        {
            int i = MenuIndexAt(hit.lv, hit.x, hit.y);
            if (i != hit.lv.Sel && (i >= 0 || !ReferenceEquals(hit.lv, _menuLevels[0]) || _menuLevels.Count == 1))
            { hit.lv.Sel = i; MenuSelChanged(hit.lv); }
            if (ReferenceEquals(hit.lv, _menuLevels[0]))
            {
                if (i >= 0 && hit.lv.Items[i].Submenu is not null) OpenMenuFlyout(i);
                else if (i >= 0 && _menuLevels.Count > 1) CloseMenuFlyout();
            }
            return;
        }
        if (_menuBarOpen >= 0 && MenuBarLabelAtScreen(sp) is int other && other != _menuBarOpen) OpenMenuBar(other, _menuByKeyboard);
    }

    /// <summary>A button went down somewhere: inside a level it waits for the release; on the open
    /// menu's own bar label it closes (a toggle, like native); on another label it switches; anywhere
    /// else it dismisses. Swallowed in every case.</summary>
    private void MenuPress(POINT sp)
    {
        if (MenuLevelAt(sp) is not null) return;
        if (_menuBarOpen >= 0 && MenuBarLabelAtScreen(sp) is int label)
        {
            if (label == _menuBarOpen) CloseMenuWindow();
            else OpenMenuBar(label);
            return;
        }
        CloseMenuWindow();
    }

    private void MenuRelease(POINT sp)
    {
        if (MenuLevelAt(sp) is not { } hit) return;
        int i = MenuIndexAt(hit.lv, hit.x, hit.y);
        if (i < 0) return;
        hit.lv.Sel = i;
        var it = hit.lv.Items[i];
        if (it.Submenu is not null) { if (ReferenceEquals(hit.lv, _menuLevels[0])) OpenMenuFlyout(i); return; }
        var run = it.Run!;
        CloseMenuWindow();
        run();
    }

    private static POINT MenuScreenPoint(IntPtr hwnd, IntPtr lParam)
    {
        long l = (long)lParam;
        var p = new POINT { x = unchecked((short)(l & 0xFFFF)), y = unchecked((short)((l >> 16) & 0xFFFF)) };
        ClientToScreen(hwnd, ref p);
        return p;
    }

    private static IntPtr MenuProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_menuByHwnd.TryGetValue(hwnd, out var self)) return DefWindowProcW(hwnd, msg, wParam, lParam);
        switch (msg)
        {
            case WM_MOUSEACTIVATE: return (IntPtr)MA_NOACTIVATE;   // never steal focus from the main window
            case WM_PAINT:
                {
                    BeginPaint(hwnd, out PAINTSTRUCT ps);
                    try { if (self._menuLevels.FirstOrDefault(l => l.Hwnd == hwnd) is { } lv) self.RenderMenuLevel(lv); }
                    catch { /* rendering is best-effort; the menu closes on any click */ }
                    EndPaint(hwnd, ref ps);
                    return IntPtr.Zero;
                }
            // Mouse messages carry client coordinates of THIS window, negative under capture when
            // the pointer is outside it; the screen point is what every level and the bar are
            // compared against. Taken from the message (not GetCursorPos), so a posted message is
            // routed exactly like a real one.
            case WM_MOUSEMOVE: self.MenuHover(MenuScreenPoint(hwnd, lParam)); return IntPtr.Zero;
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN: self.MenuPress(MenuScreenPoint(hwnd, lParam)); return IntPtr.Zero;
            case WM_LBUTTONUP: self.MenuRelease(MenuScreenPoint(hwnd, lParam)); return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                if (!self._menuClosing && self._menuLevels.Count > 0 && hwnd == self._menuLevels[0].Hwnd) self.CloseMenuWindow();   // capture stolen (alt-tab, other app) — dismiss
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
