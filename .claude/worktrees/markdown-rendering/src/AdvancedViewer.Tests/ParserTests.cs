using System.Diagnostics;
using System.Linq;
using System.Text;
using AdvancedViewer.Markdown;
using AdvancedViewer.Markdown.Model;
using Xunit;

namespace AdvancedViewer.Tests;

public class ParserTests
{
    private static Document Parse(string md)
    {
        Document doc = MarkdownParser.Parse(md);
        foreach (InlineText t in ModelWalk.AllInlines(doc))
            Assert.Null(t.Validate());
        return doc;
    }

    private static T Single<T>(Document doc) where T : Block
    {
        Assert.True(doc.Blocks.Count == 1, ModelWalk.Dump(doc));
        return Assert.IsType<T>(doc.Blocks[0]);
    }

    private static string Dump(string md) => ModelWalk.Dump(Parse(md));

    // ---- blocks -------------------------------------------------------------------------------

    [Fact]
    public void EmptyDocument()
    {
        Assert.Empty(Parse("").Blocks);
        Assert.Empty(Parse("\n\n   \n").Blocks);
    }

    [Fact]
    public void NullInputIsEmptyDocument()
    {
        Assert.Empty(MarkdownParser.Parse(null!).Blocks);
    }

    [Theory]
    [InlineData("# One", 1, "One")]
    [InlineData("###### Six ######", 6, "Six")]
    [InlineData("Setext\n======", 1, "Setext")]
    [InlineData("Setext\n------", 2, "Setext")]
    public void Headings(string md, int level, string text)
    {
        var h = Single<Heading>(Parse(md));
        Assert.Equal(level, h.Level);
        Assert.Equal(text, h.Text.Text);
    }

    [Fact]
    public void EmptyHeading()
    {
        var h = Single<Heading>(Parse("#"));
        Assert.Equal(1, h.Level);
        Assert.Equal("", h.Text.Text);
        Assert.Empty(h.Text.Runs);
    }

    [Fact]
    public void ParagraphPlain()
    {
        var p = Single<Paragraph>(Parse("Hello world"));
        Assert.Equal("Hello world", p.Text.Text);
        Assert.Equal(new InlineRun(0, 11, StyleFlags.None, null), Assert.Single(p.Text.Runs));
    }

    [Fact]
    public void FencedCodeWithLanguage()
    {
        var c = Single<CodeBlock>(Parse("```csharp title=\"x\"\nint a;\n\nint b;\n```\n"));
        Assert.Equal("csharp", c.Language);
        Assert.Equal("int a;\n\nint b;", c.Code);
    }

    [Fact]
    public void FencedCodeTildesNoLanguage()
    {
        var c = Single<CodeBlock>(Parse("~~~\n  x <b>&amp;\n~~~"));
        Assert.Null(c.Language);
        Assert.Equal("  x <b>&amp;", c.Code);
    }

    [Fact]
    public void UnclosedFenceRunsToEnd()
    {
        var c = Single<CodeBlock>(Parse("```\na\nb\n"));
        Assert.Equal("a\nb", c.Code);
    }

    [Fact]
    public void IndentedCode()
    {
        var c = Single<CodeBlock>(Parse("    line1\n      line2\n\n    line3\n"));
        Assert.Null(c.Language);
        Assert.Equal("line1\n  line2\n\nline3", c.Code);
    }

    [Fact]
    public void ThematicBreaks()
    {
        Document doc = Parse("***\n\n- - -\n\n___");
        Assert.Equal(3, doc.Blocks.Count);
        Assert.All(doc.Blocks, b => Assert.IsType<ThematicBreak>(b));
    }

    [Fact]
    public void HtmlBlock()
    {
        var h = Single<HtmlBlock>(Parse("<div align=\"center\">\n  <b>hi</b>\n</div>\n"));
        Assert.Equal("<div align=\"center\">\n  <b>hi</b>\n</div>", h.Html);
    }

    [Fact]
    public void HtmlComment()
    {
        var h = Single<HtmlBlock>(Parse("<!-- note -->"));
        Assert.Equal("<!-- note -->", h.Html);
    }

    [Fact]
    public void BlockQuote()
    {
        var q = Single<BlockQuote>(Parse("> # Title\n> text\n>\n> more"));
        Assert.Equal(3, q.Children.Count);
        Assert.IsType<Heading>(q.Children[0]);
        Assert.Equal("text", Assert.IsType<Paragraph>(q.Children[1]).Text.Text);
    }

    [Fact]
    public void LazyContinuationInQuote()
    {
        var q = Single<BlockQuote>(Parse("> a\nb"));
        Assert.Equal("a b", Assert.IsType<Paragraph>(Assert.Single(q.Children)).Text.Text);
    }

    [Fact]
    public void BulletListTight()
    {
        var l = Single<ListBlock>(Parse("- a\n- b\n- c"));
        Assert.False(l.Ordered);
        Assert.True(l.Tight);
        Assert.Equal(3, l.Items.Count);
        Assert.All(l.Items, i => Assert.Null(i.Checked));
        Assert.Equal("b", Assert.IsType<Paragraph>(Assert.Single(l.Items[1].Children)).Text.Text);
    }

    [Fact]
    public void BulletListLoose()
    {
        var l = Single<ListBlock>(Parse("* a\n\n* b\n"));
        Assert.False(l.Tight);
        Assert.Equal(2, l.Items.Count);
    }

    [Fact]
    public void ListItemWithTwoParagraphsIsLoose()
    {
        var l = Single<ListBlock>(Parse("- a\n\n  second\n- b"));
        Assert.False(l.Tight);
        Assert.Equal(2, l.Items[0].Children.Count);
    }

    [Theory]
    [InlineData("1. a\n2. b", 1)]
    [InlineData("7. a\n8. b", 7)]
    [InlineData("0) a", 0)]
    [InlineData("003. a", 3)]
    public void OrderedListStart(string md, int start)
    {
        var l = Single<ListBlock>(Parse(md));
        Assert.True(l.Ordered);
        Assert.Equal(start, l.Start);
    }

    [Fact]
    public void BulletListStartIsOne()
    {
        Assert.Equal(1, Single<ListBlock>(Parse("+ x")).Start);
    }

    [Fact]
    public void ListInQuoteInList()
    {
        Document doc = Parse("- outer\n  > quoted\n  > - inner 1\n  > - inner 2\n- next");
        var outer = Single<ListBlock>(doc);
        Assert.Equal(2, outer.Items.Count);
        var first = outer.Items[0].Children;
        Assert.Equal("outer", Assert.IsType<Paragraph>(first[0]).Text.Text);
        var quote = Assert.IsType<BlockQuote>(first[1]);
        Assert.Equal("quoted", Assert.IsType<Paragraph>(quote.Children[0]).Text.Text);
        var inner = Assert.IsType<ListBlock>(quote.Children[1]);
        Assert.Equal(2, inner.Items.Count);
        Assert.Equal("inner 2", Assert.IsType<Paragraph>(inner.Items[1].Children[0]).Text.Text);
    }

    [Fact]
    public void NestedLists()
    {
        var l = Single<ListBlock>(Parse("1. one\n   - a\n   - b\n2. two"));
        Assert.True(l.Ordered);
        var nested = Assert.IsType<ListBlock>(l.Items[0].Children[1]);
        Assert.False(nested.Ordered);
        Assert.Equal(2, nested.Items.Count);
    }

    [Fact]
    public void TaskItems()
    {
        var l = Single<ListBlock>(Parse("- [ ] todo\n- [x] done\n- [X] DONE\n- plain\n- [y] not a task"));
        Assert.Equal(5, l.Items.Count);
        Assert.False(l.Items[0].Checked);
        Assert.True(l.Items[1].Checked);
        Assert.True(l.Items[2].Checked);
        Assert.Null(l.Items[3].Checked);
        Assert.Null(l.Items[4].Checked);
        Assert.Equal("todo", Assert.IsType<Paragraph>(l.Items[0].Children[0]).Text.Text);
        Assert.Equal("done", Assert.IsType<Paragraph>(l.Items[1].Children[0]).Text.Text);
        Assert.Equal("[y] not a task", Assert.IsType<Paragraph>(l.Items[4].Children[0]).Text.Text);
    }

    [Fact]
    public void TaskItemWithStyledText()
    {
        var l = Single<ListBlock>(Parse("- [x] **bold** rest"));
        var p = Assert.IsType<Paragraph>(l.Items[0].Children[0]);
        Assert.Equal("bold rest", p.Text.Text);
        Assert.Equal(StyleFlags.Bold, p.Text.Runs[0].Style);
    }

    [Fact]
    public void TaskMarkerInOrderedList()
    {
        var l = Single<ListBlock>(Parse("1. [x] a\n2. [ ] b"));
        Assert.True(l.Items[0].Checked);
        Assert.False(l.Items[1].Checked);
    }

    [Fact]
    public void LinkReferenceDefinitionsAreSkipped()
    {
        Document doc = Parse("[a]: https://example.com\n\n[b]: /x \"t\"\n");
        Assert.Empty(doc.Blocks);
    }

    [Fact]
    public void FrontMatterSkipped()
    {
        Document doc = Parse("---\ntitle: Hello\ntags: [a, b]\n---\n# Real\n");
        Assert.Equal("Real", Single<Heading>(doc).Text.Text);
    }

    [Fact]
    public void OnlyFrontMatter()
    {
        Assert.Empty(Parse("---\ntitle: x\n---\n").Blocks);
    }

    [Fact]
    public void ThematicBreakNotAtStartIsNotFrontMatter()
    {
        Document doc = Parse("text\n\n---\nkey: v\n---\n");
        Assert.IsType<Paragraph>(doc.Blocks[0]);
        Assert.IsType<ThematicBreak>(doc.Blocks[1]);
    }

    // ---- tables -------------------------------------------------------------------------------

    [Fact]
    public void TableAlignment()
    {
        var t = Single<Table>(Parse("| a | b | c | d |\n|---|:--|:-:|--:|\n| 1 | 2 | 3 | 4 |"));
        Assert.Equal(new[] { ColumnAlign.None, ColumnAlign.Left, ColumnAlign.Center, ColumnAlign.Right }, t.Aligns);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(new[] { "a", "b", "c", "d" }, t.Rows[0].Cells.Select(c => c.Text));
        Assert.Equal(new[] { "1", "2", "3", "4" }, t.Rows[1].Cells.Select(c => c.Text));
    }

    [Fact]
    public void TableRaggedRowsArePadded()
    {
        var t = Single<Table>(Parse("| a | b | c |\n|---|---|---|\n| 1 |\n| 1 | 2 |\n| 1 | 2 | 3 |"));
        Assert.Equal(3, t.Aligns.Length);
        Assert.Equal(4, t.Rows.Count);
        Assert.All(t.Rows, r => Assert.Equal(3, r.Cells.Count));
        Assert.Equal("", t.Rows[1].Cells[1].Text);
        Assert.Empty(t.Rows[1].Cells[2].Runs);
        Assert.Equal("2", t.Rows[2].Cells[1].Text);
    }

    [Fact]
    public void TableWithoutOuterPipes()
    {
        var t = Single<Table>(Parse("a | b\n--|--\n1 | 2\n"));
        Assert.Equal(2, t.Aligns.Length);
        Assert.Equal("2", t.Rows[1].Cells[1].Text);
    }

    [Fact]
    public void TableExcessCellsAreDropped()
    {
        // GFM: the header row fixes the column count.
        var t = Single<Table>(Parse("| a | b |\n|---|---|\n| 1 | 2 | 3 |"));
        Assert.Equal(2, t.Aligns.Length);
        Assert.Equal(new[] { "1", "2" }, t.Rows[1].Cells.Select(c => c.Text));
    }

    [Fact]
    public void TableEscapedPipes()
    {
        var t = Single<Table>(Parse("| h | h2 |\n|---|---|\n| e \\| x | `a \\| b` |\n| e \\\\| x |"));
        Assert.Equal("e | x", t.Rows[1].Cells[0].Text);
        Assert.Equal("{Code:a | b}", ModelWalk.Runs(t.Rows[1].Cells[1]));
        // Like cmark-gfm: the pipe escape is removed before inline parsing, so "\\|" stays in the cell.
        Assert.Equal(new[] { "e | x", "" }, t.Rows[2].Cells.Select(c => c.Text));
    }

    [Fact]
    public void TableEmptyCells()
    {
        var t = Single<Table>(Parse("|  | b |\n|---|---|\n| 1 | |"));
        Assert.Equal("", t.Rows[0].Cells[0].Text);
        Assert.Equal("", t.Rows[1].Cells[1].Text);
    }

    [Fact]
    public void TableInlineStylingInCells()
    {
        var t = Single<Table>(Parse("| h |\n|---|\n| **b** `c` [l](u) \\| x |"));
        InlineText cell = t.Rows[1].Cells[0];
        Assert.Equal("b c l | x", cell.Text);
        Assert.Equal(StyleFlags.Bold, cell.Runs[0].Style);
        Assert.Contains(cell.Runs, r => r.Style == StyleFlags.Code && cell.Text.Substring(r.Start, r.Length) == "c");
        Assert.Contains(cell.Runs, r => r.Style == StyleFlags.Link && r.LinkUrl == "u");
    }

    [Fact]
    public void TableNeedsDelimiterRow()
    {
        Document doc = Parse("| a | b |\n| 1 | 2 |");
        Assert.IsType<Paragraph>(Assert.Single(doc.Blocks));
    }

    // ---- inlines ------------------------------------------------------------------------------

    [Fact]
    public void SoftAndHardBreaks()
    {
        var p = Single<Paragraph>(Parse("a\nb  \nc\\\nd"));
        Assert.Equal("a b\nc\nd", p.Text.Text);
        Assert.Single(p.Text.Runs);
    }

    [Fact]
    public void Entities()
    {
        var p = Single<Paragraph>(Parse("&amp; &copy; &#169; &#x41; &nbsp;| &bogus; &lt;b&gt;"));
        Assert.Equal("& © © A  | &bogus; <b>", p.Text.Text);
        Assert.Single(p.Text.Runs);
    }

    [Fact]
    public void BackslashEscapes()
    {
        Assert.Equal("*not em* # [x]", Single<Paragraph>(Parse("\\*not em\\* \\# \\[x\\]")).Text.Text);
    }

    [Fact]
    public void EmphasisKinds()
    {
        var p = Single<Paragraph>(Parse("*i* _i_ **b** __b__ ***bi*** ~~s~~ `c`"));
        string runs = ModelWalk.Runs(p.Text);
        Assert.Equal("{Italic:i} {Italic:i} {Bold:b} {Bold:b} {Bold, Italic:bi} {Strike:s} {Code:c}", runs);
    }

    [Fact]
    public void NestedEmphasisMerges()
    {
        var p = Single<Paragraph>(Parse("**bold *both ~~all~~* bold** plain"));
        Assert.Equal("{Bold:bold }{Bold, Italic:both }{Bold, Italic, Strike:all}{Bold: bold} plain", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void SingleTildeIsLiteral()
    {
        Assert.Equal("~x~", Single<Paragraph>(Parse("~x~")).Text.Text);
    }

    [Fact]
    public void ExtraEmphasisKindsStayLiteral()
    {
        var p = Single<Paragraph>(Parse("^sup^ ++ins++ ==mark=="));
        Assert.Equal("^sup^ ++ins++ ==mark==", p.Text.Text);
        Assert.Single(p.Text.Runs);
    }

    [Fact]
    public void UnmatchedDelimitersAreLiteral()
    {
        Assert.Equal("a * b ** c _ [d", Single<Paragraph>(Parse("a * b ** c _ [d")).Text.Text);
        Assert.Equal("**a", Single<Paragraph>(Parse("**a")).Text.Text);
        Assert.Equal("[a](", Single<Paragraph>(Parse("[a](")).Text.Text);
    }

    [Fact]
    public void CodeSpan()
    {
        var p = Single<Paragraph>(Parse("x `` a ` b `` y"));
        Assert.Equal("x {Code:a ` b} y", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void InlineHtml()
    {
        var p = Single<Paragraph>(Parse("a <kbd>Ctrl</kbd> b <!-- c -->"));
        Assert.Equal("a {Code:<kbd>}Ctrl{Code:</kbd>} b {Code:<!-- c -->}", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void InlineLink()
    {
        var p = Single<Paragraph>(Parse("see [the *docs*](https://x.org/a?b=1&amp;c \"title\") now"));
        Assert.Equal("see {Link:the  @https://x.org/a?b=1&c}{Italic, Link:docs @https://x.org/a?b=1&c} now", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void ReferenceLinks()
    {
        Document doc = Parse("[full][r] [collapsed][] [r]\n\n[r]: https://ref.example/ \"T\"\n[collapsed]: /c");
        var p = Single<Paragraph>(doc);
        Assert.Equal("{Link:full @https://ref.example/} {Link:collapsed @/c} {Link:r @https://ref.example/}", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void UndefinedReferenceIsLiteral()
    {
        Assert.Equal("[nope]", Single<Paragraph>(Parse("[nope]")).Text.Text);
    }

    [Fact]
    public void AngleAutolinks()
    {
        var p = Single<Paragraph>(Parse("<https://a.example/x> and <me@example.com>"));
        Assert.Equal("{Link:https://a.example/x @https://a.example/x} and {Link:me@example.com @mailto:me@example.com}", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void BareUrlAutolinks()
    {
        var p = Single<Paragraph>(Parse("go to https://example.com/path. or www.example.org now"));
        Assert.Contains(p.Text.Runs, r => r.Style == StyleFlags.Link && r.LinkUrl == "https://example.com/path"
            && p.Text.Text.Substring(r.Start, r.Length) == "https://example.com/path");
        Assert.Contains(p.Text.Runs, r => r.Style == StyleFlags.Link && r.LinkUrl == "http://www.example.org"
            && p.Text.Text.Substring(r.Start, r.Length) == "www.example.org");
        Assert.Equal("go to https://example.com/path. or www.example.org now", p.Text.Text);
    }

    [Fact]
    public void BareUrlInsideCodeIsNotLink()
    {
        var p = Single<Paragraph>(Parse("`https://example.com`"));
        Assert.Equal(StyleFlags.Code, Assert.Single(p.Text.Runs).Style);
    }

    [Fact]
    public void InlineImage()
    {
        var p = Single<Paragraph>(Parse("logo ![The *alt*](img.png \"t\") end"));
        Assert.Equal("logo [The alt] end", p.Text.Text);
        Assert.Equal("logo {ImagePlaceholder:[The alt]} end", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void ImageInsideLink()
    {
        var p = Single<Paragraph>(Parse("x [![badge](b.svg)](https://ci) y"));
        Assert.Equal("x {Link, ImagePlaceholder:[badge] @https://ci} y", ModelWalk.Runs(p.Text));
    }

    [Fact]
    public void ImageOnlyParagraphIsImageBlock()
    {
        var img = Single<ImageBlock>(Parse("![A diagram](docs/d.png)\n"));
        Assert.Equal("A diagram", img.Alt);
        Assert.Equal("docs/d.png", img.Url);
    }

    [Fact]
    public void ReferenceImageBlock()
    {
        var img = Single<ImageBlock>(Parse("![x][i]\n\n[i]: <a b.png>"));
        Assert.Equal("a b.png", img.Url);
    }

    [Fact]
    public void TwoImagesIsParagraph()
    {
        var p = Single<Paragraph>(Parse("![a](1.png) ![b](2.png)"));
        Assert.Equal("[a] [b]", p.Text.Text);
    }

    [Fact]
    public void LinkedImageIsNotImageBlock()
    {
        Single<Paragraph>(Parse("[![a](1.png)](https://x)"));
    }

    [Fact]
    public void CrlfInput()
    {
        Document lf = Parse("# T\n\npara\nline\n\n```js\na\nb\n```\n\n- x\n- y\n");
        Document crlf = Parse("# T\r\n\r\npara\r\nline\r\n\r\n```js\r\na\r\nb\r\n```\r\n\r\n- x\r\n- y\r\n");
        Document cr = Parse("# T\r\rpara\rline\r\r```js\ra\rb\r```\r\r- x\r- y\r");
        Assert.Equal(ModelWalk.Dump(lf), ModelWalk.Dump(crlf));
        Assert.Equal(ModelWalk.Dump(lf), ModelWalk.Dump(cr));
        Assert.All(ModelWalk.AllInlines(crlf), t => Assert.DoesNotContain('\r', t.Text));
        Assert.Equal("a\nb", Assert.IsType<CodeBlock>(crlf.Blocks[2]).Code);
    }

    [Fact]
    public void NulCharactersDoNotBreak()
    {
        var p = Single<Paragraph>(Parse("a\0b"));
        Assert.Equal(3, p.Text.Text.Length);
    }

    [Fact]
    public void DeepNestingDoesNotThrow()
    {
        string[] inputs =
        {
            new string('>', 20000) + " deep",
            string.Concat(Enumerable.Repeat("- ", 5000)) + "x",
            new string('[', 50000) + "x" + new string(']', 50000),
            new string('*', 50000) + "x" + new string('*', 50000),
            string.Concat(Enumerable.Repeat("*a ", 20000)) + string.Concat(Enumerable.Repeat("b* ", 20000)),
            new string('`', 100000),
            string.Concat(Enumerable.Repeat("<a>", 30000)),
            string.Concat(Enumerable.Repeat("| a ", 3000)) + "|\n" + string.Concat(Enumerable.Repeat("|---", 3000)) + "|\n",
        };
        foreach (string md in inputs)
        {
            Document doc = MarkdownParser.Parse(md);
            Assert.NotEmpty(doc.Blocks);
            foreach (InlineText t in ModelWalk.AllInlines(doc))
                Assert.Null(t.Validate());
        }
    }

    [Fact]
    public void LargeDocumentParsesQuickly()
    {
        var sb = new StringBuilder();
        int n = 0;
        while (sb.Length < 1024 * 1024)
        {
            n++;
            sb.Append("## Section ").Append(n).Append("\n\n");
            sb.Append("Some *italic* and **bold** text with `code`, a [link](https://example.com/").Append(n)
              .Append(") and &amp; entity. https://auto.example/").Append(n).Append("\n\n");
            sb.Append("- item one\n- [x] task\n  1. nested\n\n");
            sb.Append("> quote ~~struck~~\n\n");
            sb.Append("| a | b |\n|:--|--:|\n| 1 | 2 |\n\n");
            sb.Append("```cs\nvar x = ").Append(n).Append(";\n```\n\n");
        }
        string md = sb.ToString();
        MarkdownParser.Parse(md); // warm up (JIT)

        var sw = Stopwatch.StartNew();
        Document doc = MarkdownParser.Parse(md);
        sw.Stop();

        Assert.True(doc.Blocks.Count >= n * 6, $"{doc.Blocks.Count} blocks");
        Assert.True(sw.ElapsedMilliseconds < 1000, $"parse took {sw.ElapsedMilliseconds} ms");
        Assert.All(ModelWalk.AllInlines(doc), t => Assert.Null(t.Validate()));
    }
}
