using System.Collections.Generic;
using System.Linq;
using System.Text;
using AdvancedViewer.Markdown;
using AdvancedViewer.Markdown.Model;
using Xunit;

namespace AdvancedViewer.Tests;

public class ChunkSplitterTests
{
    private static List<(int Start, int End)> Split(string text, int target)
    {
        var chunks = new List<(int, int)>();
        int pos = 0;
        while (pos < text.Length)
        {
            int end = ChunkSplitter.NextEnd(text, pos, target);
            Assert.True(end > pos, "no progress");
            chunks.Add((pos, end));
            pos = end;
        }
        return chunks;
    }

    [Fact]
    public void Chunks_cover_the_text_and_end_before_column_zero_lines_after_blank_lines()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++) sb.Append("Paragraph ").Append(i).Append(" with some words.\n\n");
        string text = sb.ToString();
        var chunks = Split(text, 100);
        Assert.True(chunks.Count > 10);
        Assert.Equal(text, string.Concat(chunks.Select(c => text[c.Start..c.End])));
        foreach (var (start, _) in chunks.Skip(1))
        {
            Assert.Equal('\n', text[start - 1]);
            Assert.Equal('\n', text[start - 2]);
            Assert.NotEqual(' ', text[start]);
        }
    }

    [Fact]
    public void Never_splits_inside_fences_html_comments_or_indented_continuations()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            sb.Append("Intro ").Append(i).Append('\n').Append('\n');
            sb.Append("```\ncode line\n\nColumn zero inside the fence\n\n~~~\nstill code\n```\n\n");
            sb.Append("<!--\ncomment\n\nColumn zero inside the comment\n-->\n\n");
            sb.Append("- item\n\n    indented continuation\n\n");
        }
        string text = sb.ToString();
        var chunks = Split(text, 1);
        Assert.True(chunks.Count > 50);
        foreach (var (start, end) in chunks)
        {
            Document doc = MarkdownParser.Parse(text[start..end]);
            foreach (Block b in doc.Blocks)
            {
                if (b is Paragraph p)
                {
                    Assert.DoesNotContain("inside the", p.Text.Text);
                    Assert.DoesNotContain("still code", p.Text.Text);
                }
            }
        }
    }

    [Fact]
    public void Unclosed_fence_at_the_end_is_reported_open()
    {
        string text = "a\n\n```\ncode\n\nmore\n";
        int end = ChunkSplitter.NextEnd(text, 0, 1, out bool open);
        Assert.Equal(3, end); // split before the fence line
        end = ChunkSplitter.NextEnd(text, end, 1, out open);
        Assert.Equal(text.Length, end);
        Assert.True(open);
    }

    [Fact]
    public void Collects_reference_definitions_including_next_line_destinations()
    {
        string text = "Text [a] and [b].\n\n[a]: https://a.example\n   [b]:\n   /b/url\nnot a def\n";
        string defs = ChunkSplitter.CollectDefinitions(text, 1000);
        Assert.Contains("[a]: https://a.example\n", defs);
        Assert.Contains("[b]:\n   /b/url\n", defs);
        Assert.DoesNotContain("not a def", defs);
    }

    [Fact]
    public void Definitions_appended_to_a_chunk_resolve_reference_links()
    {
        string chunk = "See [the spec][spec].\n";
        string defs = ChunkSplitter.CollectDefinitions("x\n\n[spec]: https://spec.commonmark.org/\n", 1000);
        Document doc = MarkdownParser.Parse(chunk + "\n\n" + defs);
        var p = Assert.IsType<Paragraph>(Assert.Single(doc.Blocks));
        Assert.Contains(p.Text.Runs, r => r.LinkUrl == "https://spec.commonmark.org/");
    }
}
