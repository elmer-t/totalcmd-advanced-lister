using System.Collections.Generic;

namespace AdvancedViewer.Markdown.Model;

/// <summary>
/// Display text of a paragraph, heading or table cell, plus its styled runs.
/// <para>
/// <see cref="Text"/> is final display text: entities decoded, soft breaks are ' ', hard breaks
/// are '\n', images are "[alt]" with <see cref="StyleFlags.ImagePlaceholder"/>, inline HTML is its
/// raw source with <see cref="StyleFlags.Code"/>.
/// </para>
/// <para>
/// <see cref="Runs"/> cover <see cref="Text"/> contiguously and in order: the first starts at 0,
/// each next one starts where the previous one ended, the last ends at <c>Text.Length</c>, every
/// length is &gt; 0, and adjacent runs differ in style or link. Empty text has no runs.
/// </para>
/// </summary>
public sealed class InlineText
{
    public InlineText(string text, List<InlineRun> runs)
    {
        Text = text;
        Runs = runs;
    }

    public string Text { get; }
    public List<InlineRun> Runs { get; }

    public static InlineText CreateEmpty() => new(string.Empty, new List<InlineRun>());

    /// <summary>One run of plain text with the given style (no run when the text is empty).</summary>
    public static InlineText CreatePlain(string text, StyleFlags style = StyleFlags.None)
    {
        var runs = new List<InlineRun>(1);
        if (text.Length > 0) runs.Add(new InlineRun(0, text.Length, style & ~StyleFlags.Link, null));
        return new InlineText(text, runs);
    }

    /// <summary>
    /// Checks the invariants described on the class. Returns null when they hold, otherwise a
    /// description of the first violation. Never throws.
    /// </summary>
    public string? Validate()
    {
        if (Text is null) return "Text is null";
        if (Runs is null) return "Runs is null";
        if (Text.Length == 0)
            return Runs.Count == 0 ? null : "empty text has runs";
        if (Runs.Count == 0) return "non-empty text has no runs";

        int pos = 0;
        for (int i = 0; i < Runs.Count; i++)
        {
            InlineRun r = Runs[i];
            if (r.Start != pos) return $"run {i} starts at {r.Start}, expected {pos}";
            if (r.Length <= 0) return $"run {i} has length {r.Length}";
            if (((r.Style & StyleFlags.Link) != 0) != (r.LinkUrl is not null))
                return $"run {i}: Link flag and LinkUrl disagree";
            if (i > 0 && Runs[i - 1].Style == r.Style && Runs[i - 1].LinkUrl == r.LinkUrl)
                return $"run {i} has the same style as run {i - 1} (not merged)";
            pos += r.Length;
        }
        return pos == Text.Length ? null : $"runs end at {pos}, text length {Text.Length}";
    }

    public override string ToString() => Text;
}
