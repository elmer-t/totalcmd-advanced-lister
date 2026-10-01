using System;
using AdvancedViewer.Markdown.Model;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.System.Com;

namespace AdvancedViewer.Markdown;

/// <summary>
/// <see cref="LayoutItem"/> → IDWriteTextLayout(s) at a content width: fonts, weights, styles,
/// underline and strikethrough per inline run; heading sizes; code, HTML, image and table cell
/// layouts; table column fitting; height estimates for items not measured yet. Colors are not
/// baked in: <see cref="ApplyEffects"/> sets drawing effects from the item's runs with the
/// current brushes, so a theme or device change only re-applies them.
/// All sizes are DIPs. Every layout created here is either stored in a <see cref="LiveLayouts"/>
/// slot (released by <see cref="LiveLayouts.Release"/>) or released before returning.
/// </summary>
internal static unsafe class BlockLayouter
{
    public const float CodePadding = 12f;
    public const float CellPadding = 8f;
    public const float ImagePadding = 8f;
    public const float HeadingRuleGap = 6f;
    /// <summary>Gap between a list marker and the item content.</summary>
    public const float MarkerGap = 6f;
    /// <summary>Items never get narrower than this, however deep the nesting.</summary>
    public const float MinWidth = 40f;
    private const float InlineCodeScale = 0.9f;
    private const float Unbounded = 1e6f;

    private static readonly float[] s_headingScale = { 2.0f, 1.5f, 1.25f, 1.1f, 1.0f, 0.9f };

    // Scratch buffer for HitTestTextRange (layout time only).
    private static DWRITE_HIT_TEST_METRICS[] s_hits = new DWRITE_HIT_TEST_METRICS[16];

    public static float HeadingScale(int level) => s_headingScale[Math.Clamp(level, 1, 6) - 1];

    public static float ItemWidth(in LayoutItem it, float contentWidth) => Math.Max(MinWidth, contentWidth - it.Indent);

    /// <summary>Y of the main text inside the item's content box (code padding, image box padding).</summary>
    public static float TextTop(in LayoutItem it) => it.Kind switch
    {
        ItemKind.Code => (it.Flags & ItemFlags.FirstChunk) != 0 ? CodePadding : 0,
        ItemKind.Image => ImagePadding,
        ItemKind.TableRow => CellPadding,
        _ => 0,
    };

    /// <summary>X of the main text inside the item's content box.</summary>
    public static float TextLeft(in LayoutItem it) => it.Kind switch
    {
        ItemKind.Code => CodePadding,
        ItemKind.Image => ImagePadding,
        _ => 0,
    };

    // ------------------------------------------------------------------ estimates

    /// <summary>Height guess without creating a layout (used for the scroll range until measured).</summary>
    public static float Estimate(in LayoutItem it, float contentWidth)
    {
        float w = ItemWidth(it, contentWidth);
        switch (it.Kind)
        {
            case ItemKind.Paragraph:
            {
                int chars = it.Inline?.Text.Length ?? 0;
                return WrappedLines(chars, it.Lines, Graphics.BodyAvgCharWidth, w) * Graphics.BodyLineHeight;
            }
            case ItemKind.Heading:
            {
                float scale = HeadingScale(it.Level);
                int chars = it.Inline?.Text.Length ?? 0;
                float h = WrappedLines(chars, 1, Graphics.BodyAvgCharWidth * scale, w) * Graphics.BodyLineHeight * scale;
                return it.Level <= 2 ? h + HeadingRuleGap + 1 : h;
            }
            case ItemKind.Code:
            {
                float h = WrappedLines(it.TextLength, it.Lines, Graphics.CodeCharWidth, w - 2 * CodePadding) * Graphics.CodeLineHeight;
                if ((it.Flags & ItemFlags.FirstChunk) != 0) h += CodePadding;
                if ((it.Flags & ItemFlags.LastChunk) != 0) h += CodePadding;
                return h;
            }
            case ItemKind.Html:
                return WrappedLines(it.TextLength, it.Lines, Graphics.CodeCharWidth, w) * Graphics.CodeLineHeight;
            case ItemKind.TableRow:
                return it.Lines * Graphics.BodyLineHeight + 2 * CellPadding;
            case ItemKind.Image:
                return Graphics.BodyLineHeight + 2 * ImagePadding;
            default:
                return it.Height;
        }
    }

    private static int WrappedLines(int chars, int hardLines, float charWidth, float width)
    {
        if (width < 1) width = 1;
        int perLine = Math.Max(1, (int)(width / Math.Max(charWidth, 0.1f)));
        // Each hard line wraps on its own; assume the characters are spread evenly over them.
        int wrapped = (chars + perLine - 1) / perLine;
        return Math.Max(Math.Max(1, hardLines), wrapped + Math.Max(0, hardLines - 1) / 2);
    }

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// Lays out <paramref name="it"/> at <paramref name="contentWidth"/> and returns its height
    /// (without the gap). With a <paramref name="live"/> slot the layouts, marker layouts and
    /// inline-code rectangles are kept in it; without, everything is released (measure only).
    /// </summary>
    public static float Layout(ref LayoutItem it, float contentWidth, LiveLayouts? live)
    {
        float w = ItemWidth(it, contentWidth);
        float height;
        switch (it.Kind)
        {
            case ItemKind.Paragraph:
            case ItemKind.Heading:
            {
                InlineText text = it.Inline!;
                float scale = it.Kind == ItemKind.Heading ? HeadingScale(it.Level) : 1f;
                IDWriteTextLayout* layout = CreateInline(text, w, scale, heading: it.Kind == ItemKind.Heading, header: false);
                height = Height(layout);
                if (it.Kind == ItemKind.Heading && it.Level <= 2) height += HeadingRuleGap + 1;
                if (live != null)
                {
                    live.Layout = layout;
                    AddInlineCodeRects(live, layout, text, 0, 0);
                }
                else
                {
                    layout->Release();
                }
                break;
            }

            case ItemKind.Code:
            case ItemKind.Html:
            {
                float pad = it.Kind == ItemKind.Code ? CodePadding : 0;
                IDWriteTextLayout* layout = CreatePlain(it.Plain!, it.TextStart, it.TextLength, Graphics.CodeFormat, w - 2 * pad);
                height = Height(layout);
                if (it.Kind == ItemKind.Code)
                {
                    if ((it.Flags & ItemFlags.FirstChunk) != 0) height += CodePadding;
                    if ((it.Flags & ItemFlags.LastChunk) != 0) height += CodePadding;
                }
                if (live != null) live.Layout = layout;
                else layout->Release();
                break;
            }

            case ItemKind.Image:
            {
                IDWriteTextLayout* layout = CreatePlain(it.Plain!, 0, it.TextLength, Graphics.BodyFormat, w - 2 * ImagePadding);
                layout->SetFontStyle(DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC, Range(0, it.TextLength));
                height = Height(layout) + 2 * ImagePadding;
                if (live != null) live.Layout = layout;
                else layout->Release();
                break;
            }

            case ItemKind.TableRow:
                height = LayoutRow(ref it, w, live);
                break;

            case ItemKind.ThematicBreak:
            default:
                height = ItemBuilder.ThematicBreakHeight;
                break;
        }

        if (live != null && it.Markers != null)
        {
            foreach (Marker m in it.Markers)
                live.Markers[live.MarkerCount++] = (nint)CreateMarker(m);
        }
        return height;
    }

    private static float Height(IDWriteTextLayout* layout)
    {
        DWRITE_TEXT_METRICS m;
        layout->GetMetrics(&m);
        return m.height;
    }

    public static DWRITE_TEXT_RANGE Range(int start, int length) => new() { startPosition = (uint)start, length = (uint)length };

    private static IDWriteTextLayout* CreatePlain(string s, int start, int length, IDWriteTextFormat* format, float width)
    {
        IDWriteTextLayout* layout = null;
        fixed (char* p = s)
        {
            Graphics.DWriteFactory->CreateTextLayout(p + start, (uint)length, format, Math.Max(1f, width), Unbounded, &layout);
        }
        return layout;
    }

    /// <summary>Body-format layout of inline text with per-run fonts. heading = semibold base weight; header = table header cell.</summary>
    private static IDWriteTextLayout* CreateInline(InlineText text, float width, float scale, bool heading, bool header)
    {
        IDWriteTextLayout* layout = CreatePlain(text.Text, 0, text.Text.Length, Graphics.BodyFormat, width);
        int len = text.Text.Length;
        if (len == 0)
        {
            if (scale != 1f) layout->SetFontSize(Graphics.BodySizeDip * scale, Range(0, 1));
            return layout;
        }
        DWRITE_TEXT_RANGE all = Range(0, len);
        if (scale != 1f) layout->SetFontSize(Graphics.BodySizeDip * scale, all);
        if (heading || header) layout->SetFontWeight(DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, all);

        fixed (char* mono = Graphics.MonoFamily)
        {
            foreach (InlineRun run in text.Runs)
            {
                StyleFlags s = run.Style;
                if (s == StyleFlags.None) continue;
                DWRITE_TEXT_RANGE r = Range(run.Start, run.Length);
                if ((s & StyleFlags.Bold) != 0) layout->SetFontWeight(DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD, r);
                if ((s & (StyleFlags.Italic | StyleFlags.ImagePlaceholder)) != 0) layout->SetFontStyle(DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC, r);
                if ((s & StyleFlags.Code) != 0)
                {
                    layout->SetFontFamilyName(mono, r);
                    layout->SetFontSize(Graphics.BodySizeDip * scale * InlineCodeScale, r);
                }
                if ((s & StyleFlags.Strike) != 0) layout->SetStrikethrough(true, r);
                if ((s & StyleFlags.Link) != 0) layout->SetUnderline(true, r);
            }
        }
        return layout;
    }

    private static IDWriteTextLayout* CreateMarker(in Marker m)
    {
        IDWriteTextLayout* layout = CreatePlain(m.Text, 0, m.Text.Length, Graphics.BodyFormat, Math.Max(1f, m.Width - MarkerGap));
        layout->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING);
        layout->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
        if (m.Kind == MarkerKind.Task)
        {
            fixed (char* sym = "Segoe UI Symbol")
                layout->SetFontFamilyName(sym, Range(0, m.Text.Length));
        }
        return layout;
    }

    /// <summary>Background rectangles behind inline code runs (relative to the item's content origin).</summary>
    private static void AddInlineCodeRects(LiveLayouts live, IDWriteTextLayout* layout, InlineText text, float ox, float oy)
    {
        foreach (InlineRun run in text.Runs)
        {
            if ((run.Style & StyleFlags.Code) == 0) continue;
            uint count = 0;
            HRESULT hr;
            while (true)
            {
                fixed (DWRITE_HIT_TEST_METRICS* hits = s_hits)
                {
                    hr = HitTestTextRange(layout, (uint)run.Start, (uint)run.Length, hits, (uint)s_hits.Length, &count);
                }
                if (hr.Value == unchecked((int)0x8007007A) /* E_NOT_SUFFICIENT_BUFFER */ && count > s_hits.Length)
                {
                    s_hits = new DWRITE_HIT_TEST_METRICS[count];
                    continue;
                }
                break;
            }
            if (hr.Failed) continue;
            for (int i = 0; i < count; i++)
            {
                ref DWRITE_HIT_TEST_METRICS h = ref s_hits[i];
                if (h.width <= 0) continue;
                live.AddCodeRect(new D2D_RECT_F
                {
                    left = ox + h.left - 2,
                    top = oy + h.top,
                    right = ox + h.left + h.width + 2,
                    bottom = oy + h.top + h.height,
                });
            }
        }
    }

    /// <summary>IDWriteTextLayout::HitTestTextRange returning the HRESULT (the generated wrapper throws on E_NOT_SUFFICIENT_BUFFER).</summary>
    private static HRESULT HitTestTextRange(IDWriteTextLayout* layout, uint pos, uint len, DWRITE_HIT_TEST_METRICS* hits, uint max, uint* count)
    {
        void** vtbl = *(void***)layout;
        var fn = (delegate* unmanaged[Stdcall]<IDWriteTextLayout*, uint, uint, float, float, DWRITE_HIT_TEST_METRICS*, uint, uint*, HRESULT>)vtbl[66];
        return fn(layout, pos, len, 0f, 0f, hits, max, count);
    }

    // ------------------------------------------------------------------ tables

    /// <summary>Lays out one table row; cell layouts are kept in <paramref name="live"/> or released.</summary>
    private static float LayoutRow(ref LayoutItem it, float width, LiveLayouts? live)
    {
        TableInfo t = it.Table!;
        FitColumns(t, width);
        TableRow row = t.Table.Rows[it.Row];
        float[] cols = t.Columns!;
        float maxH = Graphics.BodyLineHeight;
        float x = 0;
        for (int c = 0; c < cols.Length; c++)
        {
            InlineText cell = row.Cells[c];
            IDWriteTextLayout* layout = CreateInline(cell, cols[c] - 2 * CellPadding, 1f, heading: false, header: it.Row == 0);
            layout->SetTextAlignment(t.Table.Aligns[c] switch
            {
                ColumnAlign.Center => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
                ColumnAlign.Right => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
                _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
            });
            maxH = Math.Max(maxH, Height(layout));
            if (live != null)
            {
                live.AddCell(layout);
                AddInlineCodeRects(live, layout, cell, x + CellPadding, CellPadding);
            }
            else
            {
                layout->Release();
            }
            x += cols[c];
        }
        return maxH + 2 * CellPadding;
    }

    /// <summary>Rows sampled for natural column widths (a 100k-row table would otherwise need 100k×cols layouts at once).</summary>
    private const int MaxSampledRows = 1000;

    /// <summary>
    /// Column widths for <paramref name="width"/> (cached per width). Natural width per column =
    /// widest unconstrained cell, minimum = longest word (DetermineMinWidth), both over the first
    /// <see cref="MaxSampledRows"/> rows. If the natural widths do not fit, the widest columns are
    /// shrunk to a common cap ("water filling"): narrow columns keep their natural width, wide
    /// ones wrap. The cap never pushes a column below its longest word while the minimum widths
    /// fit; only when even those do not fit are words broken (emergency break).
    /// </summary>
    public static void FitColumns(TableInfo t, float width)
    {
        int n = t.Table.Aligns.Length;
        if (t.Natural == null)
        {
            var natural = new float[n];
            var minimum = new float[n];
            int rows = Math.Min(t.Table.Rows.Count, MaxSampledRows);
            for (int r = 0; r < rows; r++)
            {
                TableRow row = t.Table.Rows[r];
                for (int c = 0; c < n; c++)
                {
                    InlineText cell = row.Cells[c];
                    if (cell.Text.Length == 0) continue;
                    IDWriteTextLayout* layout = CreateInline(cell, Unbounded, 1f, heading: false, header: r == 0);
                    DWRITE_TEXT_METRICS m;
                    layout->GetMetrics(&m);
                    float minWord;
                    layout->DetermineMinWidth(&minWord);
                    layout->Release();
                    natural[c] = Math.Max(natural[c], m.widthIncludingTrailingWhitespace);
                    minimum[c] = Math.Max(minimum[c], minWord);
                }
            }
            for (int c = 0; c < n; c++)
            {
                natural[c] = MathF.Ceiling(natural[c]) + 2 * CellPadding + 1;
                minimum[c] = Math.Min(natural[c], MathF.Ceiling(minimum[c]) + 2 * CellPadding + 1);
            }
            t.Natural = natural;
            t.Minimum = minimum;
        }
        if (t.FittedFor == width && t.Columns != null) return;

        float[] nat = t.Natural, min = t.Minimum!;
        float[] cols = t.Columns ?? new float[n];
        float sumNat = 0, sumMin = 0, maxNat = 0;
        for (int c = 0; c < n; c++)
        {
            sumNat += nat[c];
            sumMin += min[c];
            maxNat = Math.Max(maxNat, nat[c]);
        }

        if (sumNat <= width)
        {
            Array.Copy(nat, cols, n);
        }
        else
        {
            // Find the cap C with sum(clamp(C, floor, natural)) == width by bisection; floor is
            // the longest word while those fit, else a small fixed minimum.
            bool keepWords = sumMin <= width;
            float floorMin = 2 * CellPadding + 8;
            float lo = 0, hi = maxNat;
            for (int iter = 0; iter < 40; iter++)
            {
                float mid = (lo + hi) / 2;
                float s = 0;
                for (int c = 0; c < n; c++)
                    s += Math.Max(keepWords ? min[c] : Math.Min(floorMin, nat[c]), Math.Min(nat[c], mid));
                if (s > width) hi = mid; else lo = mid;
            }
            for (int c = 0; c < n; c++)
                cols[c] = MathF.Floor(Math.Max(keepWords ? min[c] : Math.Min(floorMin, nat[c]), Math.Min(nat[c], lo)));
        }
        float sum = 0;
        for (int c = 0; c < n; c++) sum += cols[c];
        t.Columns = cols;
        t.Width = sum;
        t.FittedFor = width;
    }

    /// <summary>X of column <paramref name="col"/>'s left edge, relative to the table's left.</summary>
    public static float ColumnLeft(TableInfo t, int col)
    {
        float x = 0;
        for (int c = 0; c < col; c++) x += t.Columns![c];
        return x;
    }

    // ------------------------------------------------------------------ colors

    /// <summary>
    /// Sets the drawing effects (link, inline code and image-placeholder colors) on the item's
    /// live layouts from its runs. Called whenever the slot's effects are older than the brushes.
    /// </summary>
    public static void ApplyEffects(in LayoutItem it, LiveLayouts live, Brushes b)
    {
        if (it.Kind is ItemKind.Paragraph or ItemKind.Heading && live.Layout != null)
        {
            ApplyRunEffects(live.Layout, it.Inline!, b);
        }
        else if (it.Kind == ItemKind.TableRow)
        {
            TableRow row = it.Table!.Table.Rows[it.Row];
            for (int c = 0; c < live.CellCount; c++)
                ApplyRunEffects((IDWriteTextLayout*)live.Cells[c], row.Cells[c], b);
        }
        live.EffectsGen = b.Generation;
    }

    private static void ApplyRunEffects(IDWriteTextLayout* layout, InlineText text, Brushes b)
    {
        foreach (InlineRun run in text.Runs)
        {
            StyleFlags s = run.Style;
            IUnknown* brush =
                (s & StyleFlags.Link) != 0 ? (IUnknown*)b.Link :
                (s & StyleFlags.Code) != 0 ? (IUnknown*)b.CodeText :
                (s & StyleFlags.ImagePlaceholder) != 0 ? (IUnknown*)b.Muted :
                null;
            if (brush != null) layout->SetDrawingEffect(brush, Range(run.Start, run.Length));
        }
    }
}
