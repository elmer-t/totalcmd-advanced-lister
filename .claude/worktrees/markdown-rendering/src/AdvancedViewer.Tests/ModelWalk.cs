using System.Collections.Generic;
using System.Text;
using AdvancedViewer.Markdown.Model;

namespace AdvancedViewer.Tests;

/// <summary>Helpers that walk a parsed document: every block, every InlineText, a text dump.</summary>
internal static class ModelWalk
{
    public static IEnumerable<Block> AllBlocks(IEnumerable<Block> blocks)
    {
        foreach (Block b in blocks)
        {
            yield return b;
            IEnumerable<Block>? children = b switch
            {
                BlockQuote q => q.Children,
                _ => null,
            };
            if (children is not null)
                foreach (Block c in AllBlocks(children)) yield return c;
            if (b is ListBlock l)
                foreach (ListItem item in l.Items)
                    foreach (Block c in AllBlocks(item.Children)) yield return c;
        }
    }

    public static IEnumerable<InlineText> AllInlines(Document doc)
    {
        foreach (Block b in AllBlocks(doc.Blocks))
        {
            switch (b)
            {
                case Heading h: yield return h.Text; break;
                case Paragraph p: yield return p.Text; break;
                case Table t:
                    foreach (TableRow r in t.Rows)
                        foreach (InlineText c in r.Cells) yield return c;
                    break;
            }
        }
    }

    /// <summary>Compact text form of a document, for diagnostics in failing asserts.</summary>
    public static string Dump(Document doc)
    {
        var sb = new StringBuilder();
        DumpBlocks(doc.Blocks, sb, 0);
        return sb.ToString();
    }

    private static void DumpBlocks(List<Block> blocks, StringBuilder sb, int indent)
    {
        foreach (Block b in blocks)
        {
            sb.Append(' ', indent * 2);
            switch (b)
            {
                case Heading h: sb.Append("H").Append(h.Level).Append(' ').AppendLine(Runs(h.Text)); break;
                case Paragraph p: sb.Append("P ").AppendLine(Runs(p.Text)); break;
                case CodeBlock c: sb.Append("Code(").Append(c.Language).Append(") ").AppendLine(c.Code.Replace("\n", "\\n")); break;
                case HtmlBlock html: sb.Append("Html ").AppendLine(html.Html.Replace("\n", "\\n")); break;
                case ImageBlock img: sb.Append("Image ").Append(img.Alt).Append(" -> ").AppendLine(img.Url); break;
                case ThematicBreak: sb.AppendLine("HR"); break;
                case BlockQuote q: sb.AppendLine("Quote"); DumpBlocks(q.Children, sb, indent + 1); break;
                case ListBlock l:
                    sb.Append(l.Ordered ? "OL start=" + l.Start : "UL").Append(l.Tight ? " tight" : " loose").AppendLine();
                    foreach (ListItem item in l.Items)
                    {
                        sb.Append(' ', indent * 2 + 2).Append("Item").AppendLine(item.Checked is null ? "" : item.Checked.Value ? " [x]" : " [ ]");
                        DumpBlocks(item.Children, sb, indent + 2);
                    }
                    break;
                case Table t:
                    sb.Append("Table ").AppendLine(string.Join(",", t.Aligns));
                    foreach (TableRow r in t.Rows)
                    {
                        sb.Append(' ', indent * 2 + 2);
                        foreach (InlineText c in r.Cells) sb.Append("| ").Append(Runs(c)).Append(' ');
                        sb.AppendLine("|");
                    }
                    break;
                default: sb.AppendLine(b.GetType().Name); break;
            }
        }
    }

    public static string Runs(InlineText t)
    {
        var sb = new StringBuilder();
        foreach (InlineRun r in t.Runs)
        {
            string s = t.Text.Substring(r.Start, r.Length).Replace("\n", "\\n");
            if (r.Style == StyleFlags.None) sb.Append(s);
            else
            {
                sb.Append('{').Append(r.Style).Append(':').Append(s);
                if (r.LinkUrl is not null) sb.Append(" @").Append(r.LinkUrl);
                sb.Append('}');
            }
        }
        return sb.ToString();
    }
}
