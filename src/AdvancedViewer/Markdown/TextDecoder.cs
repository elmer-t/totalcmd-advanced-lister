using System;
using System.Globalization;
using System.Text;

namespace AdvancedViewer.Markdown;

/// <summary>
/// Turns the raw bytes of a Markdown file into a string. Pure managed code (no Win32), so it is
/// unit-tested on CoreCLR. Never throws on any input.
/// </summary>
public static class TextDecoder
{
    /// <summary>Size cap the loader applies before decoding (named in the truncation notice).</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    // Replacement-character fallback (never throws), no BOM emitted.
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding s_utf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly UnicodeEncoding s_utf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Decodes <paramref name="bytes"/>: UTF-8 BOM, UTF-16 LE BOM (FF FE) or UTF-16 BE BOM (FE FF)
    /// select the encoding and are dropped; anything else is UTF-8 with U+FFFD for invalid
    /// sequences. CRLF and lone CR become LF.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            string text;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = s_utf8.GetString(bytes[3..]);
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = s_utf16Le.GetString(bytes[2..]);   // a dangling odd byte becomes U+FFFD
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                text = s_utf16Be.GetString(bytes[2..]);
            else
                text = s_utf8.GetString(bytes);
            return NormalizeNewlines(text);
        }
        catch (Exception)
        {
            // Not expected (replacement fallbacks do not throw), but the viewer must show something.
            return string.Empty;
        }
    }

    /// <summary>Replaces CRLF and lone CR with LF. Returns the same instance when there is no CR.</summary>
    public static string NormalizeNewlines(string text)
    {
        int firstCr = text.IndexOf('\r');
        if (firstCr < 0) return text;

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, firstCr);
        for (int i = firstCr; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends a final Markdown paragraph "…(file truncated: showing first 32 MB of N MB)", where N
    /// is <paramref name="totalBytes"/> in MB rounded up. Starts a new block with a blank line.
    /// Note: when the cut lands inside an open code fence the notice is shown as code; it is
    /// still visible, so this is accepted.
    /// </summary>
    public static string AppendTruncationNotice(string text, long totalBytes)
    {
        const long Mb = 1024 * 1024;
        long totalMb = totalBytes <= 0 ? 0 : (totalBytes + Mb - 1) / Mb;
        string notice = "…(file truncated: showing first "
            + (MaxBytes / Mb).ToString(CultureInfo.InvariantCulture) + " MB of "
            + totalMb.ToString(CultureInfo.InvariantCulture) + " MB)";

        text ??= string.Empty;
        string sep = text.Length == 0 ? "" : text.EndsWith('\n') ? "\n" : "\n\n";
        if (text.EndsWith("\n\n", StringComparison.Ordinal)) sep = "";
        return text + sep + notice + "\n";
    }
}
