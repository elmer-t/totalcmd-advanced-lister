using Windows.Win32.Graphics.Direct2D.Common;

namespace AdvancedViewer.Hosting;

/// <summary>
/// Colors for one theme as Direct2D colors (straight alpha, 0..1). Two immutable instances
/// exist for the process lifetime: <see cref="Light"/> and <see cref="Dark"/>. The active theme
/// is picked from the Lister show flags: <c>lcp_darkmode</c> (128) selects <see cref="Dark"/>.
/// Views compare themes by reference and create their brushes from these values.
/// </summary>
internal sealed class Theme
{
    /// <summary>listplug.h: Total Commander runs in dark mode.</summary>
    public const int lcp_darkmode = 128;
    /// <summary>listplug.h: TC uses native (Windows 10 1809+) dark mode; sent in addition to lcp_darkmode.</summary>
    public const int lcp_darkmodenative = 256;

    public readonly string Name;
    public readonly bool IsDark;

    public readonly D2D1_COLOR_F Background;
    public readonly D2D1_COLOR_F Text;
    public readonly D2D1_COLOR_F MutedText;
    /// <summary>Rule under H1/H2 and the thematic break.</summary>
    public readonly D2D1_COLOR_F HeadingRule;
    public readonly D2D1_COLOR_F Link;
    /// <summary>Fenced/indented code block background.</summary>
    public readonly D2D1_COLOR_F CodeBackground;
    /// <summary>Inline code span background: stronger than the block background so it stands out in running text.</summary>
    public readonly D2D1_COLOR_F InlineCodeBackground;
    public readonly D2D1_COLOR_F CodeText;
    public readonly D2D1_COLOR_F QuoteBar;
    public readonly D2D1_COLOR_F TableGrid;
    public readonly D2D1_COLOR_F TableHeaderBackground;
    /// <summary>Text selection fill (selection is a later feature). Translucent.</summary>
    public readonly D2D1_COLOR_F Selection;

    public static readonly Theme Light = new(
        "light", isDark: false,
        background: Rgb(0xFFFFFF),
        text: Rgb(0x1F2328),
        mutedText: Rgb(0x59636E),
        headingRule: Rgb(0xD1D9E0),
        link: Rgb(0x0969DA),
        codeBackground: Rgb(0xF6F8FA),
        inlineCodeBackground: Rgb(0xE4E8EC),
        codeText: Rgb(0x1F2328),
        quoteBar: Rgb(0xD1D9E0),
        tableGrid: Rgb(0xD1D9E0),
        tableHeaderBackground: Rgb(0xF6F8FA),
        selection: Rgb(0x0969DA, 0.20f));

    public static readonly Theme Dark = new(
        "dark", isDark: true,
        background: Rgb(0x0D1117),
        text: Rgb(0xE6EDF3),
        mutedText: Rgb(0x9198A1),
        headingRule: Rgb(0x3D444D),
        link: Rgb(0x4493F8),
        codeBackground: Rgb(0x151B23),
        inlineCodeBackground: Rgb(0x343A43),
        codeText: Rgb(0xE6EDF3),
        quoteBar: Rgb(0x3D444D),
        tableGrid: Rgb(0x3D444D),
        tableHeaderBackground: Rgb(0x151B23),
        selection: Rgb(0x388BFD, 0.30f));

    private Theme(string name, bool isDark, D2D1_COLOR_F background, D2D1_COLOR_F text, D2D1_COLOR_F mutedText,
        D2D1_COLOR_F headingRule, D2D1_COLOR_F link, D2D1_COLOR_F codeBackground, D2D1_COLOR_F inlineCodeBackground,
        D2D1_COLOR_F codeText, D2D1_COLOR_F quoteBar, D2D1_COLOR_F tableGrid, D2D1_COLOR_F tableHeaderBackground,
        D2D1_COLOR_F selection)
    {
        InlineCodeBackground = inlineCodeBackground;
        Name = name;
        IsDark = isDark;
        Background = background;
        Text = text;
        MutedText = mutedText;
        HeadingRule = headingRule;
        Link = link;
        CodeBackground = codeBackground;
        CodeText = codeText;
        QuoteBar = quoteBar;
        TableGrid = tableGrid;
        TableHeaderBackground = tableHeaderBackground;
        Selection = selection;
    }

    /// <summary>Theme for Lister show flags (ListLoad/ListLoadNext showFlags, or the lc_newparams parameter).</summary>
    public static Theme FromShowFlags(int showFlags) => (showFlags & lcp_darkmode) != 0 ? Dark : Light;

    /// <summary>0xRRGGBB to a D2D color.</summary>
    public static D2D1_COLOR_F Rgb(uint rgb, float alpha = 1f) => new()
    {
        r = ((rgb >> 16) & 0xFF) / 255f,
        g = ((rgb >> 8) & 0xFF) / 255f,
        b = (rgb & 0xFF) / 255f,
        a = alpha,
    };
}
