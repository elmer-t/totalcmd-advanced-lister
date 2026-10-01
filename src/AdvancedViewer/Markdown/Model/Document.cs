using System.Collections.Generic;

namespace AdvancedViewer.Markdown.Model;

/// <summary>
/// A parsed Markdown document: the list of top-level blocks. Pure data with no Markdig or Win32
/// types, so the layout code and the unit tests can use it without either.
/// </summary>
public sealed class Document
{
    public List<Block> Blocks { get; } = new();
}
