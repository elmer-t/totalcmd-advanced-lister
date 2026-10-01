using System;

namespace AdvancedViewer.Markdown.Model;

[Flags]
public enum StyleFlags
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strike = 8,
    Link = 16,
    ImagePlaceholder = 32,
}

public enum ColumnAlign
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>
/// A styled range of <see cref="InlineText.Text"/>. <see cref="LinkUrl"/> is non-null exactly when
/// <see cref="Style"/> contains <see cref="StyleFlags.Link"/> (it may be the empty string).
/// </summary>
public readonly record struct InlineRun(int Start, int Length, StyleFlags Style, string? LinkUrl);
