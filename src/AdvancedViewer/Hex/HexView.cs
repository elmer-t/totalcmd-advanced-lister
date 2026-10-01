using System;
using AdvancedViewer.Hosting;
using Windows.Win32;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AdvancedViewer.Hex;

/// <summary>
/// The spike's hex view over a memory-mapped file, as a <see cref="View"/>: 16 bytes per row,
/// offset column + hex + ASCII, drawn with two DrawText calls per paint from reused char
/// buffers (allocation-free). Scrolling is row based; the window, render target, DPI and key
/// forwarding live in <see cref="ViewWindow"/>.
/// </summary>
internal sealed unsafe class HexView : View
{
    private const int BytesPerRow = 16;

    private const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_UP = 0x26, VK_DOWN = 0x28;
    private const int SB_LINEUP = 0, SB_LINEDOWN = 1, SB_PAGEUP = 2, SB_PAGEDOWN = 3,
        SB_THUMBPOSITION = 4, SB_THUMBTRACK = 5, SB_TOP = 6, SB_BOTTOM = 7;

    private MappedFile? _file;
    private long _topRow;

    private ID2D1SolidColorBrush* _textBrush;
    private ID2D1SolidColorBrush* _offsetBrush;

    private char[] _offsetText = Array.Empty<char>();
    private char[] _bodyText = Array.Empty<char>();

    public override long ScrollPosition => _topRow;

    // ---------------------------------------------------------------- loading

    public override bool Load(string path, int showFlags, out string? error)
    {
        MappedFile? file = MappedFile.Open(path, out error);
        if (file == null) return false;
        _file?.Dispose();
        _file = file;
        Path = path;
        _topRow = 0;
        UpdateScrollBar();
        return true;
    }

    public override void Unload()
    {
        _file?.Dispose();
        _file = null;
    }

    // ---------------------------------------------------------------- input

    public override bool OnKeyDown(int vk, bool ctrl, bool shift)
    {
        long page = Math.Max(1, FullRows - 1);
        switch (vk)
        {
            case VK_UP: ScrollTo(_topRow - 1); return true;
            case VK_DOWN: ScrollTo(_topRow + 1); return true;
            case VK_PRIOR: ScrollTo(_topRow - page); return true;
            case VK_NEXT: ScrollTo(_topRow + page); return true;
            // Home/End and Ctrl+Home/Ctrl+End: there is no caret, so both go to the start / end of the file.
            case VK_HOME: ScrollTo(0); return true;
            case VK_END: ScrollTo(long.MaxValue); return true;
        }
        return false;
    }

    public override void OnMouseWheel(int notches, bool ctrl)
    {
        uint lines = 3;
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
        long step = lines == uint.MaxValue /* WHEEL_PAGESCROLL */ ? Math.Max(1, FullRows - 1) : lines;
        ScrollTo(_topRow - notches * step);
    }

    public override void OnVScroll(int sbCode, int trackPos)
    {
        long page = Math.Max(1, FullRows - 1);
        switch (sbCode)
        {
            case SB_LINEUP: ScrollTo(_topRow - 1); break;
            case SB_LINEDOWN: ScrollTo(_topRow + 1); break;
            case SB_PAGEUP: ScrollTo(_topRow - page); break;
            case SB_PAGEDOWN: ScrollTo(_topRow + page); break;
            case SB_TOP: ScrollTo(0); break;
            case SB_BOTTOM: ScrollTo(long.MaxValue); break;
            case SB_THUMBTRACK:
            case SB_THUMBPOSITION:
                ScrollTo(ScrollPosToRow(trackPos));
                break;
        }
    }

    // ---------------------------------------------------------------- scrolling model

    private long FileSize => _file?.Size ?? 0;
    private long TotalRows => (FileSize + BytesPerRow - 1) / BytesPerRow;
    private float ClientHeightDip => Window.ClientHeightPx / Window.DpiScale;
    private long FullRows => Math.Max(1, (long)(ClientHeightDip / Graphics.LineHeight));
    private long VisibleRows => Math.Max(1, (long)Math.Ceiling(ClientHeightDip / Graphics.LineHeight));
    private long MaxTopRow => Math.Max(0, TotalRows - FullRows);

    private void ScrollTo(long row)
    {
        long clamped = Math.Clamp(row, 0, MaxTopRow);
        if (clamped == _topRow) return;
        _topRow = clamped;
        UpdateScrollBar();
        Window.Invalidate();
    }

    // Win32 scroll positions are 32-bit. Files with <= int.MaxValue rows map 1:1;
    // larger files (> 32 GB) map the row range linearly onto 0..int.MaxValue.
    private bool ScaledScroll => TotalRows > int.MaxValue;

    private uint ScaledPage(long fullRows) =>
        (uint)Math.Max(1, (long)((Int128)fullRows * int.MaxValue / TotalRows));

    private long ScrollPosToRow(int pos)
    {
        if (!ScaledScroll) return pos;
        long posRange = (long)int.MaxValue - ScaledPage(FullRows) + 1;
        if (posRange <= 0) return 0;
        return (long)((Int128)pos * MaxTopRow / posRange);
    }

    private void UpdateScrollBar()
    {
        long full = FullRows;
        if (!ScaledScroll)
        {
            Window.SetScrollInfoRaw((int)Math.Max(0, TotalRows - 1), (uint)Math.Min(full, int.MaxValue), (int)_topRow);
        }
        else
        {
            uint page = ScaledPage(full);
            long posRange = (long)int.MaxValue - page + 1;
            long maxTop = MaxTopRow;
            Window.SetScrollInfoRaw(int.MaxValue, page, maxTop == 0 ? 0 : (int)((Int128)_topRow * posRange / maxTop));
        }
    }

    public override void OnSize(int widthPx, int heightPx, float dpiScale)
    {
        _topRow = Math.Clamp(_topRow, 0, MaxTopRow);
        UpdateScrollBar();
    }

    // ---------------------------------------------------------------- rendering

    public override void SetTheme(Theme theme) => OnDeviceLost(); // brushes are recreated with the new colors

    public override void OnDeviceLost()
    {
        if (_textBrush != null) { _textBrush->Release(); _textBrush = null; }
        if (_offsetBrush != null) { _offsetBrush->Release(); _offsetBrush = null; }
    }

    private void EnsureBrushes(ID2D1RenderTarget* rt, Theme theme)
    {
        if (_textBrush != null) return;
        // Light: the spike's exact colors (black text, blue-gray offsets). Dark: theme colors.
        D2D1_COLOR_F text = theme.IsDark ? theme.Text : new D2D1_COLOR_F { r = 0f, g = 0f, b = 0f, a = 1f };
        D2D1_COLOR_F offset = theme.IsDark ? theme.MutedText : new D2D1_COLOR_F { r = 0.35f, g = 0.35f, b = 0.55f, a = 1f };
        ID2D1SolidColorBrush* b1 = null, b2 = null;
        rt->CreateSolidColorBrush(&text, null, &b1);
        rt->CreateSolidColorBrush(&offset, null, &b2);
        _textBrush = b1;
        _offsetBrush = b2;
    }

    public override void OnPaint(ID2D1RenderTarget* rt, Theme theme)
    {
        EnsureBrushes(rt, theme);
        int digits = FileSize >= 0x1_0000_0000L ? 16 : 8;
        int rows = (int)Math.Min(VisibleRows, Math.Max(0, TotalRows - _topRow));
        int offLen = FormatRows(rows, digits);
        if (rows <= 0) return;

        float x0 = 4f;
        float w = Window.ClientWidthPx / Window.DpiScale;
        float h = Window.ClientHeightPx / Window.DpiScale;
        float offW = (digits + 2) * Graphics.CellWidth;
        var offRect = new D2D_RECT_F { left = x0, top = 0, right = x0 + offW, bottom = h };
        var bodyRect = new D2D_RECT_F { left = x0 + offW, top = 0, right = Math.Max(x0 + offW + 1, w), bottom = h };
        fixed (char* po = _offsetText)
        fixed (char* pb = _bodyText)
        {
            rt->DrawText(po, (uint)offLen, Graphics.TextFormat, &offRect, (ID2D1Brush*)_offsetBrush,
                D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
            rt->DrawText(pb, (uint)(rows * BodyCharsPerRow), Graphics.TextFormat, &bodyRect, (ID2D1Brush*)_textBrush,
                D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
        }
    }

    // Body row: "XX XX XX XX XX XX XX XX  XX XX XX XX XX XX XX XX  ................\n"
    private const int BodyCharsPerRow = 8 * 3 + 1 + 8 * 3 + 1 + 16 + 1;
    private static ReadOnlySpan<char> HexDigits => "0123456789ABCDEF";

    /// <summary>Formats the visible rows into _offsetText / _bodyText. Returns the offset text length.</summary>
    private int FormatRows(int rows, int digits)
    {
        int offPerRow = digits + 1;
        if (_offsetText.Length < rows * offPerRow) _offsetText = new char[rows * offPerRow + 64];
        if (_bodyText.Length < rows * BodyCharsPerRow) _bodyText = new char[rows * BodyCharsPerRow + 64];

        byte* data = _file != null ? _file.Data : null;
        long size = FileSize;
        ReadOnlySpan<char> hex = HexDigits;
        int o = 0, b = 0;
        for (int r = 0; r < rows; r++)
        {
            long offset = (_topRow + r) * BytesPerRow;
            for (int d = digits - 1; d >= 0; d--)
                _offsetText[o++] = hex[(int)((offset >> (d * 4)) & 0xF)];
            _offsetText[o++] = '\n';

            int n = (int)Math.Min(BytesPerRow, size - offset);
            int asciiStart = b + 8 * 3 + 1 + 8 * 3 + 1;
            for (int i = 0; i < BytesPerRow; i++)
            {
                if (i == 8) _bodyText[b++] = ' ';
                if (i < n)
                {
                    byte v = data[offset + i];
                    _bodyText[b++] = hex[v >> 4];
                    _bodyText[b++] = hex[v & 0xF];
                    _bodyText[asciiStart + i] = v >= 0x20 && v < 0x7F ? (char)v : '.';
                }
                else
                {
                    _bodyText[b++] = ' ';
                    _bodyText[b++] = ' ';
                    _bodyText[asciiStart + i] = ' ';
                }
                _bodyText[b++] = ' ';
            }
            _bodyText[b++] = ' ';
            b += BytesPerRow; // ASCII column already written
            _bodyText[b++] = '\n';
        }
        return o;
    }

#if SPIKE_THROW
    public static bool IsThrowFile(string path) =>
        path.EndsWith("\\__throw__.bin", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("__throw__.bin", StringComparison.OrdinalIgnoreCase);
#endif
}
