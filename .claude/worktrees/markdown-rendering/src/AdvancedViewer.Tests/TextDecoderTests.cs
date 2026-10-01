using System.Linq;
using System.Text;
using AdvancedViewer.Markdown;
using Xunit;

namespace AdvancedViewer.Tests;

public class TextDecoderTests
{
    private const string Sample = "# Héllo\nwörld €𝄞";

    [Fact]
    public void Utf8WithoutBom()
    {
        Assert.Equal(Sample, TextDecoder.Decode(Encoding.UTF8.GetBytes(Sample)));
    }

    [Fact]
    public void Utf8Bom()
    {
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Sample)).ToArray();
        Assert.Equal(Sample, TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf16LeBom()
    {
        byte[] bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Sample)).ToArray();
        Assert.Equal(Sample, TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf16BeBom()
    {
        byte[] bytes = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(Sample)).ToArray();
        Assert.Equal(Sample, TextDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf16OddByteCountGetsReplacementChar()
    {
        byte[] bytes = new byte[] { 0xFF, 0xFE, (byte)'a', 0, (byte)'b' };
        Assert.Equal("a�", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void BomOnlyAndEmpty()
    {
        Assert.Equal("", TextDecoder.Decode(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("", TextDecoder.Decode(new byte[] { 0xFF, 0xFE }));
        Assert.Equal("", TextDecoder.Decode(new byte[] { 0xFE, 0xFF }));
        Assert.Equal("", TextDecoder.Decode(System.ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void InvalidUtf8BecomesReplacementChars()
    {
        byte[] bytes = { (byte)'a', 0xC3, (byte)'b', 0xFF, 0xFE, 0xE2, 0x82, (byte)'c', 0xED, 0xA0, 0x80 };
        string s = TextDecoder.Decode(bytes);
        Assert.StartsWith("a�b", s);
        Assert.Contains('c', s);
        Assert.DoesNotContain(s, ch => char.IsSurrogate(ch));
        Assert.True(s.Count(ch => ch == '�') >= 4, s);
    }

    [Fact]
    public void TruncatedMultiByteSequenceAtEnd()
    {
        byte[] bytes = { (byte)'x', 0xF0, 0x9D };
        Assert.Equal("x�", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void LooksLikeUtf16WithoutBomIsTreatedAsUtf8()
    {
        string s = TextDecoder.Decode(new byte[] { (byte)'a', 0, (byte)'b', 0 });
        Assert.Equal("a\0b\0", s);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb\n")]
    [InlineData("a\rb\r", "a\nb\n")]
    [InlineData("a\r\r\nb", "a\n\nb")]
    [InlineData("\n\r", "\n\n")]
    [InlineData("no cr", "no cr")]
    public void NewlinesNormalized(string input, string expected)
    {
        Assert.Equal(expected, TextDecoder.Decode(Encoding.UTF8.GetBytes(input)));
    }

    [Fact]
    public void Utf16CrlfNormalized()
    {
        byte[] bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("a\r\nb")).ToArray();
        Assert.Equal("a\nb", TextDecoder.Decode(bytes));
    }

    [Fact]
    public void TruncationNotice()
    {
        string s = TextDecoder.AppendTruncationNotice("# Title\ntext", 40L * 1024 * 1024 + 1);
        Assert.Equal("# Title\ntext\n\n…(file truncated: showing first 32 MB of 41 MB)\n", s);

        var doc = MarkdownParser.Parse(s);
        var last = Assert.IsType<AdvancedViewer.Markdown.Model.Paragraph>(doc.Blocks[^1]);
        Assert.Equal("…(file truncated: showing first 32 MB of 41 MB)", last.Text.Text);
    }

    [Fact]
    public void TruncationNoticeAfterTrailingNewlines()
    {
        Assert.Equal("a\n\n…(file truncated: showing first 32 MB of 64 MB)\n",
            TextDecoder.AppendTruncationNotice("a\n", 64L * 1024 * 1024));
        Assert.Equal("a\n\n…(file truncated: showing first 32 MB of 64 MB)\n",
            TextDecoder.AppendTruncationNotice("a\n\n", 64L * 1024 * 1024));
        Assert.Equal("…(file truncated: showing first 32 MB of 33 MB)\n",
            TextDecoder.AppendTruncationNotice("", 33L * 1024 * 1024));
    }
}
