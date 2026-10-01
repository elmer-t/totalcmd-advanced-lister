using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.DirectWrite;

namespace AdvancedViewer;

/// <summary>
/// Device-independent Direct2D/DirectWrite resources shared by every viewer window:
/// one ID2D1Factory, one IDWriteFactory, one Consolas IDWriteTextFormat and the measured
/// cell size. Created lazily on the first ListLoad (never in DllMain/static init) and kept
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
}
