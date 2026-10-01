using Windows.Win32.Graphics.Direct2D;

namespace AdvancedViewer.Hosting;

/// <summary>
/// Per-window content shown by a <see cref="ViewWindow"/> (the "View seam" of
/// docs/plan-markdown-rendering.md). <see cref="ViewWindow"/> owns the HWND, the render target,
/// DPI, the scroll bar, key forwarding and the idle pump; a View only reacts to these calls.
/// All calls arrive on TC's UI thread, inside a catch-all (WndProc or export). A View never
/// sees a window message and never creates or releases the render target.
///
/// Deviations from / additions to the plan's contract (all additive, names and semantics of the
/// planned members are unchanged):
///  - <see cref="Window"/> is a property with a private setter (set through <see cref="Attach"/>
///    before the first <see cref="Load"/>), not a writable field.
///  - <see cref="Load"/> may run before the HWND exists (first ListLoadW): Window.Hwnd is then
///    null, while Window.ClientWidthPx/ClientHeightPx/DpiScale/Theme already hold the values
///    of the window about to be created (taken from the parent). ViewWindow services are safe
///    to call then (they no-op or are deferred: RequestIdleWork is posted once the window exists).
///  - A failed <see cref="Load"/> (false) must leave the view showing its previous file, if any.
///    On ListLoadNext to a file of another kind, ViewWindow creates a fresh View, loads it, and
///    only on success unloads the old one and swaps.
///  - <see cref="ScrollPosition"/> (virtual, extra): a number logged with each WM_PAINT ("top=").
///  - Graphics.EnsureInitialized() has run before Load is called.
///  - Scroll units are the view's choice: <see cref="ViewWindow.SetScrollRange"/> takes "DIP"
///    names from the plan but just maps total/page/pos onto the Win32 scroll bar
///    (0..total-1); HexView uses rows (and ViewWindow.SetScrollInfoRaw for > 2^31 rows).
///  - <see cref="OnVScroll"/>: trackPos is the 32-bit SIF_TRACKPOS value for SB_THUMBTRACK and
///    SB_THUMBPOSITION, 0 for every other code.
///  - <see cref="OnMouseWheel"/>: notches is positive for wheel-up (away from the user), already
///    accumulated from WHEEL_DELTA (120) units by ViewWindow; never 0.
///  - <see cref="DoIdleWork"/>: deadlineTicks is in <see cref="System.Diagnostics.Stopwatch"/>
///    ticks (Log.Now()), about 6 ms after the call starts.
///  - <see cref="OnPaint"/>: the render target has the window DPI set, so drawing is in DIPs;
///    rt is the ID2D1HwndRenderTarget seen through its ID2D1RenderTarget base. The window has
///    already called Clear(theme.Background). Brushes created from rt must be released in
///    <see cref="OnDeviceLost"/>, which ViewWindow calls before it releases the target
///    (D2DERR_RECREATE_TARGET, DPI change, window destroy, view swap).
///  - Coordinates passed to the mouse methods are client pixels (divide by Window.DpiScale for DIPs).
/// </summary>
internal abstract unsafe class View
{
    /// <summary>The hosting window; set by ViewWindow before Load.</summary>
    protected ViewWindow Window { get; private set; } = null!;

    /// <summary>Full path of the file currently shown ("" before the first successful Load).</summary>
    public string Path { get; protected set; } = "";

    internal void Attach(ViewWindow window) => Window = window;

    /// <summary>First load and ListLoadNext. Returns false (reason in error) if the file cannot be shown; the previous state is kept.</summary>
    public abstract bool Load(string path, int showFlags, out string? error);

    /// <summary>Release the file, layouts and device-independent objects. Called on window destroy and when the view is swapped out.</summary>
    public abstract void Unload();

    /// <summary>Client size changed (also called once right after the window is created, and after a view swap).</summary>
    public abstract void OnSize(int widthPx, int heightPx, float dpiScale);

    /// <summary>Called inside BeginDraw/EndDraw; rt is already cleared with theme.Background.</summary>
    public abstract void OnPaint(ID2D1RenderTarget* rt, Theme theme);

    /// <summary>Release everything created from the render target (brushes, bitmaps).</summary>
    public abstract void OnDeviceLost();

    /// <summary>true = handled; otherwise ViewWindow forwards the key to TC (PostMessage to the parent).</summary>
    public abstract bool OnKeyDown(int vk, bool ctrl, bool shift);

    public abstract void OnVScroll(int sbCode, int trackPos);

    public abstract void OnMouseWheel(int notches, bool ctrl);

    /// <summary>true = the view set the cursor (e.g. hand over a link); false = arrow.</summary>
    public virtual bool OnMouseMove(int xPx, int yPx) => false;

    public virtual void OnMouseDown(int xPx, int yPx) { }

    /// <summary>Do incremental work until deadlineTicks. true = more work left; ViewWindow re-posts WM_APP_LAYOUT.</summary>
    public virtual bool DoIdleWork(long deadlineTicks) => false;

    /// <summary>Dark/light switch at runtime (also before the first paint if the theme differs from the one at Load).</summary>
    public abstract void SetTheme(Theme theme);

    /// <summary>Extra (not in the plan): logged with every WM_PAINT as "top=N".</summary>
    public virtual long ScrollPosition => 0;
}
