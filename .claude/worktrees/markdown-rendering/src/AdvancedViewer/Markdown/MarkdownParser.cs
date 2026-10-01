using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AdvancedViewer.Markdown.Model;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = AdvancedViewer.Markdown.Model.Block;
using MdCodeBlock = Markdig.Syntax.CodeBlock;
using MdHtmlBlock = Markdig.Syntax.HtmlBlock;
using MdListBlock = Markdig.Syntax.ListBlock;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using ModelCodeBlock = AdvancedViewer.Markdown.Model.CodeBlock;
using ModelHtmlBlock = AdvancedViewer.Markdown.Model.HtmlBlock;
using ModelListBlock = AdvancedViewer.Markdown.Model.ListBlock;
using ModelTable = AdvancedViewer.Markdown.Model.Table;
using ModelTableRow = AdvancedViewer.Markdown.Model.TableRow;

namespace AdvancedViewer.Markdown;

/// <summary>
/// Markdown text → <see cref="Document"/>, via Markdig.
/// <para>
/// Pipeline: CommonMark + pipe tables (Markdig's GFM rules: cells split before inline parsing,
/// rows padded or truncated to the header width), task lists, bare-URL autolinks, <c>~~strikethrough~~</c>,
/// YAML front matter (parsed and skipped). Not enabled: grid tables (rare in real files, and the
/// model has no row/column spans), subscript/superscript/inserted/marked (<c>~x~ ^x^ ++x++ ==x==</c>
/// are not GFM, so they stay literal text, which is also what GitHub shows for most of them;
/// a single <c>~x~</c> therefore is not struck through, unlike GitHub), footnotes, emoji,
/// smarty pants.
/// </para>
/// <para>
/// <see cref="Parse"/> never throws: any failure (including Markdig's nesting-depth limit) falls
/// back to a document with one <see cref="ModelCodeBlock"/> holding the raw text.
/// </para>
/// </summary>
public static class MarkdownParser
{
    /// <summary>Containers nested deeper than this are flattened to plain text (stack safety).</summary>
    private const int MaxDepth = 64;

    private static MarkdownPipeline? s_pipeline;

    private static MarkdownPipeline Pipeline => s_pipeline ??= new MarkdownPipelineBuilder()
        .UsePipeTables(new PipeTableOptions { UseGfmRules = true })
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseYamlFrontMatter()
        .Build();

    public static Document Parse(string text)
    {
        text ??= string.Empty;
        try
        {
            text = TextDecoder.NormalizeNewlines(text);
            MarkdownDocument md = Markdig.Markdown.Parse(text, Pipeline);
            var doc = new Document();
            var converter = new Converter(text);
            converter.ConvertBlocks(md, doc.Blocks, 0);
            return doc;
        }
        catch (Exception)
        {
            return Fallback(text);
        }
    }

    private static Document Fallback(string text)
    {
        var doc = new Document();
        try
        {
            doc.Blocks.Add(new ModelCodeBlock(TextDecoder.NormalizeNewlines(text).TrimEnd('\n'), null));
        }
        catch (Exception)
        {
            // Out of memory or similar: an empty document is still a valid document.
        }
        return doc;
    }

    private sealed class Converter
    {
        private readonly string _source;
        private readonly InlineBuilder _inline = new();

        public Converter(string source) => _source = source;

        public void ConvertBlocks(ContainerBlock container, List<Block> output, int depth)
        {
            for (int i = 0; i < container.Count; i++)
                ConvertBlock(container[i], output, depth);
        }

        private void ConvertBlock(Markdig.Syntax.Block block, List<Block> output, int depth)
        {
            if (depth > MaxDepth)
            {
                AddPlain(SourceText(block), output);
                return;
            }

            switch (block)
            {
                case YamlFrontMatterBlock:
                case LinkReferenceDefinitionGroup:
                case LinkReferenceDefinition:
                case BlankLineBlock:
                    return;

                case HeadingBlock h:
                    output.Add(new Heading(h.Level, BuildInline(h.Inline, skipTask: false)));
                    return;

                case ParagraphBlock p:
                    ConvertParagraph(p, output, skipTask: false);
                    return;

                case MdCodeBlock c: // also FencedCodeBlock
                    output.Add(new ModelCodeBlock(LinesText(c.Lines), c is FencedCodeBlock f ? FirstWord(f.Info) : null));
                    return;

                case MdHtmlBlock html:
                    output.Add(new ModelHtmlBlock(LinesText(html.Lines)));
                    return;

                case ThematicBreakBlock:
                    output.Add(new ThematicBreak());
                    return;

                case QuoteBlock q:
                {
                    var quote = new BlockQuote();
                    ConvertBlocks(q, quote.Children, depth + 1);
                    output.Add(quote);
                    return;
                }

                case MdListBlock l:
                    output.Add(ConvertList(l, depth));
                    return;

                case MdTable t:
                    output.Add(ConvertTable(t));
                    return;

                case LeafBlock leaf:
                    // Unknown leaf: its inlines, or its raw lines, as a paragraph.
                    if (leaf.Inline is not null) ConvertParagraphInline(leaf.Inline, output);
                    else AddPlain(LinesText(leaf.Lines), output);
                    return;

                case ContainerBlock other:
                    ConvertBlocks(other, output, depth + 1);
                    return;

                default:
                    AddPlain(SourceText(block), output);
                    return;
            }
        }

        private void ConvertParagraph(ParagraphBlock p, List<Block> output, bool skipTask)
        {
            if (p.Inline is null) return;
            if (!skipTask && TryGetSoleImage(p.Inline, out LinkInline? image))
            {
                output.Add(new ImageBlock(PlainText(image!), LinkUrl(image!)));
                return;
            }
            InlineText text = BuildInline(p.Inline, skipTask);
            if (text.Text.Length > 0) output.Add(new Paragraph(text));
        }

        private void ConvertParagraphInline(ContainerInline inline, List<Block> output)
        {
            InlineText text = BuildInline(inline, skipTask: false);
            if (text.Text.Length > 0) output.Add(new Paragraph(text));
        }

        private ModelListBlock ConvertList(MdListBlock l, int depth)
        {
            int start = 1;
            if (l.IsOrdered)
            {
                string? s = l.OrderedStart ?? l.DefaultOrderedStart;
                if (s is null || !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out start))
                    start = 1;
            }

            var list = new ModelListBlock(l.IsOrdered, start, tight: !l.IsLoose);
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i] is not ListItemBlock itemBlock)
                {
                    // Not expected; keep the content in an item of its own.
                    var stray = new ListItem(null);
                    ConvertBlock(l[i], stray.Children, depth + 1);
                    list.Items.Add(stray);
                    continue;
                }

                bool? isChecked = null;
                ParagraphBlock? taskParagraph = null;
                if (itemBlock.Count > 0 && itemBlock[0] is ParagraphBlock first
                    && first.Inline?.FirstChild is TaskList task)
                {
                    isChecked = task.Checked;
                    taskParagraph = first;
                }

                var item = new ListItem(isChecked);
                for (int j = 0; j < itemBlock.Count; j++)
                {
                    if (j == 0 && taskParagraph is not null)
                        ConvertParagraph(taskParagraph, item.Children, skipTask: true);
                    else
                        ConvertBlock(itemBlock[j], item.Children, depth + 1);
                }
                list.Items.Add(item);
            }
            return list;
        }

        private ModelTable ConvertTable(MdTable t)
        {
            int columns = 0;
            for (int r = 0; r < t.Count; r++)
                if (t[r] is MdTableRow row) columns = Math.Max(columns, row.Count);
            if (columns == 0) columns = Math.Max(1, t.ColumnDefinitions.Count);

            var aligns = new ColumnAlign[columns];
            for (int c = 0; c < columns && c < t.ColumnDefinitions.Count; c++)
            {
                aligns[c] = t.ColumnDefinitions[c].Alignment switch
                {
                    TableColumnAlign.Left => ColumnAlign.Left,
                    TableColumnAlign.Center => ColumnAlign.Center,
                    TableColumnAlign.Right => ColumnAlign.Right,
                    _ => ColumnAlign.None,
                };
            }

            var table = new ModelTable(aligns);
            // Header rows first (Markdig puts them first anyway), then the body in order.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int r = 0; r < t.Count; r++)
                {
                    if (t[r] is not MdTableRow row || row.IsHeader != (pass == 0)) continue;
                    var outRow = new ModelTableRow();
                    for (int c = 0; c < columns; c++)
                        outRow.Cells.Add(c < row.Count && row[c] is ContainerBlock cell ? CellText(cell) : InlineText.CreateEmpty());
                    table.Rows.Add(outRow);
                }
            }
            if (table.Rows.Count == 0)
            {
                var empty = new ModelTableRow();
                for (int c = 0; c < columns; c++) empty.Cells.Add(InlineText.CreateEmpty());
                table.Rows.Add(empty);
            }
            return table;
        }

        private InlineText CellText(ContainerBlock cell)
        {
            _inline.Reset();
            for (int i = 0; i < cell.Count; i++)
            {
                if (i > 0) _inline.Append(" ", StyleFlags.None, null);
                if (cell[i] is LeafBlock leaf)
                {
                    if (leaf.Inline is not null) AppendInlines(leaf.Inline, StyleFlags.None, null, 0, skipTask: false);
                    else _inline.Append(LinesText(leaf.Lines), StyleFlags.None, null);
                }
                else
                {
                    _inline.Append(SourceText(cell[i]), StyleFlags.None, null);
                }
            }
            return _inline.Build();
        }

        // ---- inlines --------------------------------------------------------------------------

        private InlineText BuildInline(ContainerInline? inline, bool skipTask)
        {
            _inline.Reset();
            if (inline is not null) AppendInlines(inline, StyleFlags.None, null, 0, skipTask);
            return _inline.Build();
        }

        private void AppendInlines(ContainerInline container, StyleFlags style, string? url, int depth, bool skipTask)
        {
            Inline? child = container.FirstChild;
            if (skipTask && child is TaskList)
            {
                child = child.NextSibling;
                _inline.SkipLeadingSpace = true;
            }
            for (; child is not null; child = child.NextSibling)
                AppendInline(child, style, url, depth);
        }

        private void AppendInline(Inline inline, StyleFlags style, string? url, int depth)
        {
            if (depth > MaxDepth)
            {
                _inline.Append(SourceText(inline), style, url);
                return;
            }

            switch (inline)
            {
                case LiteralInline lit:
                    _inline.Append(lit.Content.AsSpan(), style, url);
                    return;

                case HtmlEntityInline entity:
                    _inline.Append(entity.Transcoded.AsSpan(), style, url);
                    return;

                case CodeInline code:
                    _inline.Append(code.Content, style | StyleFlags.Code, url);
                    return;

                case LineBreakInline br:
                    _inline.Append(br.IsHard ? "\n" : " ", style, url);
                    return;

                case HtmlInline html:
                    _inline.Append(html.Tag, style | StyleFlags.Code, url);
                    return;

                case AutolinkInline auto:
                {
                    string target = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                        ? "mailto:" + auto.Url
                        : auto.Url;
                    _inline.Append(auto.Url, style | StyleFlags.Link, url ?? target);
                    return;
                }

                case LinkInline link when link.IsImage:
                    _inline.Append("[", style | StyleFlags.ImagePlaceholder, url);
                    _inline.Append(PlainText(link), style | StyleFlags.ImagePlaceholder, url);
                    _inline.Append("]", style | StyleFlags.ImagePlaceholder, url);
                    return;

                case LinkInline link:
                    // A link inside a link cannot happen in CommonMark; the outer URL would win.
                    AppendInlines(link, style | StyleFlags.Link, url ?? LinkUrl(link), depth + 1, skipTask: false);
                    return;

                case EmphasisInline em:
                    AppendInlines(em, style | EmphasisStyle(em), url, depth + 1, skipTask: false);
                    return;

                case TaskList task:
                    // Only reached when not at the start of a list item (not expected).
                    _inline.Append(task.Checked ? "[x]" : "[ ]", style, url);
                    return;

                case DelimiterInline delim:
                    // Unmatched delimiter left in the tree: its literal text, then its children.
                    _inline.Append(SafeToLiteral(delim), style, url);
                    AppendInlines(delim, style, url, depth + 1, skipTask: false);
                    return;

                case ContainerInline other:
                    AppendInlines(other, style, url, depth + 1, skipTask: false);
                    return;

                default:
                    _inline.Append(SourceText(inline), style, url);
                    return;
            }
        }

        private static StyleFlags EmphasisStyle(EmphasisInline em)
        {
            switch (em.DelimiterChar)
            {
                case '*':
                case '_':
                    return em.DelimiterCount >= 3 ? StyleFlags.Bold | StyleFlags.Italic
                        : em.DelimiterCount == 2 ? StyleFlags.Bold
                        : StyleFlags.Italic;
                case '~':
                    return em.DelimiterCount >= 2 ? StyleFlags.Strike : StyleFlags.None;
                default:
                    // '^', '+', '=' are not enabled; if they ever appear they render as plain text.
                    return StyleFlags.None;
            }
        }

        private static bool TryGetSoleImage(ContainerInline container, out LinkInline? image)
        {
            image = null;
            for (Inline? c = container.FirstChild; c is not null; c = c.NextSibling)
            {
                if (c is LinkInline { IsImage: true } li && image is null) { image = li; continue; }
                if (c is LiteralInline lit && IsWhitespace(lit.Content)) continue;
                image = null;
                return false;
            }
            return image is not null;
        }

        private static bool IsWhitespace(StringSlice s)
        {
            for (int i = s.Start; i <= s.End; i++)
                if (!char.IsWhiteSpace(s.Text[i])) return false;
            return true;
        }

        private static string LinkUrl(LinkInline link)
        {
            string? u = null;
            try { u = link.GetDynamicUrl?.Invoke(); } catch (Exception) { }
            return u ?? link.Url ?? string.Empty;
        }

        /// <summary>Plain text of a container's inlines (image alt text), without styles.</summary>
        private static string PlainText(ContainerInline container)
        {
            var sb = new StringBuilder();
            AppendPlain(container, sb, 0);
            return sb.ToString();
        }

        private static void AppendPlain(ContainerInline container, StringBuilder sb, int depth)
        {
            if (depth > MaxDepth) return;
            for (Inline? c = container.FirstChild; c is not null; c = c.NextSibling)
            {
                switch (c)
                {
                    case LiteralInline lit: sb.Append(lit.Content.AsSpan()); break;
                    case HtmlEntityInline e: sb.Append(e.Transcoded.AsSpan()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case LineBreakInline br: sb.Append(br.IsHard ? '\n' : ' '); break;
                    case AutolinkInline a: sb.Append(a.Url); break;
                    case HtmlInline h: sb.Append(h.Tag); break;
                    case DelimiterInline d: sb.Append(SafeToLiteral(d)); AppendPlain(d, sb, depth + 1); break;
                    case ContainerInline ci: AppendPlain(ci, sb, depth + 1); break;
                }
            }
        }

        private static string SafeToLiteral(DelimiterInline d)
        {
            try { return d.ToLiteral() ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        /// <summary>The original source text of a node (fallback for unknown node types).</summary>
        private string SourceText(MarkdownObject node)
        {
            SourceSpan span = node.Span;
            if (span.IsEmpty || span.Start < 0 || span.End >= _source.Length || span.End < span.Start)
                return string.Empty;
            return _source.Substring(span.Start, span.Length);
        }

        private static string LinesText(StringLineGroup lines)
        {
            if (lines.Count == 0 || lines.Lines is null) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(lines.Lines[i].Slice.AsSpan());
            }
            int end = sb.Length;
            while (end > 0 && sb[end - 1] == '\n') end--;
            sb.Length = end;
            return sb.ToString();
        }

        private static string? FirstWord(string? info)
        {
            if (string.IsNullOrWhiteSpace(info)) return null;
            string s = info.Trim();
            int sp = 0;
            while (sp < s.Length && !char.IsWhiteSpace(s[sp])) sp++;
            return s.Substring(0, sp);
        }

        private static void AddPlain(string text, List<Block> output)
        {
            if (text.Length > 0) output.Add(new Paragraph(InlineText.CreatePlain(text)));
        }
    }

    /// <summary>Accumulates display text and merged runs for one <see cref="InlineText"/>.</summary>
    private sealed class InlineBuilder
    {
        private readonly StringBuilder _text = new();
        private List<InlineRun> _runs = new();

        /// <summary>Drop one leading space of the next appended text (after a task-list marker).</summary>
        public bool SkipLeadingSpace;

        public void Reset()
        {
            _text.Clear();
            _runs = new List<InlineRun>();
            SkipLeadingSpace = false;
        }

        public void Append(string? s, StyleFlags style, string? url) => Append(s.AsSpan(), style, url);

        public void Append(ReadOnlySpan<char> s, StyleFlags style, string? url)
        {
            if (SkipLeadingSpace && s.Length > 0)
            {
                SkipLeadingSpace = false;
                while (s.Length > 0 && (s[0] == ' ' || s[0] == '\t')) s = s[1..];
            }
            if (s.IsEmpty) return;

            // Link flag and URL always travel together.
            if (url is null) style &= ~StyleFlags.Link;
            else style |= StyleFlags.Link;

            int start = _text.Length;
            _text.Append(s);
            int n = _runs.Count;
            if (n > 0)
            {
                InlineRun last = _runs[n - 1];
                if (last.Style == style && string.Equals(last.LinkUrl, url, StringComparison.Ordinal)
                    && last.Start + last.Length == start)
                {
                    _runs[n - 1] = last with { Length = last.Length + s.Length };
                    return;
                }
            }
            _runs.Add(new InlineRun(start, s.Length, style, url));
        }

        public InlineText Build()
        {
            var result = new InlineText(_text.ToString(), _runs);
            _runs = new List<InlineRun>();
            _text.Clear();
            SkipLeadingSpace = false;
            return result;
        }
    }
}
