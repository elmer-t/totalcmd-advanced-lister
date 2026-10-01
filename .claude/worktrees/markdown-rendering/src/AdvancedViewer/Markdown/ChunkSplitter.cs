using System;
using System.Text;

namespace AdvancedViewer.Markdown;

/// <summary>
/// Splits a large Markdown text into chunks that can be parsed independently, so the first
/// screenful of a multi-megabyte file is shown after parsing ~64 K characters instead of all of
/// it (Markdig parses roughly 15 MB/s; a 5 MB file would block for ~350 ms). Pure managed code.
/// <para>
/// A chunk ends just before a line that starts in column 0, follows a blank line, and lies
/// outside fenced code and multi-line HTML (comments, &lt;pre&gt;, &lt;script&gt;, &lt;style&gt;,
/// &lt;textarea&gt;). At such a point every open container (paragraph, list, block quote,
/// indented code) has ended or would end, so parsing the halves separately gives the same blocks.
/// Known differences: a list interrupted by the split becomes two lists (numbers are kept, the
/// tight/loose spacing may differ), and reference definitions are only visible across chunks
/// through <see cref="CollectDefinitions"/>.
/// </para>
/// </summary>
public static class ChunkSplitter
{
    /// <summary>Texts up to this many characters are parsed in one go.</summary>
    public const int Threshold = 128 * 1024;

    /// <summary>Target chunk size in characters (a chunk ends at the first split point after it).</summary>
    public const int ChunkChars = 64 * 1024;

    /// <summary>
    /// End (exclusive) of the chunk that starts at <paramref name="start"/> (a line start outside
    /// any fence): the first split point at or after start + <paramref name="target"/>, or the end
    /// of the text when there is none.
    /// </summary>
    public static int NextEnd(string text, int start, int target) => NextEnd(text, start, target, out _);

    /// <summary>
    /// As <see cref="NextEnd(string, int, int)"/>; <paramref name="endsOpen"/> is set when the
    /// chunk runs to the end of the text inside an unclosed fence or HTML block (text appended
    /// to such a chunk would become part of that block).
    /// </summary>
    public static int NextEnd(string text, int start, int target, out bool endsOpen)
    {
        endsOpen = false;
        int len = text.Length;
        int pos = start;
        bool prevBlank = false;
        char fenceChar = '\0';
        int fenceLen = 0;
        string? htmlUntil = null;

        while (pos < len)
        {
            int lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0) lineEnd = len;
            int next = Math.Min(len, lineEnd + 1);

            int indent = 0, i = pos;
            while (i < lineEnd && (text[i] == ' ' || text[i] == '\t') && indent < 4)
            {
                indent += text[i] == '\t' ? 4 - indent % 4 : 1;
                i++;
            }
            bool blank = IsBlank(text, i, lineEnd);

            if (fenceChar == '\0' && htmlUntil == null && prevBlank && !blank && indent == 0 && pos - start >= target)
                return pos;

            if (fenceChar != '\0')
            {
                if (indent <= 3 && RunLength(text, i, lineEnd, fenceChar) >= fenceLen &&
                    IsBlank(text, i + RunLength(text, i, lineEnd, fenceChar), lineEnd))
                    fenceChar = '\0';
            }
            else if (htmlUntil != null)
            {
                if (Contains(text, pos, lineEnd, htmlUntil)) htmlUntil = null;
            }
            else if (indent <= 3 && i < lineEnd)
            {
                char c = text[i];
                if ((c == '`' || c == '~') && RunLength(text, i, lineEnd, c) >= 3)
                {
                    fenceChar = c;
                    fenceLen = RunLength(text, i, lineEnd, c);
                }
                else if (c == '<')
                {
                    htmlUntil = HtmlBlockEnd(text, i, lineEnd);
                }
            }

            prevBlank = blank;
            pos = next;
        }
        endsOpen = fenceChar != '\0' || htmlUntil != null;
        return len;
    }

    /// <summary>
    /// The link reference definition lines of <paramref name="text"/> ("[label]: url", plus the
    /// next line when the URL is on it), at most <paramref name="maxChars"/> characters, so they
    /// can be appended to every chunk. Lines inside code are not excluded; an extra definition is
    /// harmless (the first definition of a label wins).
    /// </summary>
    public static string CollectDefinitions(string text, int maxChars)
    {
        StringBuilder? sb = null;
        int len = text.Length;
        int pos = 0;
        while (pos < len)
        {
            int lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0) lineEnd = len;
            int i = pos, spaces = 0;
            while (i < lineEnd && text[i] == ' ' && spaces < 3) { i++; spaces++; }
            if (i < lineEnd && text[i] == '[')
            {
                int close = text.IndexOf("]:", i + 1, lineEnd - i - 1, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    int end = lineEnd;
                    if (IsBlank(text, close + 2, lineEnd) && lineEnd < len)
                    {
                        // Destination on the next line.
                        int next = text.IndexOf('\n', lineEnd + 1);
                        end = next < 0 ? len : next;
                    }
                    sb ??= new StringBuilder();
                    if (sb.Length + (end - pos) + 1 > maxChars) break;
                    sb.Append(text, pos, end - pos).Append('\n');
                    lineEnd = end;
                }
            }
            pos = lineEnd + 1;
        }
        return sb?.ToString() ?? string.Empty;
    }

    private static bool IsBlank(string s, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (s[i] != ' ' && s[i] != '\t' && s[i] != '\r') return false;
        return true;
    }

    private static int RunLength(string s, int from, int to, char c)
    {
        int n = 0;
        while (from + n < to && s[from + n] == c) n++;
        return n;
    }

    private static bool Contains(string s, int from, int to, string value) =>
        to - from >= value.Length && s.IndexOf(value, from, to - from, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>For an HTML block start that may span blank lines (CommonMark types 1 and 2), the text that ends it, unless it ends on this line.</summary>
    private static string? HtmlBlockEnd(string s, int i, int lineEnd)
    {
        string? end = null;
        if (StartsWith(s, i, lineEnd, "<!--")) end = "-->";
        else if (StartsWith(s, i, lineEnd, "<pre")) end = "</pre>";
        else if (StartsWith(s, i, lineEnd, "<script")) end = "</script>";
        else if (StartsWith(s, i, lineEnd, "<style")) end = "</style>";
        else if (StartsWith(s, i, lineEnd, "<textarea")) end = "</textarea>";
        if (end == null) return null;
        return Contains(s, i + 2, lineEnd, end) ? null : end;
    }

    private static bool StartsWith(string s, int i, int lineEnd, string value) =>
        lineEnd - i >= value.Length && string.Compare(s, i, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
}
