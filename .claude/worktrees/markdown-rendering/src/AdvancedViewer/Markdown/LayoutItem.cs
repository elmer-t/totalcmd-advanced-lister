using System;
using System.Collections.Generic;
using AdvancedViewer.Markdown.Model;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;

namespace AdvancedViewer.Markdown;

internal enum ItemKind : byte
{
    Paragraph,
    Heading,
    /// <summary>A chunk of a fenced/indented code block (long blocks are split, see <see cref="ItemBuilder.CodeChunkLines"/>).</summary>
    Code,
    /// <summary>A chunk of a raw HTML block: monospace, muted, no background.</summary>
    Html,
    TableRow,
    ThematicBreak,
    Image,
}

[Flags]
internal enum ItemFlags : byte
{
    None = 0,
    /// <summary>First chunk of a code block (top padding, top of the background).</summary>
    FirstChunk = 1,
    /// <summary>Last chunk of a code block (bottom padding).</summary>
    LastChunk = 2,
    /// <summary><see cref="LayoutItem.Height"/> is measured at <see cref="MarkdownView"/>'s current width (else it is an estimate).</summary>
    Measured = 4,
}

internal enum MarkerKind : byte { Bullet, Ordered, Task }

/// <summary>A list marker ("•", "3.", "☑") drawn in the marker column left of an item's content.</summary>
internal readonly struct Marker
{
    public Marker(string text, float x, float width, MarkerKind kind)
    {
        Text = text;
        X = x;
        Width = width;
        Kind = kind;
    }

    public readonly string Text;
    /// <summary>Left edge of the marker column, relative to the content left.</summary>
    public readonly float X;
    /// <summary>Width of the marker column (the marker is right-aligned in it, minus a small gap).</summary>
    public readonly float Width;
    public readonly MarkerKind Kind;
}

/// <summary>Per-table data shared by its row items: natural column widths and the fitted widths for one content width.</summary>
internal sealed class TableInfo
{
    public TableInfo(Table table) => Table = table;

    public readonly Table Table;
    /// <summary>Unconstrained cell width per column (max over rows), including cell padding. Null until measured.</summary>
    public float[]? Natural;
    /// <summary>Longest word per column (max over rows), including cell padding.</summary>
    public float[]? Minimum;
    /// <summary>Fitted column widths including cell padding, for <see cref="FittedFor"/>.</summary>
    public float[]? Columns;
    public float FittedFor = -1;
    /// <summary>Sum of <see cref="Columns"/>.</summary>
    public float Width;
}

/// <summary>
/// One entry of the flattened document: a leaf block (or a chunk of one, or a table row) with
/// everything needed to lay it out and paint it without walking the block tree: x indent from
/// nested lists/quotes, the space above it, the quote bars and list markers beside it. While it
/// is near the viewport its DirectWrite layouts live in a pooled <see cref="LiveLayouts"/> slot.
/// Stored by value in one array, so a 32 MB file with ~600k items is one allocation instead of
/// 600k objects.
/// </summary>
internal struct LayoutItem
{
    public ItemKind Kind;
    public ItemFlags Flags;
    /// <summary>Heading level 1..6.</summary>
    public byte Level;

    /// <summary>Left edge of the content, relative to the content column's left (lists, quotes).</summary>
    public float Indent;
    /// <summary>Vertical space above the item (DIP).</summary>
    public float Gap;
    /// <summary>Height without <see cref="Gap"/> (DIP): measured, or an estimate until <see cref="ItemFlags.Measured"/>.</summary>
    public float Height;

    /// <summary>X of each enclosing quote's bar, relative to the content left (outermost first). Shared between items.</summary>
    public float[]? QuoteBars;
    /// <summary>Bars with index ≥ this start at this item (their bar skips the gap above it).</summary>
    public int QuoteStartDepth;

    /// <summary>Up to two list markers (an item whose first child is itself a list item gets two).</summary>
    public Marker[]? Markers;

    /// <summary>Paragraph/heading text.</summary>
    public InlineText? Inline;
    /// <summary>Code/HTML/image text; the item shows [<see cref="TextStart"/>, +<see cref="TextLength"/>).</summary>
    public string? Plain;
    public int TextStart;
    public int TextLength;
    /// <summary>Line breaks in the shown text + 1 (height estimates).</summary>
    public int Lines;

    public TableInfo? Table;
    public int Row;

    /// <summary>1 + index of the <see cref="LiveLayouts"/> slot holding this item's layouts; 0 = none.</summary>
    public int Live;

    public readonly bool IsMeasured => (Flags & ItemFlags.Measured) != 0;
    public readonly float Extent => Gap + Height;
}

/// <summary>
/// The DirectWrite objects of one item near the viewport. Slots are pooled and reused by
/// <see cref="MarkdownView"/>, so scrolling does not allocate once the pool has grown to the
/// largest screenful.
/// </summary>
internal sealed unsafe class LiveLayouts
{
    /// <summary>Item index this slot belongs to (-1 = free).</summary>
    public int Item = -1;
    /// <summary>Main text layout (paragraph, heading, code, HTML, image label); null for rules and table rows.</summary>
    public IDWriteTextLayout* Layout;
    /// <summary>Table cell layouts (IDWriteTextLayout*); <see cref="CellCount"/> are in use.</summary>
    public nint[] Cells = new nint[4];
    public int CellCount;
    /// <summary>Marker layouts, parallel to <see cref="LayoutItem.Markers"/>.</summary>
    public nint[] Markers = new nint[2];
    public int MarkerCount;
    /// <summary>Inline code backgrounds, relative to the item's content origin (x = Indent, y = after Gap).</summary>
    public D2D_RECT_F[] CodeRects = new D2D_RECT_F[4];
    public int CodeRectCount;
    /// <summary>Brush generation the drawing effects were applied with (0 = none).</summary>
    public int EffectsGen;

    /// <summary>Releases every layout (exactly one Release each) and marks the slot free.</summary>
    public void Release()
    {
        if (Layout != null) { Layout->Release(); Layout = null; }
        for (int i = 0; i < CellCount; i++)
        {
            ((IDWriteTextLayout*)Cells[i])->Release();
            Cells[i] = 0;
        }
        CellCount = 0;
        for (int i = 0; i < MarkerCount; i++)
        {
            ((IDWriteTextLayout*)Markers[i])->Release();
            Markers[i] = 0;
        }
        MarkerCount = 0;
        CodeRectCount = 0;
        EffectsGen = 0;
        Item = -1;
    }

    public void AddCell(IDWriteTextLayout* layout)
    {
        if (CellCount == Cells.Length) Array.Resize(ref Cells, Cells.Length * 2);
        Cells[CellCount++] = (nint)layout;
    }

    public void AddCodeRect(D2D_RECT_F r)
    {
        if (CodeRectCount == CodeRects.Length) Array.Resize(ref CodeRects, CodeRects.Length * 2);
        CodeRects[CodeRectCount++] = r;
    }
}

/// <summary>
/// Flattens a <see cref="Document"/> into <see cref="LayoutItem"/>s: containers (quotes, lists,
/// list items) disappear and become indents, bars and markers on the leaf items inside them.
/// Pure managed code; runs once per Load.
/// </summary>
internal sealed class ItemBuilder
{
    /// <summary>Code and HTML blocks are split into items of at most this many lines, so a huge block never becomes one huge layout.</summary>
    public const int CodeChunkLines = 200;

    public const float ParagraphGap = 10.5f;   // 0.75 em
    public const float TightItemGap = 3f;
    public const float QuoteIndent = 16f;
    public const float QuoteBarWidth = 4f;
    public const float MarkerColumn = 24f;
    public const float ThematicBreakHeight = 17f;

    private static readonly string[] s_bullets = { "•", "◦", "▪" }; // • ◦ ▪

    private LayoutItem[] _items = new LayoutItem[64];
    private int _count;

    private float _carryGap = -1;
    private int _quoteStart = int.MaxValue;
    private Marker _pending0, _pending1;
    private int _pendingCount;

    /// <summary>Items so far; the array is replaced when it grows, so re-read it after <see cref="Append"/>.</summary>
    public LayoutItem[] Items => _items;
    public int Count => _count;

    /// <summary>Appends the items of <paramref name="doc"/> (a whole document, or the next chunk of one, see <see cref="ChunkSplitter"/>).</summary>
    public void Append(Document doc)
    {
        _topLevel = true;
        Blocks(doc.Blocks, 0f, null, inTightItem: false, listDepth: 0);
    }

    /// <summary>Drops the unused tail of the item array (after the last <see cref="Append"/>).</summary>
    public void Trim()
    {
        if (_items.Length > _count) Array.Resize(ref _items, Math.Max(_count, 1));
    }

    private bool _topLevel;

    private void Blocks(List<Block> blocks, float indent, float[]? bars, bool inTightItem, int listDepth)
    {
        for (int j = 0; j < blocks.Count; j++)
        {
            Block block = blocks[j];
            float gap;
            bool continuesDocument = _topLevel && _count > 0; // first block of a later chunk
            _topLevel = false;
            if (j == 0 && !continuesDocument) gap = 0;
            else if (block is Heading h) gap = h.Level <= 2 ? 24f : 20f;
            else if (inTightItem) gap = TightItemGap;
            else gap = ParagraphGap;
            Carry(gap);
            One(block, indent, bars, inTightItem, listDepth);
        }
    }

    private void Carry(float gap) => _carryGap = Math.Max(_carryGap, gap);

    private void One(Block block, float indent, float[]? bars, bool inTightItem, int listDepth)
    {
        switch (block)
        {
            case Paragraph p:
                Emit(new LayoutItem { Kind = ItemKind.Paragraph, Inline = p.Text, Lines = CountLines(p.Text.Text, 0, p.Text.Text.Length) }, indent, bars);
                break;

            case Heading h:
                Emit(new LayoutItem { Kind = ItemKind.Heading, Level = (byte)h.Level, Inline = h.Text, Lines = 1 }, indent, bars);
                break;

            case Model.CodeBlock c:
                Chunks(ItemKind.Code, c.Code, indent, bars);
                break;

            case Model.HtmlBlock html:
                Chunks(ItemKind.Html, html.Html, indent, bars);
                break;

            case ImageBlock img:
            {
                string label = "Image: " + (img.Alt.Length > 0 ? img.Alt : img.Url.Length > 0 ? img.Url : "(no description)");
                Emit(new LayoutItem { Kind = ItemKind.Image, Plain = label, TextLength = label.Length, Lines = 1 }, indent, bars);
                break;
            }

            case ThematicBreak:
                Emit(new LayoutItem { Kind = ItemKind.ThematicBreak, Height = ThematicBreakHeight }, indent, bars);
                break;

            case Model.Table t:
            {
                var info = new TableInfo(t);
                for (int r = 0; r < t.Rows.Count; r++)
                {
                    int lines = 1;
                    foreach (InlineText cell in t.Rows[r].Cells)
                        lines = Math.Max(lines, CountLines(cell.Text, 0, cell.Text.Length));
                    if (r > 0) Carry(0);
                    Emit(new LayoutItem { Kind = ItemKind.TableRow, Table = info, Row = r, Lines = lines }, indent, bars);
                }
                break;
            }

            case BlockQuote q:
            {
                float[] inner = new float[(bars?.Length ?? 0) + 1];
                bars?.CopyTo(inner, 0);
                inner[^1] = indent;
                _quoteStart = Math.Min(_quoteStart, inner.Length - 1);
                if (q.Children.Count == 0)
                    EmitEmpty(indent + QuoteIndent, inner);
                else
                    Blocks(q.Children, indent + QuoteIndent, inner, inTightItem: false, listDepth);
                break;
            }

            case Model.ListBlock list:
            {
                float column = MarkerColumn;
                if (list.Ordered)
                {
                    long last = (long)list.Start + Math.Max(0, list.Items.Count - 1);
                    int digits = last.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
                    column += Math.Max(0, digits - 2) * 8f;
                }
                for (int k = 0; k < list.Items.Count; k++)
                {
                    ListItem item = list.Items[k];
                    if (k > 0) Carry(list.Tight ? TightItemGap : ParagraphGap);
                    string text;
                    MarkerKind kind;
                    if (item.Checked is bool done)
                    {
                        text = done ? "☑" : "☐"; // ☑ ☐
                        kind = MarkerKind.Task;
                    }
                    else if (list.Ordered)
                    {
                        text = ((long)list.Start + k).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
                        kind = MarkerKind.Ordered;
                    }
                    else
                    {
                        text = s_bullets[listDepth % s_bullets.Length];
                        kind = MarkerKind.Bullet;
                    }
                    AddMarker(new Marker(text, indent, column, kind));
                    if (item.Children.Count == 0)
                        EmitEmpty(indent + column, bars);
                    else
                        Blocks(item.Children, indent + column, bars, list.Tight, listDepth + 1);
                }
                break;
            }

            default:
                break; // the model has no other block types
        }
    }

    private void AddMarker(Marker m)
    {
        if (_pendingCount == 0) _pending0 = m;
        else if (_pendingCount == 1) _pending1 = m;
        else return; // a third nested marker on one line is not shown
        _pendingCount++;
    }

    private void EmitEmpty(float indent, float[]? bars) =>
        Emit(new LayoutItem { Kind = ItemKind.Paragraph, Inline = InlineText.CreateEmpty(), Lines = 1 }, indent, bars);

    private void Chunks(ItemKind kind, string text, float indent, float[]? bars)
    {
        int start = 0;
        bool first = true;
        while (true)
        {
            // Find the end of the next chunk of CodeChunkLines lines.
            int end = start, lines = 1;
            while (end < text.Length)
            {
                if (text[end] == '\n')
                {
                    if (lines == CodeChunkLines) break;
                    lines++;
                }
                end++;
            }
            bool last = end >= text.Length;
            var item = new LayoutItem
            {
                Kind = kind,
                Plain = text,
                TextStart = start,
                TextLength = end - start,
                Lines = lines,
                Flags = (first ? ItemFlags.FirstChunk : 0) | (last ? ItemFlags.LastChunk : 0),
            };
            if (!first) Carry(0);
            Emit(item, indent, bars);
            if (last) break;
            start = end + 1; // skip the '\n' between chunks
            first = false;
        }
    }

    private void Emit(LayoutItem item, float indent, float[]? bars)
    {
        item.Indent = indent;
        item.QuoteBars = bars;
        item.QuoteStartDepth = _quoteStart;
        item.Gap = _count == 0 ? 0 : Math.Max(0, _carryGap);
        if (_pendingCount > 0)
        {
            item.Markers = _pendingCount == 1 ? new[] { _pending0 } : new[] { _pending0, _pending1 };
            _pendingCount = 0;
        }
        _carryGap = -1;
        _quoteStart = int.MaxValue;
        if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
        _items[_count++] = item;
    }

    private static int CountLines(string s, int start, int length)
    {
        int n = 1;
        int end = start + length;
        for (int i = start; i < end; i++)
            if (s[i] == '\n') n++;
        return n;
    }
}
