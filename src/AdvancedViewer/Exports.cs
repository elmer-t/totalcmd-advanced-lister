using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AdvancedViewer.Hex;
using AdvancedViewer.Hosting;
using AdvancedViewer.Markdown;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AdvancedViewer;

/// <summary>Layout of ListDefaultParamStruct from listplug.h (272 bytes on x64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ListDefaultParamStruct
{
    public int size;
    public uint PluginInterfaceVersionLow;
    public uint PluginInterfaceVersionHi;
    public fixed byte DefaultIniName[260];
}

/// <summary>
/// Total Commander Lister plugin exports (listplug.h). x64 has a single calling convention,
/// so [UnmanagedCallersOnly] without CallConvs matches __stdcall declarations.
/// Every export catches everything: an exception escaping into TC would kill the host.
/// Routing: .md/.markdown/.mdown/.mkd files get a <see cref="MarkdownView"/>, everything else
/// (only reachable with lcp_forceshow, or from the harness) a <see cref="HexView"/>.
/// </summary>
internal static unsafe class Exports
{
    private const int LISTPLUGIN_OK = 0;
    private const int LISTPLUGIN_ERROR = 1;

    // ListSendCommand commands (listplug.h).
    private const int lc_copy = 1, lc_newparams = 2, lc_selectall = 3, lc_setpercent = 4;

    /// <summary>
    /// Markdown files only, so TC's built-in viewers keep every other file and the runtime only
    /// starts when needed. With lcp_forceshow (user picked the plugin from the Lister menu) TC
    /// calls ListLoad for any file anyway; those files get the hex view.
    /// Note: TC caches this as N_detect in wincmd.ini on first sight of the plugin.
    /// </summary>
    internal const string DetectString = "EXT=\"MD\" | EXT=\"MARKDOWN\" | EXT=\"MDOWN\" | EXT=\"MKD\"";

    [UnmanagedCallersOnly(EntryPoint = "ListLoadW")]
    public static nint ListLoadW(nint parentWin, char* fileToLoad, int showFlags)
    {
        long t0 = Log.Now();
        string? path = null;
        try
        {
            path = fileToLoad == null ? "" : new string(fileToLoad);
            return LoadCore(parentWin, path, showFlags, t0, "ListLoadW");
        }
        catch (Exception ex)
        {
            Log.Exception("ListLoadW", path, t0, ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ListLoad")]
    public static nint ListLoad(nint parentWin, byte* fileToLoad, int showFlags)
    {
        long t0 = Log.Now();
        string? path = null;
        try
        {
            path = Ansi.ToString(fileToLoad);
            return LoadCore(parentWin, path, showFlags, t0, "ListLoad");
        }
        catch (Exception ex)
        {
            Log.Exception("ListLoad", path, t0, ex);
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ListLoadNextW")]
    public static int ListLoadNextW(nint parentWin, nint listWin, char* fileToLoad, int showFlags)
    {
        long t0 = Log.Now();
        string? path = null;
        try
        {
            path = fileToLoad == null ? "" : new string(fileToLoad);
            return LoadNextCore(listWin, path, showFlags, t0, "ListLoadNextW");
        }
        catch (Exception ex)
        {
            Log.Exception("ListLoadNextW", path, t0, ex);
            return LISTPLUGIN_ERROR;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ListLoadNext")]
    public static int ListLoadNext(nint parentWin, nint listWin, byte* fileToLoad, int showFlags)
    {
        long t0 = Log.Now();
        string? path = null;
        try
        {
            path = Ansi.ToString(fileToLoad);
            return LoadNextCore(listWin, path, showFlags, t0, "ListLoadNext");
        }
        catch (Exception ex)
        {
            Log.Exception("ListLoadNext", path, t0, ex);
            return LISTPLUGIN_ERROR;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ListCloseWindow")]
    public static void ListCloseWindow(nint listWin)
    {
        long t0 = Log.Now();
        string? path = null;
        try
        {
            path = ViewWindow.FromHwnd(listWin)?.View.Path;
            // WM_DESTROY releases the render target and unloads the view; WM_NCDESTROY frees the GCHandle.
            PInvoke.DestroyWindow((HWND)listWin);
            Log.Write("ListCloseWindow", path, Log.ElapsedUs(t0));
        }
        catch (Exception ex)
        {
            Log.Exception("ListCloseWindow", path, t0, ex);
        }
    }

    /// <summary>
    /// int __stdcall ListSendCommand(HWND ListWin, int Command, int Parameter). ANSI-only export
    /// by the SDK (no W form). lc_newparams carries the new lcp_* show flags; TC uses it to switch
    /// dark mode at runtime (HISTORY.TXT 22.01.20), so the theme is re-derived from lcp_darkmode.
    /// Copy / select all / set percent are not supported yet: logged, and OK returned.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "ListSendCommand")]
    public static int ListSendCommand(nint listWin, int command, int parameter)
    {
        long t0 = Log.Now();
        string? path = null;
        try
        {
            ViewWindow? win = ViewWindow.FromHwnd(listWin);
            path = win?.View.Path;
            if (win != null && command == lc_newparams)
                win.SetTheme(Theme.FromShowFlags(parameter));
            Log.Write("ListSendCommand", path, Log.ElapsedUs(t0), CommandName(command) + " parameter=", parameter, hasNum: true);
            return LISTPLUGIN_OK;
        }
        catch (Exception ex)
        {
            Log.Exception("ListSendCommand", path, t0, ex);
            return LISTPLUGIN_ERROR;
        }
    }

    private static string CommandName(int command) => command switch
    {
        lc_copy => "lc_copy",
        lc_newparams => "lc_newparams",
        lc_selectall => "lc_selectall",
        lc_setpercent => "lc_setpercent",
        _ => "command=" + command,
    };

    [UnmanagedCallersOnly(EntryPoint = "ListGetDetectString")]
    public static void ListGetDetectString(byte* detectString, int maxLen)
    {
        long t0 = Log.Now();
        try
        {
            if (detectString == null || maxLen <= 0) return;
            int n = Math.Min(DetectString.Length, maxLen - 1);
            for (int i = 0; i < n; i++) detectString[i] = (byte)DetectString[i];
            detectString[n] = 0;
            Log.Write("ListGetDetectString", null, Log.ElapsedUs(t0), DetectString + " maxLen=" + maxLen);
        }
        catch (Exception ex)
        {
            Log.Exception("ListGetDetectString", null, t0, ex);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ListSetDefaultParams")]
    public static void ListSetDefaultParams(ListDefaultParamStruct* dps)
    {
        long t0 = Log.Now();
        try
        {
            if (dps == null) { Log.Write("ListSetDefaultParams", null, -1, "dps=NULL"); return; }
            string ini = Ansi.ToString(dps->DefaultIniName, 260);
            Log.Write("ListSetDefaultParams", ini, Log.ElapsedUs(t0),
                $"size={dps->size} version={dps->PluginInterfaceVersionHi}.{dps->PluginInterfaceVersionLow}");
        }
        catch (Exception ex)
        {
            Log.Exception("ListSetDefaultParams", null, t0, ex);
        }
    }

    // ------------------------------------------------------------------------------------

    private static nint LoadCore(nint parentWin, string path, int showFlags, long t0, string evt)
    {
        ThrowTestHook(path);
        nint hwnd = ViewWindow.Create(parentWin, CreateView(path), path, showFlags, t0, evt);
        Log.Write(evt, path, Log.ElapsedUs(t0), hwnd == 0 ? "result=NULL" : $"hwnd=0x{hwnd:X}");
        return hwnd;
    }

    private static int LoadNextCore(nint listWin, string path, int showFlags, long t0, string evt)
    {
        ThrowTestHook(path);
        ViewWindow? win = ViewWindow.FromHwnd(listWin);
        if (win == null)
        {
            Log.Write(evt, path, Log.ElapsedUs(t0), "result=ERROR (unknown window)");
            return LISTPLUGIN_ERROR;
        }
        // Same kind of file: reuse the view. Other kind: a fresh view replaces it on success.
        View view = (win.View is MarkdownView) == IsMarkdownPath(path) ? win.View : CreateView(path);
        bool ok = win.LoadNext(view, path, showFlags, t0, evt);
        Log.Write(evt, path, Log.ElapsedUs(t0), ok ? "result=OK" : "result=ERROR");
        Diag.AfterLoadNext();
        return ok ? LISTPLUGIN_OK : LISTPLUGIN_ERROR;
    }

    private static View CreateView(string path) => IsMarkdownPath(path) ? new MarkdownView() : new HexView();

    /// <summary>True for .md, .markdown, .mdown, .mkd (case-insensitive), matching the detect string.</summary>
    internal static bool IsMarkdownPath(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0 || path.IndexOf('\\', dot) >= 0 || path.IndexOf('/', dot) >= 0) return false;
        ReadOnlySpan<char> ext = path.AsSpan(dot + 1);
        return ext.Equals("md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals("markdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals("mdown", StringComparison.OrdinalIgnoreCase)
            || ext.Equals("mkd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Throw-test build only (SPIKE_THROW): a file named __throw__.bin throws inside the export.</summary>
    private static void ThrowTestHook(string path)
    {
#if SPIKE_THROW
        if (HexView.IsThrowFile(path))
            throw new InvalidOperationException("SPIKE_THROW: deliberate test exception for " + path);
#endif
    }
}

internal static unsafe class Ansi
{
    /// <summary>Converts a NUL-terminated string in the ANSI code page (CP_ACP) to UTF-16.</summary>
    public static string ToString(byte* s, int maxLen = int.MaxValue)
    {
        if (s == null) return "";
        int len = 0;
        while (len < maxLen && s[len] != 0) len++;
        if (len == 0) return "";
        int chars = PInvoke.MultiByteToWideChar(0 /* CP_ACP */, 0, (PCSTR)s, len, (PWSTR)null, 0);
        if (chars <= 0) throw new InvalidOperationException("MultiByteToWideChar failed, Win32 error " + Marshal.GetLastPInvokeError());
        string result = new string('\0', chars);
        fixed (char* d = result)
        {
            PInvoke.MultiByteToWideChar(0, 0, (PCSTR)s, len, d, chars);
        }
        return result;
    }
}
