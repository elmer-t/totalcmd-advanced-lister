using System;
using System.Collections.Generic;
using AdvancedViewer.Hosting;
using AdvancedViewer.Markdown.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.UI.WindowsAndMessaging;

namespace AdvancedViewer.Markdown;

/// <summary>
/// Rendered Markdown. Load = read → decode → Markdig → <see cref="Document"/> → flat
/// <see cref="LayoutItem"/> array (<see cref="ItemBuilder"/>). Heights start as estimates and
/// are replaced by measured ones: synchronously for what is on screen, and in ~6 ms idle slices
/// (<see cref="DoIdleWork"/>) for the rest, so the first paint costs the same for a 5 MB file as
/// for a README. A Fenwick tree (<see cref="HeightIndex"/>) maps y ↔ item. The scroll position
/// is kept as (top item, offset into it), so measuring items above the viewport never moves
/// the visible text; on a width change all heights go back to estimates and the top item
/// keeps its relative offset. Layouts exist only for items near the viewport (pooled
/// <see cref="LiveLayouts"/> slots); painting is allocation-free once the pool has grown.
/// </summary>
internal sealed unsafe class MarkdownView : View
{
    private const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24,
        VK_UP = 0x26, VK_DOWN = 0x28;
    private const int SB_LINEUP = 0, SB_LINEDOWN = 1, SB_PAGEUP = 2, SB_PAGEDOWN = 3,
        SB_THUMBPOSITION = 4, SB_THUMBTRACK = 5, SB_TOP = 6, SB_BOTTOM = 7;

    private const float Margin = 24f;          // DIP: left/right minimum, top and bottom
    private const float MaxContentWidth = 900f;
    private const float MinContentWidth = 60f;
    private const int LineStep = 40;           // DIP per arrow key / scroll-bar line
    private const int WheelLineDip = 20;       // DIP per wheel "line"
    /// <summary>Items kept live above/below the painted range before their layouts are released.</summary>
    private const int KeepAround = 12;

    private LayoutItem[] _items = Array.Empty<LayoutItem>();
    private int _count;
    private readonly HeightIndex _index = new();
    /// <summary>Content width the heights are for (-1 = none yet).</summary>
    private float _width = -1;

    // Scroll position = top of item _anchor + _anchorOffset (DIP, document coordinates).
    private int _anchor;
    private double _anchorOffset;
    /// <summary>After a width change: offset as a fraction of the anchor's extent, applied once it is measured (-1 = none).</summary>
    private double _anchorFraction = -1;
    /// <summary>The user scrolled to the end: stay there while measuring changes the total height.</summary>
    private bool _pinBottom;

    // Incremental measuring.
    private int _measureCursor;
    private int _unmeasured;
    private long _loadStart;
    private long _busyTicks;
    private bool _logDonePending;
    private long _parseTicks;

    // Incremental parsing of large files (null when the whole text is parsed).
    private ItemBuilder? _builder;
    private string? _source;
    private int _parsePos;
    private string _definitions = "";

    private readonly List<LiveLayouts> _slots = new();
    private readonly List<int> _freeSlots = new();
    private readonly Brushes _brushes = new();
    private int _firstPainted = -1, _lastPainted = -1;

    private int _sbTotal = -1, _sbPage = -1, _sbPos = -1;

    public override long ScrollPosition => (long)ScrollY;

    // ---------------------------------------------------------------- geometry

    private float ClientWidthDip => Window.ClientWidthPx / Window.DpiScale;
    private float ViewportDip => Window.ClientHeightPx / Window.DpiScale;
    private float ContentWidth => Math.Max(MinContentWidth, Math.Min(ClientWidthDip - 2 * Margin, MaxContentWidth));
    private float ContentLeft => Math.Max(Margin, (ClientWidthDip - ContentWidth) / 2);

    private double ItemTop(int i) => Margin + _index.Prefix(i);
    private double DocHeight => _count == 0 ? 0 : Margin + _index.Total + Margin;
    private double MaxScroll => Math.Max(0, DocHeight - ViewportDip);
    private double ScrollY => _count == 0 ? 0 : ItemTop(_anchor) + _anchorOffset;

    // ---------------------------------------------------------------- loading

    public override bool Load(string path, int showFlags, out string? error)
    {
        long t0 = Log.Now();
        byte[]? bytes = TextLoader.Read(path, out bool truncated, out long fileSize, out error);
        if (bytes == null) return false;

        string text = TextDecoder.Decode(bytes);
        bytes = null;
        if (truncated) text = TextDecoder.AppendTruncationNotice(text, fileSize);

        // Parse: whole text, or (large files) only the first chunk now and the rest in DoIdleWork.
        long tParse = Log.Now();
        var builder = new ItemBuilder();
        string definitions = "";
        int parsed = text.Length;
        if (text.Length > ChunkSplitter.Threshold)
        {
            definitions = ChunkSplitter.CollectDefinitions(text, MaxDefinitionChars);
            parsed = ChunkSplitter.NextEnd(text, 0, ChunkSplitter.ChunkChars, out bool open);
            builder.Append(MarkdownParser.Parse(ChunkText(text, 0, parsed, definitions, open)));
        }
        else
        {
            builder.Append(MarkdownParser.Parse(text));
        }
        bool more = parsed < text.Length;
        if (!more) builder.Trim();
        long parseTicks = Log.Now() - tParse;
        Log.Write("MarkdownParse", path, Log.ElapsedUs(tParse),
            "chars=" + text.Length + " parsed=" + parsed + " items=", builder.Count, hasNum: true);
        Graphics.EnsureMarkdown();

        // Commit: nothing below fails on bad input. Release the old document's layouts first;
        // pending idle work of the old document now works on the new one.
        ReleaseAllLive();
        _builder = more ? builder : null;
        _source = more ? text : null;
        _parsePos = parsed;
        _definitions = definitions;
        _items = builder.Items;
        _count = builder.Count;
        Path = path;
        _pinBottom = false;
        _width = -1;
        _sbTotal = _sbPage = _sbPos = -1;
        _firstPainted = _lastPainted = -1;
        _index.Clear();
        _anchor = 0;
        _anchorOffset = 0;
        _anchorFraction = -1;
        _loadStart = t0;
        _busyTicks = 0;
        _parseTicks = parseTicks;
        _logDonePending = true;

        long tLayout = Log.Now();
        Relayout(ContentWidth);
        if (_count > 0)
        {
            SetScroll(0);
            PrepareViewport();
        }
        Log.Write("MarkdownLayout", path, Log.ElapsedUs(tLayout), "firstScreen items=", _count, hasNum: true);
        UpdateScrollBar();
        Window.RequestIdleWork();
        return true;
    }

    /// <summary>A chunk parse slower than this is logged (MarkdownSlowChunk, with the number of GCs it contained).</summary>
    private const long SlowSliceMs = 20;

    /// <summary>Reference definitions appended to every chunk (cap), so reference links resolve across chunks.</summary>
    private const int MaxDefinitionChars = 32 * 1024;

    private static string ChunkText(string text, int start, int end, string definitions, bool endsOpen) =>
        definitions.Length == 0 || endsOpen
            ? text.Substring(start, end - start)
            : string.Concat(text.AsSpan(start, end - start), "\n\n", definitions);

    /// <summary>Parses the next chunk of a large file and appends its items (estimated heights).</summary>
    private void ParseNextChunk()
    {
        string source = _source!;
        long t0 = Log.Now();
        int end = ChunkSplitter.NextEnd(source, _parsePos, ChunkSplitter.ChunkChars, out bool open);
        Document doc = MarkdownParser.Parse(ChunkText(source, _parsePos, end, _definitions, open));
        int before = _count;
        _builder!.Append(doc);
        _parsePos = end;
        bool last = end >= source.Length;
        if (last) _builder.Trim();
        _items = _builder.Items;
        _count = _builder.Count;
        for (int i = before; i < _count; i++)
        {
            ref LayoutItem it = ref _items[i];
            if (it.Kind == ItemKind.ThematicBreak) it.Flags |= ItemFlags.Measured;
            else
            {
                it.Height = BlockLayouter.Estimate(it, _width);
                _unmeasured++;
            }
            _index.Append(it.Extent);
        }
        _parseTicks += Log.Now() - t0;
        if (last)
        {
            _builder = null;
            _source = null;
            _definitions = "";
            Log.Write("MarkdownParseDone", Path, Log.ElapsedUs(_loadStart),
                "items=" + _count + " parseBusyUs=", _parseTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency, hasNum: true);
        }
        // New items may be on screen (short first chunk) or the view may follow the end.
        if (_pinBottom || ItemTop(before) - ScrollY < ViewportDip) Window.Invalidate();
    }

    public override void Unload()
    {
        ReleaseAllLive();
        _builder = null;
        _source = null;
        _definitions = "";
        _brushes.Release();
        _items = Array.Empty<LayoutItem>();
        _count = 0;
        _index.Clear();
        _unmeasured = 0;
    }

    // ---------------------------------------------------------------- measuring

    /// <summary>All heights back to estimates at <paramref name="width"/>; keeps the anchor's relative offset.</summary>
    private void Relayout(float width)
    {
        double fraction = -1;
        if (_width > 0 && _anchor < _count && _anchorOffset >= 0)
        {
            float extent = _items[_anchor].Extent;
            fraction = extent > 0 ? Math.Clamp(_anchorOffset / extent, 0, 1) : 0;
        }
        ReleaseAllLive();
        _width = width;
        int unmeasured = 0;
        for (int i = 0; i < _count; i++)
        {
            ref LayoutItem it = ref _items[i];
            if (it.Kind == ItemKind.ThematicBreak)
            {
                it.Flags |= ItemFlags.Measured;
                continue;
            }
            it.Flags &= ~ItemFlags.Measured;
            it.Height = BlockLayouter.Estimate(it, width);
            unmeasured++;
        }
        _unmeasured = unmeasured;
        _measureCursor = 0;
        _index.Build(new ReadOnlySpan<LayoutItem>(_items, 0, _count));
        if (fraction >= 0)
        {
            _anchorFraction = fraction;
            _anchorOffset = fraction * _items[_anchor].Extent;
        }
        if (_unmeasured > 0) Window.RequestIdleWork();
    }

    /// <summary>Measures item <paramref name="i"/>; with <paramref name="live"/> it also keeps its layouts.</summary>
    private void Measure(int i, bool live)
    {
        ref LayoutItem it = ref _items[i];
        float h;
        if (live)
        {
            if (it.Live != 0) return;
            h = BlockLayouter.Layout(ref it, _width, AllocSlot(i));
        }
        else
        {
            if (it.IsMeasured) return;
            h = BlockLayouter.Layout(ref it, _width, null);
        }
        if (!it.IsMeasured)
        {
            it.Flags |= ItemFlags.Measured;
            _unmeasured--;
        }
        double delta = h - it.Height;
        it.Height = h;
        _index.Add(i, delta);
    }

    public override bool DoIdleWork(long deadlineTicks)
    {
        if (_source != null)
        {
            // Parsing first (one chunk, ~4 ms), so the document is complete as early as possible.
            long tp = Log.Now();
            int gc0 = Diag.GcCount();
            ParseNextChunk();
            long spent = Log.Now() - tp;
            _busyTicks += spent;
            if (spent * 1000 / System.Diagnostics.Stopwatch.Frequency >= SlowSliceMs)
                Log.Write("MarkdownSlowChunk", null, spent * 1_000_000 / System.Diagnostics.Stopwatch.Frequency, "gc=", Diag.GcCount() - gc0, hasNum: true);
            UpdateScrollBar();
            return true;
        }
        if (_count == 0 || _unmeasured <= 0)
        {
            LogDone();
            return false;
        }
        long t0 = Log.Now();
        int gcStart = Diag.GcCount();
        while (_measureCursor < _count)
        {
            if (!_items[_measureCursor].IsMeasured) Measure(_measureCursor, live: false);
            _measureCursor++;
            if (Log.Now() >= deadlineTicks) break;
        }
        if (_measureCursor >= _count && _unmeasured > 0) _measureCursor = 0; // defensive: rescan
        long measured = Log.Now() - t0;
        _busyTicks += measured;
        if (measured * 1000 / System.Diagnostics.Stopwatch.Frequency >= SlowSliceMs)
            Log.Write("MarkdownSlowMeasure", null, measured * 1_000_000 / System.Diagnostics.Stopwatch.Frequency, "gc=", Diag.GcCount() - gcStart, hasNum: true);

        if (_pinBottom || ScrollY > MaxScroll) Window.Invalidate(); // keep the end in view
        UpdateScrollBar();
        if (_unmeasured > 0) return true;
        LogDone();
        return false;
    }

    private void LogDone()
    {
        if (!_logDonePending) return;
        _logDonePending = false;
        long busyUs = _busyTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
        Log.Write("MarkdownLayoutDone", Path, Log.ElapsedUs(_loadStart), "items=" + _count + " busyUs=", busyUs, hasNum: true);
    }

    // ---------------------------------------------------------------- live layouts

    private LiveLayouts AllocSlot(int item)
    {
        LiveLayouts slot;
        int index;
        if (_freeSlots.Count > 0)
        {
            index = _freeSlots[^1];
            _freeSlots.RemoveAt(_freeSlots.Count - 1);
            slot = _slots[index];
        }
        else
        {
            slot = new LiveLayouts();
            index = _slots.Count;
            _slots.Add(slot);
        }
        slot.Item = item;
        _items[item].Live = index + 1;
        return slot;
    }

    private void FreeSlot(int index)
    {
        LiveLayouts slot = _slots[index];
        if (slot.Item < 0) return;
        if (slot.Item < _count) _items[slot.Item].Live = 0;
        slot.Release();
        _freeSlots.Add(index);
    }

    private void ReleaseAllLive()
    {
        for (int s = 0; s < _slots.Count; s++) FreeSlot(s);
    }

    /// <summary>Releases layouts of items far from the painted range.</summary>
    private void Evict()
    {
        int lo = _firstPainted - KeepAround, hi = _lastPainted + KeepAround;
        for (int s = 0; s < _slots.Count; s++)
        {
            int item = _slots[s].Item;
            if (item >= 0 && (item < lo || item > hi)) FreeSlot(s);
        }
    }

    // ---------------------------------------------------------------- scrolling

    /// <summary>Sets the scroll position (clamped) and re-anchors. Does not repaint.</summary>
    private void SetScroll(double y)
    {
        if (_count == 0) return;
        y = Math.Clamp(y, 0, MaxScroll);
        _anchor = _index.Find(y - Margin);
        _anchorOffset = y - ItemTop(_anchor);
        _anchorFraction = -1;
    }

    private void ScrollTo(double y)
    {
        if (_count == 0) return;
        double max = MaxScroll;
        _pinBottom = max > 0 && y >= max;
        double old = ScrollY;
        SetScroll(y);
        if (Math.Abs(ScrollY - old) < 0.01) return;
        UpdateScrollBar();
        Window.Invalidate();
    }

    private void UpdateScrollBar()
    {
        int total = (int)Math.Min(int.MaxValue, Math.Ceiling(DocHeight));
        int page = (int)ViewportDip;
        int pos = (int)Math.Round(ScrollY);
        if (total == _sbTotal && page == _sbPage && pos == _sbPos) return;
        if (Window.Hwnd == HWND.Null) return; // Load before the window exists: set after creation (OnSize)
        _sbTotal = total;
        _sbPage = page;
        _sbPos = pos;
        Window.SetScrollRange(total, page, pos);
    }

    private int PageStep => Math.Max(LineStep, (int)ViewportDip - LineStep);

    public override bool OnKeyDown(int vk, bool ctrl, bool shift)
    {
        switch (vk)
        {
            case VK_UP: ScrollTo(ScrollY - LineStep); return true;
            case VK_DOWN: ScrollTo(ScrollY + LineStep); return true;
            case VK_PRIOR: ScrollTo(ScrollY - PageStep); return true;
            case VK_NEXT: ScrollTo(ScrollY + PageStep); return true;
            // No caret: Home/End and Ctrl+Home/Ctrl+End both go to the start / end of the document.
            case VK_HOME: ScrollTo(0); return true;
            case VK_END: ScrollTo(double.MaxValue); return true;
        }
        return false;
    }

    public override void OnVScroll(int sbCode, int trackPos)
    {
        switch (sbCode)
        {
            case SB_LINEUP: ScrollTo(ScrollY - LineStep); break;
            case SB_LINEDOWN: ScrollTo(ScrollY + LineStep); break;
            case SB_PAGEUP: ScrollTo(ScrollY - PageStep); break;
            case SB_PAGEDOWN: ScrollTo(ScrollY + PageStep); break;
            case SB_TOP: ScrollTo(0); break;
            case SB_BOTTOM: ScrollTo(double.MaxValue); break;
            case SB_THUMBTRACK:
            case SB_THUMBPOSITION: ScrollTo(trackPos); break;
        }
    }

    public override void OnMouseWheel(int notches, bool ctrl)
    {
        uint lines = 3;
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
        double step = lines == uint.MaxValue /* WHEEL_PAGESCROLL */ ? PageStep : lines * (double)WheelLineDip;
        ScrollTo(ScrollY - notches * step);
    }

    public override void OnSize(int widthPx, int heightPx, float dpiScale)
    {
        if (_count > 0)
        {
            float width = ContentWidth;
            if (width != _width) Relayout(width);
            if (!_pinBottom && ScrollY > MaxScroll) SetScroll(MaxScroll);
        }
        UpdateScrollBar();
    }

    // ---------------------------------------------------------------- painting

    public override void SetTheme(Theme theme)
    {
        // Brushes are recolored in place on the next paint (Brushes.Ensure); layouts and their
        // drawing effects stay valid. ViewWindow invalidates.
    }

    public override void OnDeviceLost()
    {
        // Live layouts hold references to the brushes through their drawing effects: drop them
        // first, so nothing created from the old render target survives it.
        ReleaseAllLive();
        _brushes.Release();
    }

    /// <summary>
    /// Measures (and keeps layouts for) every item from the anchor to the bottom of the
    /// viewport, and fixes the scroll position when the document turned out shorter than
    /// estimated or the view is pinned to the end.
    /// </summary>
    private void PrepareViewport()
    {
        float viewport = ViewportDip;
        for (int pass = 0; pass < 4; pass++)
        {
            Measure(_anchor, live: true);
            if (_anchorFraction >= 0)
            {
                _anchorOffset = _anchorFraction * _items[_anchor].Extent;
                _anchorFraction = -1;
            }
            else if (_anchorOffset >= _items[_anchor].Extent && _anchor < _count - 1)
            {
                SetScroll(ScrollY); // the anchor shrank below the offset: re-anchor at the same y
                Measure(_anchor, live: true);
            }

            double y = -_anchorOffset;
            int i = _anchor;
            while (i < _count && y < viewport)
            {
                Measure(i, live: true);
                y += _items[i].Extent;
                i++;
            }

            double max = MaxScroll;
            double scroll = ScrollY;
            if ((_pinBottom && Math.Abs(scroll - max) > 0.5) || scroll > max + 0.5)
            {
                SetScroll(max);
                continue;
            }
            break;
        }
    }

    public override void OnPaint(ID2D1RenderTarget* rt, Theme theme)
    {
        _brushes.Ensure(rt, theme);
        if (_count == 0 || Window.ClientWidthPx <= 0) return;
        float width = ContentWidth;
        if (width != _width) Relayout(width);

        PrepareViewport();

        float viewport = ViewportDip;
        float left = ContentLeft;
        float px = 1f / Window.DpiScale;
        double top = -_anchorOffset;
        int i = _anchor;
        while (i < _count && top < viewport)
        {
            ref LayoutItem it = ref _items[i];
            if (it.Live == 0) Measure(i, live: true);
            LiveLayouts slot = _slots[it.Live - 1];
            if (slot.EffectsGen != _brushes.Generation) BlockLayouter.ApplyEffects(it, slot, _brushes);
            BlockPainter.Paint(rt, it, slot, _brushes, left, (float)top, _width, px);
            top += it.Extent;
            i++;
        }
        _firstPainted = _anchor;
        _lastPainted = i - 1;
        Evict();
        UpdateScrollBar();
    }

    // ---------------------------------------------------------------- links

    /// <summary>URL of the link under client pixel (x, y), or null.</summary>
    private string? LinkAt(int xPx, int yPx)
    {
        if (_count == 0) return null;
        float scale = Window.DpiScale;
        double docY = yPx / scale + ScrollY;
        int i = _index.Find(docY - Margin);
        ref LayoutItem it = ref _items[i];
        if (it.Live == 0) return null;
        LiveLayouts slot = _slots[it.Live - 1];
        float lx = xPx / scale - (ContentLeft + it.Indent);
        float ly = (float)(docY - (ItemTop(i) + it.Gap));
        if (ly < 0) return null;

        switch (it.Kind)
        {
            case ItemKind.Paragraph:
            case ItemKind.Heading:
                return slot.Layout != null ? HitLink(slot.Layout, it.Inline!, lx, ly) : null;

            case ItemKind.TableRow:
            {
                TableInfo t = it.Table!;
                if (t.Columns == null) return null;
                float x0 = 0;
                for (int c = 0; c < t.Columns.Length && c < slot.CellCount; c++)
                {
                    float x1 = x0 + t.Columns[c];
                    if (lx >= x0 && lx < x1)
                        return HitLink((IDWriteTextLayout*)slot.Cells[c], t.Table.Rows[it.Row].Cells[c],
                            lx - x0 - BlockLayouter.CellPadding, ly - BlockLayouter.CellPadding);
                    x0 = x1;
                }
                return null;
            }

            default:
                return null;
        }
    }

    private static string? HitLink(IDWriteTextLayout* layout, InlineText text, float x, float y)
    {
        BOOL trailing, inside;
        DWRITE_HIT_TEST_METRICS m;
        layout->HitTestPoint(x, y, &trailing, &inside, &m);
        if (!inside) return null;
        int pos = (int)m.textPosition;
        foreach (InlineRun run in text.Runs)
        {
            if (pos >= run.Start && pos < run.Start + run.Length)
                return (run.Style & StyleFlags.Link) != 0 ? run.LinkUrl : null;
        }
        return null;
    }

    public override bool OnMouseMove(int xPx, int yPx)
    {
        if (LinkAt(xPx, yPx) == null) return false;
        ViewWindow.SetHandCursor();
        return true;
    }

    public override void OnMouseDown(int xPx, int yPx)
    {
        string? url = LinkAt(xPx, yPx);
        if (url == null) return;
        bool web = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!web)
        {
            Log.Write("LinkIgnored", url, -1);
            return;
        }
        long t0 = Log.Now();
        HINSTANCE result;
        fixed (char* verb = "open")
        fixed (char* file = url)
        {
            result = PInvoke.ShellExecute(HWND.Null, verb, file, null, null, SHOW_WINDOW_CMD.SW_SHOWNORMAL);
        }
        Log.Write("LinkOpen", url, Log.ElapsedUs(t0), "result=", (long)(nint)result.Value, hasNum: true);
    }
}
