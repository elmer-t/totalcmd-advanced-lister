using System;
using System.IO;
using System.Linq;
using AdvancedViewer.Markdown;
using AdvancedViewer.Markdown.Model;
using Xunit;
using Xunit.Abstractions;

namespace AdvancedViewer.Tests;

public class CorpusTests
{
    private readonly ITestOutputHelper _output;

    public CorpusTests(ITestOutputHelper output) => _output = output;

    private static string CorpusPath(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    private static Document LoadTorture()
        => MarkdownParser.Parse(TextDecoder.Decode(File.ReadAllBytes(CorpusPath("torture.md"))));

    [Fact]
    public void TortureDocumentInvariantsHold()
    {
        Document doc = LoadTorture();
        _output.WriteLine(ModelWalk.Dump(doc));

        int count = 0;
        foreach (InlineText t in ModelWalk.AllInlines(doc))
        {
            count++;
            string? error = t.Validate();
            Assert.True(error is null, $"{error} in \"{t.Text}\"");
        }
        Assert.True(count > 100, $"only {count} inline texts");

        foreach (Block b in ModelWalk.AllBlocks(doc.Blocks))
        {
            switch (b)
            {
                case CodeBlock c:
                    Assert.False(c.Code.EndsWith('\n'));
                    Assert.DoesNotContain('\r', c.Code);
                    break;
                case HtmlBlock h:
                    Assert.False(h.Html.EndsWith('\n'));
                    break;
                case Table t:
                    Assert.All(t.Rows, r => Assert.Equal(t.Aligns.Length, r.Cells.Count));
                    break;
                case Heading h:
                    Assert.InRange(h.Level, 1, 6);
                    break;
            }
        }
    }

    [Fact]
    public void TortureDocumentHasEveryBlockKind()
    {
        Block[] all = ModelWalk.AllBlocks(LoadTorture().Blocks).ToArray();
        Assert.Contains(all, b => b is Heading);
        Assert.Contains(all, b => b is Paragraph);
        Assert.Contains(all, b => b is CodeBlock { Language: "csharp" });
        Assert.Contains(all, b => b is CodeBlock { Language: "python" });
        Assert.Contains(all, b => b is CodeBlock { Language: null });
        Assert.Contains(all, b => b is BlockQuote);
        Assert.Contains(all, b => b is ListBlock { Ordered: true, Start: 7 });
        Assert.Contains(all, b => b is ListBlock { Ordered: true, Start: 0 });
        Assert.Contains(all, b => b is ListBlock { Tight: false });
        Assert.Contains(all, b => b is Table);
        Assert.Contains(all, b => b is ThematicBreak);
        Assert.Contains(all, b => b is HtmlBlock);
        Assert.Contains(all, b => b is ImageBlock { Url: "images/block.png" });
        Assert.Contains(all, b => b is ImageBlock { Url: "images/ref.png" });
        Assert.Contains(all, b => b is ListBlock l && l.Items.Any(i => i.Checked == true) && l.Items.Any(i => i.Checked == false));
    }

    [Fact]
    public void TortureFrontMatterIsSkipped()
    {
        Document doc = LoadTorture();
        var first = Assert.IsType<Heading>(doc.Blocks[0]);
        Assert.Equal("Torture test", first.Text.Text);
        Assert.DoesNotContain(ModelWalk.AllInlines(doc), t => t.Text.Contains("layout: none"));
    }

    [Fact]
    public void TortureDocumentWithCrlfGivesSameModel()
    {
        string lf = TextDecoder.Decode(File.ReadAllBytes(CorpusPath("torture.md")));
        string crlf = lf.Replace("\n", "\r\n");
        Assert.Equal(ModelWalk.Dump(MarkdownParser.Parse(lf)), ModelWalk.Dump(MarkdownParser.Parse(crlf)));
    }

    [Fact]
    public void EveryPrefixOfTortureDocumentParses()
    {
        // Cutting the file anywhere (as the 32 MB cap does) must still give a valid model.
        string text = TextDecoder.Decode(File.ReadAllBytes(CorpusPath("torture.md")));
        for (int len = 0; len <= text.Length; len += 7)
        {
            Document doc = MarkdownParser.Parse(text.Substring(0, len));
            foreach (InlineText t in ModelWalk.AllInlines(doc))
                Assert.Null(t.Validate());
        }
    }
}
