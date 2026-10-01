using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AdvancedViewer;

/// <summary>
/// One Lister child window: a hex view over a memory-mapped file, drawn with an
/// ID2D1HwndRenderTarget + DirectWrite. Per-window state lives in this managed object,
/// reached from the WndProc through a GCHandle stored in GWLP_USERDATA.
/// </summary>
internal sealed unsafe class HexView
{
    private const string ClassName = "AdvancedViewerHexView";
    private const int BytesPerRow = 16;

    // Window messages / keys (kept local instead of generating hundreds of constants).
    private const uint WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082, WM_DESTROY = 0x0002,
        WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100,
        WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_SYSCHAR = 0x0106, WM_VSCROLL = 0x0115,
        WM_MOUSEWHEEL = 0x020A, WM_LBUTTONDOWN = 0x0201, WM_DPICHANGED_AFTERPARENT = 0x02E3;
    private const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_UP = 0x26, VK_DOWN = 0x28;
    private const int SB_LINEUP = 0, SB_LINEDOWN = 1, SB_PAGEUP = 2, SB_PAGEDOWN = 3,
        SB_THUMBPOSITION = 4, SB_THUMBTRACK = 5, SB_TOP = 6, SB_BOTTOM = 7;

    private static bool s_classRegistered;
    // GCHandle values currently stored in GWLP_USERDATA, to detect a host overwriting it.
    private static readonly HashSet<nint> s_liveHandles = new();

    private HWND _hwnd;
    private MappedFile? _file;
    private string _path = "";
    private long _topRow;
    private int _clientW, _clientH; // pixels
    private float _dpiScale = 1f;   // pixels per DIP
    private int _wheelRemainder;
    private bool _handleFreed;

    private ID2D1HwndRenderTarget* _rt;
    private ID2D1SolidColorBrush* _textBrush;
    private ID2D1SolidColorBrush* _offsetBrush;

    private char[] _offsetText = Array.Empty<char>();
    private char[] _bodyText = Array.Empty<char>();

    // First-paint timing for ListLoadW / ListLoadNextW.
    private long _pendingStart;
    private string? _pendingEvent;

    public string Path => _path;

    // ---------------------------------------------------------------- creation / loading

    /// <summary>Opens the file and creates the child window. Returns 0 if the file cannot be opened.</summary>
    public static nint Create(nint parent, string path, long startTicks, string evt)
    {
        MappedFile? file = MappedFile.Open(path, out string? error);
        if (file == null)
        {
            Log.Write(evt + ":open-failed", path, Log.ElapsedUs(startTicks), error);
            return 0;
        }

        Graphics.EnsureInitialized();
        EnsureClassRegistered();

        var view = new HexView { _file = file, _path = path, _pendingStart = startTicks, _pendingEvent = evt };
        GCHandle gch = GCHandle.Alloc(view);
        nint gchPtr = GCHandle.ToIntPtr(gch);

        RECT rc = default;
        PInvoke.GetClientRect((HWND)parent, &rc);

        HWND hwnd;
        fixed (char* cls = ClassName)
        {
            hwnd = PInvoke.CreateWindowEx(
                0, cls, null,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_VSCROLL,
                0, 0, rc.right - rc.left, rc.bottom - rc.top,
                (HWND)parent, HMENU.Null, ModuleInstance, (void*)gchPtr);
        }
        if (hwnd == HWND.Null)
        {
            int err = Marshal.GetLastPInvokeError();
            // WM_NCDESTROY frees the handle if the window got that far; otherwise free it here.
            if (!view._handleFreed) { s_liveHandles.Remove(gchPtr); gch.Free(); }
            file.Dispose();
            throw new InvalidOperationException("CreateWindowExW failed, Win32 error " + err);
        }
        return hwnd;
    }

    /// <summary>ListLoadNextW: swap in a new file in the same window. Returns false if it cannot be opened (old file kept).</summary>
    public bool LoadNext(string path, long startTicks, string evt)
    {
        MappedFile? file = MappedFile.Open(path, out string? error);
        if (file == null)
        {
            Log.Write(evt + ":open-failed", path, Log.ElapsedUs(startTicks), error);
            return false;
        }
        _file?.Dispose();
        _file = file;
        _path = path;
        _topRow = 0;
        _wheelRemainder = 0;
        _pendingStart = startTicks;
        _pendingEvent = evt;
        UpdateScrollBar();
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
        return true;
    }

    public static HexView? FromHwnd(nint hwnd)
    {
        nint p = PInvoke.GetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
        if (p == 0) return null;
        if (!s_liveHandles.Contains(p))
        {
            Log.Write("GWLP_USERDATA-mismatch", null, -1, $"hwnd=0x{hwnd:X} value=0x{p:X}");
            return null;
        }
        return GCHandle.FromIntPtr(p).Target as HexView;
    }

    private static HINSTANCE s_module;
    private static HINSTANCE ModuleInstance
    {
        get
        {
            if (s_module == HINSTANCE.Null)
            {
                // Handle of this DLL, found from the address of one of its functions.
                // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT:
                // deliberately NOT pinned, so the spike can observe whether TC unloads the plugin.
                HMODULE mod;
                delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT> fn = &WndProc;
                if (!PInvoke.GetModuleHandleEx(0x4 | 0x2, (char*)fn, &mod))
                    throw new InvalidOperationException("GetModuleHandleExW failed, Win32 error " + Marshal.GetLastPInvokeError());
                s_module = (HINSTANCE)(nint)mod.Value;
            }
            return s_module;
        }
    }

    private static void EnsureClassRegistered()
    {
        if (s_classRegistered) return;
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = ModuleInstance,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, (char*)32512 /* IDC_ARROW */),
                lpszClassName = cls,
            };
            if (PInvoke.RegisterClassEx(&wc) == 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                    throw new InvalidOperationException("RegisterClassExW failed, Win32 error " + err);
            }
        }
        s_classRegistered = true;
    }

    // ---------------------------------------------------------------- window procedure

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        long t0 = 0;
        try
        {
            if (msg == WM_NCCREATE)
            {
                var cs = (CREATESTRUCTW*)lParam.Value;
                nint gch = (nint)cs->lpCreateParams;
                PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, gch);
                s_liveHandles.Add(gch);
                if (GCHandle.FromIntPtr(gch).Target is HexView v) v._hwnd = hwnd;
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
            }

            nint p = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
            HexView? view = p != 0 && s_liveHandles.Contains(p) ? GCHandle.FromIntPtr(p).Target as HexView : null;
            if (view == null)
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

            switch (msg)
            {
                case WM_PAINT:
                    t0 = Log.Now();
                    view.OnPaint();
                    return (LRESULT)0;
                case WM_ERASEBKGND:
                    return (LRESULT)1;
                case WM_SIZE:
                    view.OnSize((int)(lParam.Value & 0xFFFF), (int)((lParam.Value >> 16) & 0xFFFF));
                    return (LRESULT)0;
                case WM_DPICHANGED_AFTERPARENT:
                    view.DiscardTarget();
                    view.OnSize(view._clientW, view._clientH);
                    return (LRESULT)0;
                case WM_KEYDOWN:
                    if (view.OnKeyDown((int)wParam.Value))
                        return (LRESULT)0;
                    PInvoke.PostMessage(PInvoke.GetParent(hwnd), msg, wParam, lParam);
                    return (LRESULT)0;
                case WM_SYSKEYDOWN:
                case WM_CHAR:
                    PInvoke.PostMessage(PInvoke.GetParent(hwnd), msg, wParam, lParam);
                    return (LRESULT)0;
                case WM_VSCROLL:
                    view.OnVScroll((int)(wParam.Value & 0xFFFF));
                    return (LRESULT)0;
                case WM_MOUSEWHEEL:
                    view.OnWheel((short)((wParam.Value >> 16) & 0xFFFF));
                    return (LRESULT)0;
                case WM_LBUTTONDOWN:
                    PInvoke.SetFocus(hwnd);
                    return (LRESULT)0;
                case WM_DESTROY:
                    view.OnDestroy();
                    return (LRESULT)0;
                case WM_NCDESTROY:
                    PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
                    s_liveHandles.Remove(p);
                    view._handleFreed = true;
                    GCHandle.FromIntPtr(p).Free();
                    return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
            }
            return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Exception("WndProc msg=0x" + msg.ToString("X4"), null, t0, ex);
            if (msg == WM_PAINT)
                PInvoke.ValidateRect(hwnd, (RECT*)null); // avoid an endless WM_PAINT storm
            return (LRESULT)0;
        }
    }

    // ---------------------------------------------------------------- input

    private bool OnKeyDown(int vk)
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

    private void OnWheel(short delta)
    {
        uint lines = 3;
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
        _wheelRemainder += delta;
        int notches = _wheelRemainder / 120;
        if (notches == 0) return;
        _wheelRemainder -= notches * 120;
        long step = lines == uint.MaxValue /* WHEEL_PAGESCROLL */ ? Math.Max(1, FullRows - 1) : lines;
        ScrollTo(_topRow - notches * step);
    }

    private void OnVScroll(int code)
    {
        long page = Math.Max(1, FullRows - 1);
        switch (code)
        {
            case SB_LINEUP: ScrollTo(_topRow - 1); break;
            case SB_LINEDOWN: ScrollTo(_topRow + 1); break;
            case SB_PAGEUP: ScrollTo(_topRow - page); break;
            case SB_PAGEDOWN: ScrollTo(_topRow + page); break;
            case SB_TOP: ScrollTo(0); break;
            case SB_BOTTOM: ScrollTo(long.MaxValue); break;
            case SB_THUMBTRACK:
            case SB_THUMBPOSITION:
            {
                var si = new SCROLLINFO { cbSize = (uint)sizeof(SCROLLINFO), fMask = SCROLLINFO_MASK.SIF_TRACKPOS };
                PInvoke.GetScrollInfo(_hwnd, SCROLLBAR_CONSTANTS.SB_VERT, &si);
                ScrollTo(ScrollPosToRow(si.nTrackPos));
                break;
            }
        }
    }

    // ---------------------------------------------------------------- scrolling model

    private long FileSize => _file?.Size ?? 0;
    private long TotalRows => (FileSize + BytesPerRow - 1) / BytesPerRow;
    private float ClientHeightDip => _clientH / _dpiScale;
    private long FullRows => Math.Max(1, (long)(ClientHeightDip / Graphics.LineHeight));
    private long VisibleRows => Math.Max(1, (long)Math.Ceiling(ClientHeightDip / Graphics.LineHeight));
    private long MaxTopRow => Math.Max(0, TotalRows - FullRows);

    private void ScrollTo(long row)
    {
        long clamped = Math.Clamp(row, 0, MaxTopRow);
        if (clamped == _topRow) return;
        _topRow = clamped;
        UpdateScrollBar();
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
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
        if (_hwnd == HWND.Null) return;
        var si = new SCROLLINFO
        {
            cbSize = (uint)sizeof(SCROLLINFO),
            fMask = SCROLLINFO_MASK.SIF_RANGE | SCROLLINFO_MASK.SIF_PAGE | SCROLLINFO_MASK.SIF_POS | SCROLLINFO_MASK.SIF_DISABLENOSCROLL,
            nMin = 0,
        };
        long full = FullRows;
        if (!ScaledScroll)
        {
            si.nMax = (int)Math.Max(0, TotalRows - 1);
            si.nPage = (uint)Math.Min(full, int.MaxValue);
            si.nPos = (int)_topRow;
        }
        else
        {
            uint page = ScaledPage(full);
            long posRange = (long)int.MaxValue - page + 1;
            long maxTop = MaxTopRow;
            si.nMax = int.MaxValue;
            si.nPage = page;
            si.nPos = maxTop == 0 ? 0 : (int)((Int128)_topRow * posRange / maxTop);
        }
        PInvoke.SetScrollInfo(_hwnd, SCROLLBAR_CONSTANTS.SB_VERT, &si, true);
    }

    private void OnSize(int w, int h)
    {
        _clientW = w;
        _clientH = h;
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        _dpiScale = dpi == 0 ? 1f : dpi / 96f;
        if (_rt != null)
        {
            var size = new D2D_SIZE_U { width = (uint)Math.Max(w, 1), height = (uint)Math.Max(h, 1) };
            _rt->Resize(&size);
        }
        _topRow = Math.Clamp(_topRow, 0, MaxTopRow);
        UpdateScrollBar();
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// Spike knob: VIEWER_SPIKE_RT=software selects the software (CPU rasterizer) Direct2D target,
    /// to compare first-paint cost against the default (hardware, D3D device per target).
    /// </summary>
    private static readonly D2D1_RENDER_TARGET_TYPE s_rtType =
        string.Equals(Environment.GetEnvironmentVariable("VIEWER_SPIKE_RT"), "software", StringComparison.OrdinalIgnoreCase)
            ? D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE
            : D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_DEFAULT;

    private void EnsureTarget()
    {
        if (_rt != null) return;
        long t0 = Log.Now();
        uint dpi = PInvoke.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        _dpiScale = dpi / 96f;

        RECT rc;
        PInvoke.GetClientRect(_hwnd, &rc);
        _clientW = rc.right - rc.left;
        _clientH = rc.bottom - rc.top;

        var rtProps = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = s_rtType,
            dpiX = dpi,
            dpiY = dpi,
        };
        var hwndProps = new D2D1_HWND_RENDER_TARGET_PROPERTIES
        {
            hwnd = _hwnd,
            pixelSize = new D2D_SIZE_U { width = (uint)Math.Max(_clientW, 1), height = (uint)Math.Max(_clientH, 1) },
            // IMMEDIATELY: EndDraw does not wait for vblank, so paint timings measure our work, not vsync.
            presentOptions = D2D1_PRESENT_OPTIONS.D2D1_PRESENT_OPTIONS_IMMEDIATELY,
        };
        ID2D1HwndRenderTarget* rt = null;
        Graphics.D2DFactory->CreateHwndRenderTarget(&rtProps, &hwndProps, &rt);
        _rt = rt;
        _rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE);

        var black = new D2D1_COLOR_F { r = 0f, g = 0f, b = 0f, a = 1f };
        var gray = new D2D1_COLOR_F { r = 0.35f, g = 0.35f, b = 0.55f, a = 1f };
        ID2D1SolidColorBrush* b1 = null, b2 = null;
        _rt->CreateSolidColorBrush(&black, null, &b1);
        _rt->CreateSolidColorBrush(&gray, null, &b2);
        _textBrush = b1;
        _offsetBrush = b2;
        Log.Write("CreateRenderTarget", null, Log.ElapsedUs(t0), s_rtType == D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE ? "type=software" : "type=default");
    }

    public void DiscardTarget()
    {
        if (_textBrush != null) { _textBrush->Release(); _textBrush = null; }
        if (_offsetBrush != null) { _offsetBrush->Release(); _offsetBrush = null; }
        if (_rt != null) { _rt->Release(); _rt = null; }
    }

    private void OnPaint()
    {
        long t0 = Log.Now();
        int gc0 = Diag.GcCount();
        PAINTSTRUCT ps;
        PInvoke.BeginPaint(_hwnd, &ps);
        try
        {
            EnsureTarget();
            int digits = FileSize >= 0x1_0000_0000L ? 16 : 8;
            int rows = (int)Math.Min(VisibleRows, Math.Max(0, TotalRows - _topRow));
            int offLen = FormatRows(rows, digits);

            _rt->BeginDraw();
            var white = new D2D1_COLOR_F { r = 1f, g = 1f, b = 1f, a = 1f };
            _rt->Clear(&white);

            if (rows > 0)
            {
                float x0 = 4f;
                float w = _clientW / _dpiScale;
                float h = _clientH / _dpiScale;
                float offW = (digits + 2) * Graphics.CellWidth;
                var offRect = new D2D_RECT_F { left = x0, top = 0, right = x0 + offW, bottom = h };
                var bodyRect = new D2D_RECT_F { left = x0 + offW, top = 0, right = Math.Max(x0 + offW + 1, w), bottom = h };
                fixed (char* po = _offsetText)
                fixed (char* pb = _bodyText)
                {
                    _rt->DrawText(po, (uint)offLen, Graphics.TextFormat, &offRect, (ID2D1Brush*)_offsetBrush,
                        D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
                    _rt->DrawText(pb, (uint)(rows * BodyCharsPerRow), Graphics.TextFormat, &bodyRect, (ID2D1Brush*)_textBrush,
                        D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
                }
            }

            HRESULT hr = _rt->EndDraw(null, null);
            if (hr == HRESULT.D2DERR_RECREATE_TARGET)
            {
                Log.Write("D2DERR_RECREATE_TARGET", _path, -1);
                DiscardTarget();
                PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
            }
            else
            {
                hr.ThrowOnFailure();
            }
        }
        finally
        {
            PInvoke.EndPaint(_hwnd, &ps);
        }

        int gcs = Diag.GcCount() - gc0;
        // Allocation-free unless a GC ran during this paint (then the line says how many).
        Log.Write("WM_PAINT", null, Log.ElapsedUs(t0), gcs == 0 ? "top=" : "gc=" + gcs + " top=", _topRow, hasNum: true);
        if (_pendingEvent != null)
        {
            Log.Write(FirstPaintEvent(_pendingEvent), _path, Log.ElapsedUs(_pendingStart));
            _pendingEvent = null;
        }
    }

    private static string FirstPaintEvent(string evt) => evt switch
    {
        "ListLoadW" => "ListLoadW->firstpaint",
        "ListLoad" => "ListLoad->firstpaint",
        "ListLoadNextW" => "ListLoadNextW->firstpaint",
        "ListLoadNext" => "ListLoadNext->firstpaint",
        _ => evt + "->firstpaint",
    };

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

    private void OnDestroy()
    {
        DiscardTarget();
        _file?.Dispose();
        _file = null;
    }

#if SPIKE_THROW
    public static bool IsThrowFile(string path) =>
        path.EndsWith("\\__throw__.bin", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("__throw__.bin", StringComparison.OrdinalIgnoreCase);
#endif
}
