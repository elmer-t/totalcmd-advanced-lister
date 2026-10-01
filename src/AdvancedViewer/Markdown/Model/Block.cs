using System.Collections.Generic;

namespace AdvancedViewer.Markdown.Model;

/// <summary>Base of every block. The concrete type says how it is laid out.</summary>
public abstract class Block
{
}

/// <summary>ATX or setext heading, <see cref="Level"/> 1..6.</summary>
public sealed class Heading : Block
{
    public Heading(int level, InlineText text)
    {
        Level = level < 1 ? 1 : level > 6 ? 6 : level;
        Text = text;
    }

    public int Level { get; }
    public InlineText Text { get; }
}

public sealed class Paragraph : Block
{
    public Paragraph(InlineText text) => Text = text;

    public InlineText Text { get; }
}

/// <summary>Fenced or indented code. <see cref="Code"/> uses '\n' line ends and has no trailing newline.</summary>
public sealed class CodeBlock : Block
{
    public CodeBlock(string code, string? language)
    {
        Code = code;
        Language = language;
    }

    public string Code { get; }

    /// <summary>First word of the fence info string ("csharp" for <c>```csharp title</c>), or null.</summary>
    public string? Language { get; }
}

public sealed class BlockQuote : Block
{
    public List<Block> Children { get; } = new();
}

public sealed class ListBlock : Block
{
    public ListBlock(bool ordered, int start, bool tight)
    {
        Ordered = ordered;
        Start = start;
        Tight = tight;
    }

    public bool Ordered { get; }

    /// <summary>Number of the first item of an ordered list (may be 0); 1 for bullet lists.</summary>
    public int Start { get; }

    /// <summary>True when no blank lines separate the items or their children (CommonMark "tight").</summary>
    public bool Tight { get; }

    public List<ListItem> Items { get; } = new();
}

public sealed class ListItem
{
    public ListItem(bool? isChecked) => Checked = isChecked;

    /// <summary>null for a normal item; true/false for a task item ("- [x]" / "- [ ]").</summary>
    public bool? Checked { get; }

    public List<Block> Children { get; } = new();
}

/// <summary>
/// GFM pipe table. <c>Rows[0]</c> is the header row. Every row has exactly
/// <c>Aligns.Length</c> cells (short rows are padded with empty cells).
/// </summary>
public sealed class Table : Block
{
    public Table(ColumnAlign[] aligns) => Aligns = aligns;

    public List<TableRow> Rows { get; } = new();
    public ColumnAlign[] Aligns { get; }
}

public sealed class TableRow
{
    public List<InlineText> Cells { get; } = new();
}

public sealed class ThematicBreak : Block
{
}

/// <summary>Raw HTML block, shown as dim monospace text. '\n' line ends, no trailing newline.</summary>
public sealed class HtmlBlock : Block
{
    public HtmlBlock(string html) => Html = html;

    public string Html { get; }
}

/// <summary>A paragraph whose only content is one image.</summary>
public sealed class ImageBlock : Block
{
    public ImageBlock(string alt, string url)
    {
        Alt = alt;
        Url = url;
    }

    public string Alt { get; }
    public string Url { get; }
}
