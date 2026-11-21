using System;
using System.Collections.Generic;
using System.Linq;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using static Agwinterm.Win32.Win32;

namespace Agwinterm.Win32;

/// <summary>
/// F1 Help overlay: a themed scrollable card listing how agwinterm works, the EFFECTIVE key
/// bindings (keymap.conf included), and an accessibility guide. When a screen reader is attached
/// the guide is spoken on open and the whole help text is exposed as a modal UIA document, so a
/// low-vision user gets an audio orientation of what they can do and how.
/// Plain F1 opens it even while a full-screen terminal application is running.
/// </summary>
internal partial class Program
{
    private bool _helpOpen;
    private float _helpScroll;
    private HelpRow[] _helpRows = Array.Empty<HelpRow>();
    private float _helpKeyCol;                                   // widest key + gap (proportional font: measured, not space-padded)
    private float _helpRowsW = -1f;                              // inner width _helpRowH was measured for
    private (float Key, float Body)[] _helpRowH = Array.Empty<(float, float)>();
    private Rect _helpCard;
    private float _helpCloseX0, _helpCloseY0, _helpCloseX1, _helpCloseY1;   // × button (matches Settings)

    private void OpenHelp()
    {
        _helpRows = BuildHelpRows();
        _helpKeyCol = _helpRows.Where(r => r.Kind == HelpKind.Binding).Select(r => MeasureText(r.Key, _uiSmall)).DefaultIfEmpty(0f).Max() + 16f;
        _helpRowsW = -1f;
        _helpOpen = true; _helpScroll = 0;
        if (Uia.ClientsListening) _uia.Announce(NarratorGuide());
        RequestRedraw();
    }

    private void CloseHelp()
    {
        _helpOpen = false;
        _uia.Announce("Help closed");
        RequestRedraw();
    }

    /// <summary>Spoken orientation for screen-reader users (concise; the full text is also readable
    /// as the modal help document via Caps+arrows).</summary>
    private static string NarratorGuide() =>
        "Help. agwinterm is a terminal with a session sidebar. " +
        "The terminal reads and navigates like a document: use Narrator's line commands to read output; new output is spoken automatically. " +
        "Press F6 to move focus between the terminal and the session list, then Up and Down to pick a session and Enter to open it. " +
        "Press Control Shift T for a new session, Control Backtick for the quick terminal, Control J for the scratch terminal. " +
        "The Settings dialog opens from the gear button and is fully keyboard navigable: Tab moves between controls, Space changes them, Page Up and Page Down switch tabs, Escape closes. " +
        "In this help screen, use Down and Up to scroll, Escape to close.";

    private enum HelpKind { Blank, Section, Text, Binding }

    /// <summary>One help row. A Binding draws Key in the shared key column and Text beside it
    /// (an empty Key continues the previous binding's description).</summary>
    private readonly record struct HelpRow(HelpKind Kind, string Text = "", string Key = "");

    private static HelpRow Section(string s) => new(HelpKind.Section, s);
    private static HelpRow Line(string s) => new(HelpKind.Text, s);
    private static HelpRow Bind(string key, string desc) => new(HelpKind.Binding, desc, key);
    private static readonly HelpRow Blank = new(HelpKind.Blank);

    private HelpRow[] BuildHelpRows()
    {
        var rows = new List<HelpRow>
        {
            Section("GETTING STARTED"),
            Line("agwinterm is an agent-first terminal: sessions live in the left sidebar,"),
            Line("each with a status dot an agent can set via the control API (agwintermctl)."),
            Blank,
            Section("FOCUS & NAVIGATION"),
            Bind("F6", "move focus between terminal and sidebar (arrows + Enter there)"),
            Bind("F1", "this help (including while full-screen terminal apps run)"),
            Bind("Alt or F10", "menu bar (File  View  Navigate  Help): arrows move, Enter opens, Esc leaves"),
            Bind("", "(a full-screen program keeps its F10; the Alt tap still reaches the bar)"),
            Bind("Alt, F/V/N/H", "open a menu (in the terminal Alt+letter goes to the program in the pane)"),
            Bind("Esc", "close overlays (help, settings, palettes, search)"),
            Blank,
            Section("KEY BINDINGS (effective — keymap.conf applied)"),
        };
        foreach (var kv in _keymap.OrderBy(k => FriendlyAction(k.Value), StringComparer.OrdinalIgnoreCase))
            rows.Add(Bind(Agwinterm.Core.Keymap.DisplayChord(kv.Key), FriendlyAction(kv.Value)));
        rows.AddRange(new[]
        {
            Blank,
            Section("ACCESSIBILITY"),
            Line("The terminal is a UIA document: screen readers read it line by line and"),
            Line("track the caret; new output is announced automatically after it settles."),
            Line("Settings is fully keyboard navigable (Tab / Space / PageUp / PageDown) and"),
            Line("buttons speak on hover and show tooltips. Sessions, buttons, and settings"),
            Line("controls are all scannable elements (Narrator: Caps Lock + arrows)."),
            Blank,
            Section("MORE"),
            Line("Settings: gear button or Ctrl+Shift+P → Settings.  Control API: agwintermctl --help."),
        });
        return rows.ToArray();
    }

    private static string FriendlyAction(string a) =>
        a.Replace('_', ' ') is { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : a;

    /// <summary>The whole help text (the UIA modal document's name while help is open).</summary>
    private string HelpText() =>
        string.Join("\n", _helpRows.Select(r => r.Key.Length > 0 ? r.Key + "  " + r.Text : r.Text));

    // _uiSmall, but wrapping: on a narrow card a long row wraps instead of running off the edge.
    private static IDWriteTextFormat? _helpFmt;
    private static IDWriteTextFormat HelpFmt
    {
        get
        {
            if (_helpFmt is null) { _helpFmt = NewChromeFormat("Segoe UI", 11.5f, center: false); _helpFmt.WordWrapping = WordWrapping.Wrap; }
            return _helpFmt;
        }
    }

    /// <summary>Per-row heights (18px per wrapped line) at this inner width, measured with the same
    /// format and widths DrawHelp draws with so wrapped rows never overlap.</summary>
    private void MeasureHelpRows(float inner, float keyCol)
    {
        static float RowH(string s, float w)
        {
            if (s.Length == 0) return 18f;
            using var tl = _dwrite.CreateTextLayout(s, HelpFmt, w, 10000f);
            return 18f * Math.Max(1, (int)tl.Metrics.LineCount);
        }
        _helpRowH = new (float, float)[_helpRows.Length];
        float total = 0f;
        for (int i = 0; i < _helpRows.Length; i++)
        {
            var r = _helpRows[i];
            bool bind = r.Kind == HelpKind.Binding;
            _helpRowH[i] = (bind ? RowH(r.Key, HelpKeyW(keyCol)) : 0f, RowH(r.Text, bind ? HelpBodyW(inner, keyCol) : MathF.Max(1f, inner)));
            total += MathF.Max(_helpRowH[i].Key, _helpRowH[i].Body);
        }
        _helpContentH = total;
        _helpRowsW = inner;
    }

    // The key rect gets slack past the widest measured key so float rounding never wraps it.
    private static float HelpKeyW(float keyCol) => MathF.Max(1f, keyCol - 8f);
    private static float HelpBodyW(float inner, float keyCol) => MathF.Max(1f, inner - keyCol);

    private void DrawHelp(ID2D1HwndRenderTarget rt, ID2D1SolidColorBrush brush)
    {
        if (!_helpOpen) return;
        int cw = ClientW(), ch = ClientH();
        brush.Color = new Color4(0f, 0f, 0f, 0.45f);           // dim the app behind
        rt.FillRectangle(new Rect(0, 0, cw, ch), brush);

        float cardW = MathF.Min(640f, cw - 60f), cardH = MathF.Min(680f, ch - 80f);
        float cx = (cw - cardW) / 2f, cy = MathF.Max(TitleBarH + 16f, (ch - cardH) / 2f);
        _helpCard = new Rect(cx, cy, cardW, cardH);
        brush.Color = PalBg;
        rt.FillRoundedRectangle(new RoundedRectangle { Rect = _helpCard, RadiusX = 10f, RadiusY = 10f }, brush);
        brush.Color = PalBorder;
        rt.DrawRoundedRectangle(new RoundedRectangle { Rect = _helpCard, RadiusX = 10f, RadiusY = 10f }, brush, 1f);

        brush.Color = ChromeText;
        rt.DrawText("Help", _uiFont, new Rect(cx + 20f, cy + 12f, 200f, 24f), brush);
        brush.Color = ChromeDim;
        rt.DrawText("Esc to close · ↑↓ scroll", _uiSmall, new Rect(cx + cardW - 220f, cy + 15f, 170f, 20f), brush);
        // Close (×) button, same treatment as the Settings card.
        float cbSz = 26f, cbX = cx + cardW - cbSz - 12f, cbY = cy + 9f;
        _helpCloseX0 = cbX; _helpCloseY0 = cbY; _helpCloseX1 = cbX + cbSz; _helpCloseY1 = cbY + cbSz;
        brush.Color = ChromeDim;
        rt.DrawText(GlyphClose, _iconSmall, new Rect(cbX, cbY, cbSz, cbSz), brush);

        float top = cy + 44f, bottom = cy + cardH - 14f;
        rt.PushAxisAlignedClip(new Rect(cx, top, cardW, bottom - top), AntialiasMode.Aliased);
        // One key column for every binding, capped so a long custom chord can't push descriptions off the card.
        float inner = cardW - 40f, keyCol = MathF.Min(_helpKeyCol, inner * 0.4f);
        if (inner != _helpRowsW) MeasureHelpRows(inner, keyCol);
        _helpScroll = Math.Clamp(_helpScroll, 0f, MathF.Max(0f, _helpContentH - (bottom - top)));   // a wider card can shrink the content
        float y = top - _helpScroll;
        for (int i = 0; i < _helpRows.Length && y <= bottom; i++)
        {
            var r = _helpRows[i];
            var (kh, bh) = _helpRowH[i];
            float h = MathF.Max(kh, bh);
            if (y + h > top && r.Kind != HelpKind.Blank)
            {
                brush.Color = r.Kind == HelpKind.Section ? ChromeAccent : ChromeText;
                if (r.Kind == HelpKind.Binding)
                {
                    rt.DrawText(r.Key, HelpFmt, new Rect(cx + 20f, y, HelpKeyW(keyCol), kh), brush);
                    rt.DrawText(r.Text, HelpFmt, new Rect(cx + 20f + keyCol, y, HelpBodyW(inner, keyCol), bh), brush);
                }
                else rt.DrawText(r.Text, HelpFmt, new Rect(cx + 20f, y, MathF.Max(1f, inner), bh), brush);
            }
            y += h;
        }
        rt.PopAxisAlignedClip();
    }

    private float _helpContentH;

    /// <summary>Key handling while help is open; swallows everything (modal).</summary>
    private bool HelpKey(int vk)
    {
        float view = _helpCard.Bottom - _helpCard.Top - 58f;
        float max = MathF.Max(0f, _helpContentH - view);
        switch (vk)
        {
            case VK_ESCAPE: case 0x70 /* F1 */: CloseHelp(); return true;
            case VK_DOWN: _helpScroll = Math.Clamp(_helpScroll + 36f, 0f, max); RequestRedraw(); return true;
            case VK_UP: _helpScroll = Math.Clamp(_helpScroll - 36f, 0f, max); RequestRedraw(); return true;
            case VK_NEXT: _helpScroll = Math.Clamp(_helpScroll + view, 0f, max); RequestRedraw(); return true;
            case VK_PRIOR: _helpScroll = Math.Clamp(_helpScroll - view, 0f, max); RequestRedraw(); return true;
            case VK_HOME: _helpScroll = 0f; RequestRedraw(); return true;
            case VK_END: _helpScroll = max; RequestRedraw(); return true;
        }
        return true;
    }
}
