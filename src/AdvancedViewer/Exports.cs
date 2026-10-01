using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// </summary>
internal static unsafe class Exports
{
    private const int LISTPLUGIN_OK = 0;
    private const int LISTPLUGIN_ERROR = 1;

    /// <summary>
    /// Detect string meaning "every file". The SDK grammar has no wildcard (EXT="*" compares
    /// against the literal "*"), and an empty string is undocumented. This tautology uses only
    /// documented operators and does not depend on SIZE (whose width for >4 GB files is
    /// undocumented). MULTIMEDIA is deliberately absent so TC's internal media viewers win.
    /// </summary>
    internal const string DetectString = "EXT=\"\" | EXT!=\"\"";

    [UnmanagedCallersOnly(EntryPoint = "ListLoadW")]
    public static nint ListLoadW(nint parentWin, char* fileToLoad, int showFlags)
    {
        long t0 = Log.Now();
        string? path = null;
        try
        {
            path = fileToLoad == null ? "" : new string(fileToLoad);
            return LoadCore(parentWin, path, t0, "ListLoadW");
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
            return LoadCore(parentWin, path, t0, "ListLoad");
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
            return LoadNextCore(listWin, path, t0, "ListLoadNextW");
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
            return LoadNextCore(listWin, path, t0, "ListLoadNext");
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
            path = HexView.FromHwnd(listWin)?.Path;
            // WM_DESTROY releases the render target and unmaps the file; WM_NCDESTROY frees the GCHandle.
            PInvoke.DestroyWindow((HWND)listWin);
            Log.Write("ListCloseWindow", path, Log.ElapsedUs(t0));
        }
        catch (Exception ex)
        {
            Log.Exception("ListCloseWindow", path, t0, ex);
        }
    }

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

    private static nint LoadCore(nint parentWin, string path, long t0, string evt)
    {
        ThrowTestHook(path);
        nint hwnd = HexView.Create(parentWin, path, t0, evt);
        Log.Write(evt, path, Log.ElapsedUs(t0), hwnd == 0 ? "result=NULL" : $"hwnd=0x{hwnd:X}");
        return hwnd;
    }

    private static int LoadNextCore(nint listWin, string path, long t0, string evt)
    {
        ThrowTestHook(path);
        HexView? view = HexView.FromHwnd(listWin);
        if (view == null)
        {
            Log.Write(evt, path, Log.ElapsedUs(t0), "result=ERROR (unknown window)");
            return LISTPLUGIN_ERROR;
        }
        bool ok = view.LoadNext(path, t0, evt);
        Log.Write(evt, path, Log.ElapsedUs(t0), ok ? "result=OK" : "result=ERROR");
        Diag.AfterLoadNext();
        return ok ? LISTPLUGIN_OK : LISTPLUGIN_ERROR;
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
