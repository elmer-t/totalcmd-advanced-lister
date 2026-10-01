using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AdvancedViewer.Hosting;

/// <summary>
/// One Lister child window. Owns the HWND, the GCHandle in GWLP_USERDATA, the
/// ID2D1HwndRenderTarget, DPI, the scroll bar, key forwarding, the WM_APP_LAYOUT idle pump and
/// the first-paint log line. Owns exactly one <see cref="View"/> at a time, which draws the
/// content. All calls happen on TC's UI thread; the WndProc catches everything.
/// </summary>
internal sealed unsafe class ViewWindow
{
    private const string ClassName = "AdvancedViewerWindow";

    // Window messages (kept local instead of generating hundreds of constants).
    private const uint WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082, WM_DESTROY = 0x0002,
        WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020,
        WM_KEYDOWN = 0x0100, WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_TIMER = 0x0113,
        WM_VSCROLL = 0x0115, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_MOUSEWHEEL = 0x020A,
        WM_DPICHANGED_AFTERPARENT = 0x02E3, WM_APP = 0x8000;

    /// <summary>Posted to the window itself to run <see cref="View.DoIdleWork"/>.</summary>
    public const uint WM_APP_LAYOUT = WM_APP + 1;

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    private const int SB_THUMBPOSITION = 4, SB_THUMBTRACK = 5;
    private const int HTCLIENT = 1;
    private const nuint IdleTimerId = 1;
    /// <summary>Time slice for one DoIdleWork call.</summary>
    private const long IdleSliceUs = 6000;
    // QS_KEY | QS_MOUSEMOVE | QS_MOUSEBUTTON | QS_PAINT | QS_RAWINPUT
    private const uint QS_YIELD_MASK = 0x0001 | 0x0002 | 0x0004 | 0x0020 | 0x0400;

    private static bool s_classRegistered;
    // GCHandle values currently stored in GWLP_USERDATA, to detect a host overwriting it.
    private static readonly HashSet<nint> s_liveHandles = new();

    private ID2D1HwndRenderTarget* _rt;
    private int _wheelRemainder;
    private bool _handleFreed;
    private bool _sizeNotified;
    private bool _idlePending;      // WM_APP_LAYOUT posted or idle timer running
    private bool _idleRequested;    // RequestIdleWork before the HWND existed
    private bool _viewCursor;       // the view set a custom cursor on the last mouse move

    // First-paint timing for ListLoadW / ListLoadNextW.
    private long _pendingStart;
    private string? _pendingEvent;

    public HWND Hwnd { get; private set; }
    public View View { get; private set; }
    public Theme Theme { get; private set; }
    /// <summary>Pixels per DIP (window DPI / 96).</summary>
    public float DpiScale { get; private set; } = 1f;
    public int ClientWidthPx { get; private set; }
    public int ClientHeightPx { get; private set; }

    private ViewWindow(View view, Theme theme)
    {
        View = view;
        Theme = theme;
    }

    // ---------------------------------------------------------------- creation / loading

    /// <summary>
    /// Loads <paramref name="path"/> into <paramref name="view"/> and creates the child window.
    /// Returns 0 if the view cannot load the file (no window is created then).
    /// </summary>
    public static nint Create(nint parent, View view, string path, int showFlags, long startTicks, string evt)
    {
        Graphics.EnsureInitialized();
        EnsureClassRegistered();

        RECT rc = default;
        PInvoke.GetClientRect((HWND)parent, &rc);
        uint dpi = PInvoke.GetDpiForWindow((HWND)parent);

        var win = new ViewWindow(view, Theme.FromShowFlags(showFlags))
        {
            ClientWidthPx = rc.right - rc.left,
            ClientHeightPx = rc.bottom - rc.top,
            DpiScale = dpi == 0 ? 1f : dpi / 96f,
            _pendingStart = startTicks,
            _pendingEvent = evt,
        };
        view.Attach(win);
        if (!view.Load(path, showFlags, out string? error))
        {
            Log.Write(evt + ":open-failed", path, Log.ElapsedUs(startTicks), error);
            view.Unload();
            return 0;
        }

        GCHandle gch = GCHandle.Alloc(win);
        nint gchPtr = GCHandle.ToIntPtr(gch);

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
            if (!win._handleFreed) { s_liveHandles.Remove(gchPtr); gch.Free(); }
            view.Unload();
            throw new InvalidOperationException("CreateWindowExW failed, Win32 error " + err);
        }
        win.ApplyScrollBarTheme();
        if (!win._sizeNotified) win.NotifySize();
        if (win._idleRequested) { win._idleRequested = false; win.RequestIdleWork(); }
        return hwnd;
    }

    /// <summary>
    /// ListLoadNextW: shows <paramref name="path"/> in this window using <paramref name="view"/>,
    /// which is either the current view or a fresh one (file of another kind). Returns false if
    /// it cannot be loaded; the window then keeps showing the old file.
    /// </summary>
    public bool LoadNext(View view, string path, int showFlags, long startTicks, string evt)
    {
        bool swap = !ReferenceEquals(view, View);
        if (swap) view.Attach(this);
        Theme theme = Theme.FromShowFlags(showFlags);
        if (!view.Load(path, showFlags, out string? error))
        {
            Log.Write(evt + ":open-failed", path, Log.ElapsedUs(startTicks), error);
            if (swap) view.Unload();
            return false;
        }
        if (swap)
        {
            View old = View;
            old.OnDeviceLost();
            old.Unload();
            View = view;
            _viewCursor = false;
            bool themeChanged = !ReferenceEquals(theme, Theme);
            Theme = theme;
            view.SetTheme(theme);
            if (themeChanged) ApplyScrollBarTheme();
            view.OnSize(ClientWidthPx, ClientHeightPx, DpiScale);
        }
        else if (!ReferenceEquals(theme, Theme))
        {
            Theme = theme;
            view.SetTheme(theme);
            ApplyScrollBarTheme();
        }
        _wheelRemainder = 0;
        _pendingStart = startTicks;
        _pendingEvent = evt;
        Invalidate();
        return true;
    }

    /// <summary>The ViewWindow behind a plugin HWND, or null (logs a GWLP_USERDATA mismatch).</summary>
    public static ViewWindow? FromHwnd(nint hwnd)
    {
        nint p = PInvoke.GetWindowLongPtr((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
        if (p == 0) return null;
        if (!s_liveHandles.Contains(p))
        {
            Log.Write("GWLP_USERDATA-mismatch", null, -1, $"hwnd=0x{hwnd:X} value=0x{p:X}");
            return null;
        }
        return GCHandle.FromIntPtr(p).Target as ViewWindow;
    }

    // ---------------------------------------------------------------- services for views

    public void Invalidate()
    {
        if (Hwnd != HWND.Null) PInvoke.InvalidateRect(Hwnd, (RECT*)null, false);
    }

    /// <summary>Scroll bar 0..totalDip-1 with page pageDip and position posDip (units are the view's choice).</summary>
    public void SetScrollRange(int totalDip, int pageDip, int posDip) =>
        SetScrollInfoRaw(Math.Max(0, totalDip - 1), (uint)Math.Max(0, pageDip), posDip);

    /// <summary>Raw SCROLLINFO (nMin=0, nMax, nPage, nPos) with SIF_DISABLENOSCROLL.</summary>
    public void SetScrollInfoRaw(int max, uint page, int pos)
    {
        if (Hwnd == HWND.Null) return;
        var si = new SCROLLINFO
        {
            cbSize = (uint)sizeof(SCROLLINFO),
            fMask = SCROLLINFO_MASK.SIF_RANGE | SCROLLINFO_MASK.SIF_PAGE | SCROLLINFO_MASK.SIF_POS | SCROLLINFO_MASK.SIF_DISABLENOSCROLL,
            nMin = 0,
            nMax = max,
            nPage = page,
            nPos = pos,
        };
        PInvoke.SetScrollInfo(Hwnd, SCROLLBAR_CONSTANTS.SB_VERT, &si, true);
    }

    /// <summary>Asks for one <see cref="View.DoIdleWork"/> call soon (coalesced; safe before the HWND exists).</summary>
    public void RequestIdleWork()
    {
        if (Hwnd == HWND.Null) { _idleRequested = true; return; }
        if (_idlePending) return;
        _idlePending = PInvoke.PostMessage(Hwnd, WM_APP_LAYOUT, default, default);
    }

    /// <summary>Sets the hand cursor (for views returning true from OnMouseMove).</summary>
    public static void SetHandCursor() => PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, (char*)32649 /* IDC_HAND */));

    public static void SetArrowCursor() => PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, (char*)32512 /* IDC_ARROW */));

    /// <summary>Runtime theme switch (lc_newparams / dark mode toggle in TC).</summary>
    public void SetTheme(Theme theme)
    {
        if (ReferenceEquals(theme, Theme)) return;
        Theme = theme;
        View.SetTheme(theme);
        ApplyScrollBarTheme();
        Invalidate();
    }

    /// <summary>
    /// Themes the window's standard (non-client) scroll bar to match <see cref="Theme"/>.
    /// Windows 10 1809+ draws a window's scroll bar from its theme class: "DarkMode_Explorer"
    /// gives the dark scroll bar that Explorer and TC's own dark mode use, "Explorer" the light
    /// one. Harmless on older systems (the call just fails). SetWindowTheme repaints the frame.
    /// </summary>
    private void ApplyScrollBarTheme()
    {
        if (Hwnd == HWND.Null) return;
        fixed (char* app = Theme.IsDark ? "DarkMode_Explorer" : "Explorer")
        {
            HRESULT hr = PInvoke.SetWindowTheme(Hwnd, app, null);
            if (hr.Failed) Log.Write("SetWindowTheme", null, -1, "hr=0x" + ((uint)hr.Value).ToString("X8"));
        }
    }

    // ---------------------------------------------------------------- class / module

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
                if (GCHandle.FromIntPtr(gch).Target is ViewWindow w) w.Hwnd = hwnd;
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
            }

            nint p = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
            ViewWindow? win = p != 0 && s_liveHandles.Contains(p) ? GCHandle.FromIntPtr(p).Target as ViewWindow : null;
            if (win == null)
                return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

            switch (msg)
            {
                case WM_PAINT:
                    t0 = Log.Now();
                    win.OnPaint();
                    return (LRESULT)0;
                case WM_ERASEBKGND:
                    return (LRESULT)1;
                case WM_SIZE:
                    win.OnSize((int)(lParam.Value & 0xFFFF), (int)((lParam.Value >> 16) & 0xFFFF));
                    return (LRESULT)0;
                case WM_DPICHANGED_AFTERPARENT:
                    win.DiscardTarget();
                    win.OnSize(win.ClientWidthPx, win.ClientHeightPx);
                    return (LRESULT)0;
                case WM_KEYDOWN:
                    if (win.View.OnKeyDown((int)wParam.Value, PInvoke.GetKeyState(VK_CONTROL) < 0, PInvoke.GetKeyState(VK_SHIFT) < 0))
                        return (LRESULT)0;
                    PInvoke.PostMessage(PInvoke.GetParent(hwnd), msg, wParam, lParam);
                    return (LRESULT)0;
                case WM_SYSKEYDOWN:
                case WM_CHAR:
                    PInvoke.PostMessage(PInvoke.GetParent(hwnd), msg, wParam, lParam);
                    return (LRESULT)0;
                case WM_VSCROLL:
                    win.OnVScroll((int)(wParam.Value & 0xFFFF));
                    return (LRESULT)0;
                case WM_MOUSEWHEEL:
                    win.OnWheel((short)((wParam.Value >> 16) & 0xFFFF), (wParam.Value & 0x0008 /* MK_CONTROL */) != 0);
                    return (LRESULT)0;
                case WM_MOUSEMOVE:
                    win.OnMouseMove((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF));
                    return (LRESULT)0;
                case WM_SETCURSOR:
                    // Keep the cursor a view set on the last mouse move (e.g. hand over a link).
                    if (win._viewCursor && (int)(lParam.Value & 0xFFFF) == HTCLIENT)
                        return (LRESULT)1;
                    return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
                case WM_LBUTTONDOWN:
                    PInvoke.SetFocus(hwnd);
                    win.View.OnMouseDown((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF));
                    return (LRESULT)0;
                case WM_APP_LAYOUT:
                    win.RunIdleWork();
                    return (LRESULT)0;
                case WM_TIMER:
                    if (wParam.Value == IdleTimerId)
                    {
                        PInvoke.KillTimer(hwnd, IdleTimerId);
                        win.RunIdleWork();
                        return (LRESULT)0;
                    }
                    return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
                case WM_DESTROY:
                    win.OnDestroy();
                    return (LRESULT)0;
                case WM_NCDESTROY:
                    PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
                    s_liveHandles.Remove(p);
                    win._handleFreed = true;
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

    private void OnVScroll(int code)
    {
        int trackPos = 0;
        if (code == SB_THUMBTRACK || code == SB_THUMBPOSITION)
        {
            var si = new SCROLLINFO { cbSize = (uint)sizeof(SCROLLINFO), fMask = SCROLLINFO_MASK.SIF_TRACKPOS };
            PInvoke.GetScrollInfo(Hwnd, SCROLLBAR_CONSTANTS.SB_VERT, &si);
            trackPos = si.nTrackPos;
        }
        View.OnVScroll(code, trackPos);
    }

    private void OnWheel(short delta, bool ctrl)
    {
        _wheelRemainder += delta;
        int notches = _wheelRemainder / 120;
        if (notches == 0) return;
        _wheelRemainder -= notches * 120;
        View.OnMouseWheel(notches, ctrl);
    }

    private void OnMouseMove(int x, int y)
    {
        bool custom = View.OnMouseMove(x, y);
        if (!custom && _viewCursor) SetArrowCursor();
        _viewCursor = custom;
    }

    // ---------------------------------------------------------------- idle pump

    private void RunIdleWork()
    {
        _idlePending = false;
        long t0 = Log.Now();
        long deadline = t0 + IdleSliceUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000;
        bool more = View.DoIdleWork(deadline);
        Log.Write("IdleWork", null, Log.ElapsedUs(t0), more ? "more=1" : "more=0");
        if (!more) return;
        // More work left. Posted messages are retrieved before input and WM_PAINT, so an
        // unconditional re-post would starve both until all work is done. If input or a paint
        // is waiting, continue from a timer (lowest priority) instead.
        uint queued = PInvoke.GetQueueStatus((QUEUE_STATUS_FLAGS)QS_YIELD_MASK) >> 16;
        if (queued != 0)
            _idlePending = PInvoke.SetTimer(Hwnd, IdleTimerId, 1, null) != 0;
        else
            _idlePending = PInvoke.PostMessage(Hwnd, WM_APP_LAYOUT, default, default);
    }

    // ---------------------------------------------------------------- size / rendering

    private void OnSize(int w, int h)
    {
        ClientWidthPx = w;
        ClientHeightPx = h;
        uint dpi = PInvoke.GetDpiForWindow(Hwnd);
        DpiScale = dpi == 0 ? 1f : dpi / 96f;
        if (_rt != null)
        {
            var size = new D2D_SIZE_U { width = (uint)Math.Max(w, 1), height = (uint)Math.Max(h, 1) };
            _rt->Resize(&size);
        }
        NotifySize();
        Invalidate();
    }

    private void NotifySize()
    {
        _sizeNotified = true;
        View.OnSize(ClientWidthPx, ClientHeightPx, DpiScale);
    }

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
        uint dpi = PInvoke.GetDpiForWindow(Hwnd);
        if (dpi == 0) dpi = 96;
        DpiScale = dpi / 96f;

        RECT rc;
        PInvoke.GetClientRect(Hwnd, &rc);
        int w = rc.right - rc.left, h = rc.bottom - rc.top;
        if (w != ClientWidthPx || h != ClientHeightPx)
        {
            ClientWidthPx = w;
            ClientHeightPx = h;
            NotifySize();
        }

        var rtProps = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = s_rtType,
            dpiX = dpi,
            dpiY = dpi,
        };
        var hwndProps = new D2D1_HWND_RENDER_TARGET_PROPERTIES
        {
            hwnd = Hwnd,
            pixelSize = new D2D_SIZE_U { width = (uint)Math.Max(ClientWidthPx, 1), height = (uint)Math.Max(ClientHeightPx, 1) },
            // IMMEDIATELY: EndDraw does not wait for vblank, so paint timings measure our work, not vsync.
            presentOptions = D2D1_PRESENT_OPTIONS.D2D1_PRESENT_OPTIONS_IMMEDIATELY,
        };
        ID2D1HwndRenderTarget* rt = null;
        Graphics.D2DFactory->CreateHwndRenderTarget(&rtProps, &hwndProps, &rt);
        _rt = rt;
        _rt->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE);
        Log.Write("CreateRenderTarget", null, Log.ElapsedUs(t0), s_rtType == D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE ? "type=software" : "type=default");
    }

    /// <summary>Releases the render target; the view first releases what it created from it.</summary>
    private void DiscardTarget()
    {
        View.OnDeviceLost();
        if (_rt != null) { _rt->Release(); _rt = null; }
    }

    private void OnPaint()
    {
        long t0 = Log.Now();
        int gc0 = Diag.GcCount();
        PAINTSTRUCT ps;
        PInvoke.BeginPaint(Hwnd, &ps);
        try
        {
            EnsureTarget();
            _rt->BeginDraw();
            Theme theme = Theme;
            D2D1_COLOR_F bg = theme.Background;
            _rt->Clear(&bg);
            View.OnPaint((ID2D1RenderTarget*)_rt, theme);

            HRESULT hr = _rt->EndDraw(null, null);
            if (hr == HRESULT.D2DERR_RECREATE_TARGET)
            {
                Log.Write("D2DERR_RECREATE_TARGET", View.Path, -1);
                DiscardTarget();
                Invalidate();
            }
            else
            {
                hr.ThrowOnFailure();
            }
        }
        finally
        {
            PInvoke.EndPaint(Hwnd, &ps);
        }

        int gcs = Diag.GcCount() - gc0;
        // Allocation-free unless a GC ran during this paint (then the line says how many).
        Log.Write("WM_PAINT", null, Log.ElapsedUs(t0), gcs == 0 ? "top=" : "gc=" + gcs + " top=", View.ScrollPosition, hasNum: true);
        if (_pendingEvent != null)
        {
            Log.Write(FirstPaintEvent(_pendingEvent), View.Path, Log.ElapsedUs(_pendingStart));
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

    private void OnDestroy()
    {
        if (_idlePending) PInvoke.KillTimer(Hwnd, IdleTimerId);
        DiscardTarget();
        View.Unload();
    }
}
