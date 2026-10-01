using System;
using AdvancedViewer.Hosting;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;

namespace AdvancedViewer.Markdown;

/// <summary>
/// The solid brushes of one render target, one per theme color. Created from the render target
/// on the first paint, recolored in place on a theme switch (so layouts keep their drawing
/// effects), released before the render target goes away. <see cref="Generation"/> changes
/// whenever the brush objects are recreated; layouts with older effects re-apply them.
/// </summary>
internal sealed unsafe class Brushes
{
    public ID2D1SolidColorBrush* Text, Muted, Link, CodeText, CodeBackground, InlineCodeBackground, Rule, QuoteBar, Grid, HeaderBackground;
    public int Generation;
    private Theme? _theme;

    public bool Created => Text != null;

    public void Ensure(ID2D1RenderTarget* rt, Theme theme)
    {
        if (Text == null)
        {
            Text = Create(rt, theme.Text);
            Muted = Create(rt, theme.MutedText);
            Link = Create(rt, theme.Link);
            CodeText = Create(rt, theme.CodeText);
            CodeBackground = Create(rt, theme.CodeBackground);
            InlineCodeBackground = Create(rt, theme.InlineCodeBackground);
            Rule = Create(rt, theme.HeadingRule);
            QuoteBar = Create(rt, theme.QuoteBar);
            Grid = Create(rt, theme.TableGrid);
            HeaderBackground = Create(rt, theme.TableHeaderBackground);
            Generation++;
            _theme = theme;
        }
        else if (!ReferenceEquals(theme, _theme))
        {
            SetColor(Text, theme.Text);
            SetColor(Muted, theme.MutedText);
            SetColor(Link, theme.Link);
            SetColor(CodeText, theme.CodeText);
            SetColor(CodeBackground, theme.CodeBackground);
            SetColor(InlineCodeBackground, theme.InlineCodeBackground);
            SetColor(Rule, theme.HeadingRule);
            SetColor(QuoteBar, theme.QuoteBar);
            SetColor(Grid, theme.TableGrid);
            SetColor(HeaderBackground, theme.TableHeaderBackground);
            _theme = theme;
        }
    }

    private static ID2D1SolidColorBrush* Create(ID2D1RenderTarget* rt, D2D1_COLOR_F color)
    {
        ID2D1SolidColorBrush* b = null;
        rt->CreateSolidColorBrush(&color, null, &b);
        return b;
    }

    private static void SetColor(ID2D1SolidColorBrush* b, D2D1_COLOR_F color) => b->SetColor(&color);

    public void Release()
    {
        Release(ref Text);
        Release(ref Muted);
        Release(ref Link);
        Release(ref CodeText);
        Release(ref CodeBackground);
        Release(ref InlineCodeBackground);
        Release(ref Rule);
        Release(ref QuoteBar);
        Release(ref Grid);
        Release(ref HeaderBackground);
        _theme = null;
    }

    private static void Release(ref ID2D1SolidColorBrush* b)
    {
        if (b != null) { b->Release(); b = null; }
    }
}

/// <summary>
/// Draws one <see cref="LayoutItem"/>: quote bars, list markers, code/table/image decorations,
/// inline code backgrounds, then the text layouts. Allocation-free.
/// </summary>
internal static unsafe class BlockPainter
{
    private const D2D1_DRAW_TEXT_OPTIONS TextOptions = D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE;

    /// <summary>
    /// Paints item <paramref name="it"/> whose top (gap included) is at <paramref name="top"/>;
    /// <paramref name="left"/> is the content column's left. <paramref name="px"/> = one device
    /// pixel in DIPs (for crisp 1 px lines).
    /// </summary>
    public static void Paint(ID2D1RenderTarget* rt, in LayoutItem it, LiveLayouts live, Brushes b,
        float left, float top, float contentWidth, float px)
    {
        float x = left + it.Indent;
        float y = top + it.Gap;
        float w = BlockLayouter.ItemWidth(it, contentWidth);
        float bottom = y + it.Height;

        // Quote bars: bars that start at this item skip the gap above it.
        if (it.QuoteBars != null)
        {
            for (int k = 0; k < it.QuoteBars.Length; k++)
            {
                float bx = Snap(left + it.QuoteBars[k], px);
                var r = new D2D_RECT_F { left = bx, top = k >= it.QuoteStartDepth ? y : top, right = bx + ItemBuilder.QuoteBarWidth, bottom = bottom };
                rt->FillRectangle(&r, (ID2D1Brush*)b.QuoteBar);
            }
        }

        // List markers, aligned with the first text line.
        if (it.Markers != null)
        {
            float my = y + (it.Kind == ItemKind.TableRow ? BlockLayouter.CellPadding : BlockLayouter.TextTop(it));
            for (int m = 0; m < live.MarkerCount && m < it.Markers.Length; m++)
            {
                var origin = new D2D_POINT_2F { x = left + it.Markers[m].X, y = my };
                ID2D1SolidColorBrush* brush = it.Markers[m].Kind == MarkerKind.Task ? b.Muted : b.Text;
                rt->DrawTextLayout(origin, (IDWriteTextLayout*)live.Markers[m], (ID2D1Brush*)brush, TextOptions);
            }
        }

        switch (it.Kind)
        {
            case ItemKind.Paragraph:
            case ItemKind.Heading:
                FillCodeRects(rt, live, b, x, y);
                rt->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, live.Layout,
                    (ID2D1Brush*)(it.Kind == ItemKind.Heading && it.Level == 6 ? b.Muted : b.Text), TextOptions);
                if (it.Kind == ItemKind.Heading && it.Level <= 2)
                    HLine(rt, x, x + w, bottom - 1, b.Rule, px);
                break;

            case ItemKind.Code:
            {
                bool first = (it.Flags & ItemFlags.FirstChunk) != 0;
                bool last = (it.Flags & ItemFlags.LastChunk) != 0;
                var r = new D2D_RECT_F { left = x, top = y, right = x + w, bottom = bottom };
                if (first && last)
                {
                    var rr = new D2D1_ROUNDED_RECT { rect = r, radiusX = 6, radiusY = 6 };
                    rt->FillRoundedRectangle(&rr, (ID2D1Brush*)b.CodeBackground);
                }
                else
                {
                    rt->FillRectangle(&r, (ID2D1Brush*)b.CodeBackground);
                }
                rt->DrawTextLayout(new D2D_POINT_2F { x = x + BlockLayouter.CodePadding, y = y + BlockLayouter.TextTop(it) }, live.Layout,
                    (ID2D1Brush*)b.CodeText, TextOptions);
                break;
            }

            case ItemKind.Html:
                rt->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, live.Layout, (ID2D1Brush*)b.Muted, TextOptions);
                break;

            case ItemKind.Image:
            {
                DWRITE_TEXT_METRICS m;
                live.Layout->GetMetrics(&m);
                float bw = Math.Min(w, m.widthIncludingTrailingWhitespace + 2 * BlockLayouter.ImagePadding);
                float half = px / 2;
                var r = new D2D_RECT_F
                {
                    left = Snap(x, px) + half,
                    top = Snap(y, px) + half,
                    right = Snap(x + bw, px) - half,
                    bottom = Snap(bottom, px) - half,
                };
                rt->DrawRectangle(&r, (ID2D1Brush*)b.Grid, px, Graphics.DashStroke);
                rt->DrawTextLayout(new D2D_POINT_2F { x = x + BlockLayouter.ImagePadding, y = y + BlockLayouter.ImagePadding }, live.Layout,
                    (ID2D1Brush*)b.Muted, TextOptions);
                break;
            }

            case ItemKind.ThematicBreak:
                HLine(rt, x, x + w, y + (it.Height - 1) / 2, b.Rule, px);
                break;

            case ItemKind.TableRow:
                PaintRow(rt, it, live, b, x, y, bottom, px);
                break;
        }
    }

    private static void PaintRow(ID2D1RenderTarget* rt, in LayoutItem it, LiveLayouts live, Brushes b, float x, float y, float bottom, float px)
    {
        TableInfo t = it.Table!;
        float[] cols = t.Columns!;
        float right = x + t.Width;
        if (it.Row == 0)
        {
            var bg = new D2D_RECT_F { left = x, top = y, right = right, bottom = bottom };
            rt->FillRectangle(&bg, (ID2D1Brush*)b.HeaderBackground);
        }
        FillCodeRects(rt, live, b, x, y);

        // Grid: top line of every row, bottom line of the last, vertical lines at column edges.
        HLine(rt, x, right + px, y, b.Grid, px);
        if (it.Row == t.Table.Rows.Count - 1) HLine(rt, x, right + px, bottom, b.Grid, px);
        float cx = x;
        for (int c = 0; c <= cols.Length; c++)
        {
            VLine(rt, cx, y, bottom, b.Grid, px);
            if (c < cols.Length)
            {
                if (c < live.CellCount)
                {
                    var origin = new D2D_POINT_2F { x = cx + BlockLayouter.CellPadding, y = y + BlockLayouter.CellPadding };
                    rt->DrawTextLayout(origin, (IDWriteTextLayout*)live.Cells[c], (ID2D1Brush*)b.Text, TextOptions);
                }
                cx += cols[c];
            }
        }
    }

    private static void FillCodeRects(ID2D1RenderTarget* rt, LiveLayouts live, Brushes b, float x, float y)
    {
        for (int i = 0; i < live.CodeRectCount; i++)
        {
            D2D_RECT_F r = live.CodeRects[i];
            var rr = new D2D1_ROUNDED_RECT
            {
                rect = new D2D_RECT_F { left = x + r.left, top = y + r.top, right = x + r.right, bottom = y + r.bottom },
                radiusX = 3,
                radiusY = 3,
            };
            rt->FillRoundedRectangle(&rr, (ID2D1Brush*)b.InlineCodeBackground);
        }
    }

    /// <summary>Rounds a DIP coordinate to the device pixel grid.</summary>
    public static float Snap(float v, float px) => MathF.Round(v / px) * px;

    /// <summary>1 device pixel high horizontal line whose top edge is at <paramref name="y"/> (snapped).</summary>
    private static void HLine(ID2D1RenderTarget* rt, float x1, float x2, float y, ID2D1SolidColorBrush* brush, float px)
    {
        float sy = Snap(y, px);
        var r = new D2D_RECT_F { left = Snap(x1, px), top = sy, right = Snap(x2, px), bottom = sy + px };
        rt->FillRectangle(&r, (ID2D1Brush*)brush);
    }

    private static void VLine(ID2D1RenderTarget* rt, float x, float y1, float y2, ID2D1SolidColorBrush* brush, float px)
    {
        float sx = Snap(x, px);
        var r = new D2D_RECT_F { left = sx, top = Snap(y1, px), right = sx + px, bottom = Snap(y2, px) };
        rt->FillRectangle(&r, (ID2D1Brush*)brush);
    }
}
