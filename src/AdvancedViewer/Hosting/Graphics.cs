using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.DirectWrite;

namespace AdvancedViewer;

/// <summary>
/// Device-independent Direct2D/DirectWrite resources shared by every viewer window:
/// one ID2D1Factory, one IDWriteFactory, one Consolas IDWriteTextFormat and the measured
/// cell size (hex view), plus the Markdown text formats (<see cref="EnsureMarkdown"/>). Created lazily on the first ListLoad (never in DllMain/static init) and kept
/// for the process lifetime; the DLL is never safely unloadable anyway (Native AOT).
/// COM objects are plain unmanaged structs (CsWin32 allowMarshaling:false), called through
/// their vtables and released explicitly.
/// </summary>
internal static unsafe class Graphics
{
    public const float FontSizeDip = 14f;

    public static ID2D1Factory* D2DFactory { get; private set; }
    public static IDWriteFactory* DWriteFactory { get; private set; }
    public static IDWriteTextFormat* TextFormat { get; private set; }

    /// <summary>Advance width of one Consolas cell, in DIPs.</summary>
    public static float CellWidth { get; private set; }
    /// <summary>Line height (natural line spacing), in DIPs.</summary>
    public static float LineHeight { get; private set; }

    public static void EnsureInitialized()
    {
        if (TextFormat != null) return;

        long t0 = Log.Now();

        // ID2D1Factory, single-threaded: all calls happen on TC's UI thread.
        void* d2d;
        Guid iid = ID2D1Factory.IID_Guid;
        PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iid, null, &d2d).ThrowOnFailure();
        var d2dFactory = (ID2D1Factory*)d2d;

        void* dw;
        Guid dwIid = IDWriteFactory.IID_Guid;
        PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &dwIid, &dw).ThrowOnFailure();
        var dwFactory = (IDWriteFactory*)dw;

        IDWriteTextFormat* format = null;
        fixed (char* family = "Consolas")
        fixed (char* locale = "en-us")
        {
            dwFactory->CreateTextFormat(
                family, null,
                DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
                DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                FontSizeDip, locale, &format);
        }
        format->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);

        // Measure one cell with a throwaway layout.
        IDWriteTextLayout* layout = null;
        fixed (char* s = "0")
        {
            dwFactory->CreateTextLayout(s, 1, format, 1000f, 1000f, &layout);
        }
        DWRITE_TEXT_METRICS m;
        layout->GetMetrics(&m);
        layout->Release();

        CellWidth = m.widthIncludingTrailingWhitespace;
        LineHeight = m.height;
        D2DFactory = d2dFactory;
        DWriteFactory = dwFactory;
        TextFormat = format;

        Log.Write("GraphicsInit", null, Log.ElapsedUs(t0), $"cell={CellWidth:F2}x{LineHeight:F2}dip");
    }

    // ------------------------------------------------------------------ Markdown formats

    /// <summary>Body text size of the Markdown view, in DIPs.</summary>
    public const float BodySizeDip = 14f;
    /// <summary>Code block size of the Markdown view, in DIPs.</summary>
    public const float CodeSizeDip = 13f;

    /// <summary>Segoe UI 14 DIP, word wrap with emergency breaks (long URLs/words break anywhere).</summary>
    public static IDWriteTextFormat* BodyFormat { get; private set; }
    /// <summary><see cref="MonoFamily"/> 13 DIP, emergency-break wrapping, tab stop 4 cells.</summary>
    public static IDWriteTextFormat* CodeFormat { get; private set; }
    /// <summary>"Cascadia Mono" if installed (Windows 11, Terminal, VS), else "Consolas".</summary>
    public static string MonoFamily { get; private set; } = "Consolas";
    /// <summary>Dashed stroke for the image placeholder box.</summary>
    public static ID2D1StrokeStyle* DashStroke { get; private set; }

    /// <summary>Natural line height of <see cref="BodyFormat"/>, in DIPs.</summary>
    public static float BodyLineHeight { get; private set; }
    /// <summary>Average advance of lower-case body text, in DIPs (height estimates only).</summary>
    public static float BodyAvgCharWidth { get; private set; }
    public static float CodeLineHeight { get; private set; }
    public static float CodeCharWidth { get; private set; }

    /// <summary>Creates the Markdown text formats once (after <see cref="EnsureInitialized"/>); kept for the process lifetime.</summary>
    public static void EnsureMarkdown()
    {
        if (CodeFormat != null) return;
        EnsureInitialized();
        long t0 = Log.Now();

        // Pick the monospace family: Cascadia Mono ships with Windows 11 and Terminal; Consolas
        // is on every Windows since Vista.
        IDWriteFontCollection* fonts = null;
        DWriteFactory->GetSystemFontCollection(&fonts, false);
        uint index;
        BOOL exists = false;
        fixed (char* name = "Cascadia Mono")
        {
            fonts->FindFamilyName(name, &index, &exists);
        }
        fonts->Release();
        MonoFamily = exists ? "Cascadia Mono" : "Consolas";

        IDWriteTextFormat* body = CreateFormat("Segoe UI", BodySizeDip);
        IDWriteTextFormat* code = CreateFormat(MonoFamily, CodeSizeDip);

        float bodyAvg = Measure(body, "abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 the and", out float bodyLine);
        float codeCell = Measure(code, "0000000000", out float codeLine);
        code->SetIncrementalTabStop(4 * codeCell);

        var props = new D2D1_STROKE_STYLE_PROPERTIES
        {
            dashStyle = D2D1_DASH_STYLE.D2D1_DASH_STYLE_DASH,
            miterLimit = 10f,
        };
        ID2D1StrokeStyle* dash = null;
        D2DFactory->CreateStrokeStyle(&props, null, 0, &dash);

        BodyLineHeight = bodyLine;
        BodyAvgCharWidth = bodyAvg;
        CodeLineHeight = codeLine;
        CodeCharWidth = codeCell;
        DashStroke = dash;
        BodyFormat = body;
        CodeFormat = code; // last: it is the "initialized" flag

        Log.Write("GraphicsMarkdownInit", null, Log.ElapsedUs(t0), $"mono={MonoFamily} body={bodyLine:F2}/{bodyAvg:F2} code={codeLine:F2}/{codeCell:F2}");
    }

    private static IDWriteTextFormat* CreateFormat(string family, float size)
    {
        IDWriteTextFormat* format = null;
        fixed (char* f = family)
        fixed (char* locale = "en-us")
        {
            DWriteFactory->CreateTextFormat(
                f, null,
                DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
                DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                size, locale, &format);
        }
        // Emergency break: wrap at word boundaries, but break a word that is wider than the line
        // (long URLs, hashes, minified code) instead of letting it overflow.
        format->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
        return format;
    }

    /// <summary>Average advance per character of <paramref name="sample"/>; lineHeight = one line's height.</summary>
    private static float Measure(IDWriteTextFormat* format, string sample, out float lineHeight)
    {
        IDWriteTextLayout* layout = null;
        fixed (char* s = sample)
        {
            DWriteFactory->CreateTextLayout(s, (uint)sample.Length, format, 10000f, 1000f, &layout);
        }
        DWRITE_TEXT_METRICS m;
        layout->GetMetrics(&m);
        layout->Release();
        lineHeight = m.height;
        return m.widthIncludingTrailingWhitespace / sample.Length;
    }
}
