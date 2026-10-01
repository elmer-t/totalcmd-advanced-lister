using System;
using System.Diagnostics;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace AdvancedViewer;

/// <summary>
/// Append-only event log at %TEMP%\viewer-spike.log. One tab-separated line per event:
///   qpc  pid  event  path  us  info
/// qpc = raw QueryPerformanceCounter ticks; us = duration in microseconds (-1 if n/a);
/// info = exception text (newlines folded) or extra data. The first line written by each
/// process is a LOG_OPEN event carrying the QPC frequency.
/// Each line is one unbuffered WriteFile on a FILE_APPEND_DATA handle, so lines from
/// concurrent processes never interleave and survive a host crash.
///
/// The hot path (one line per ListLoadNextW and per WM_PAINT) does not allocate: lines are
/// formatted into reused char/byte buffers. All calls happen on TC's UI thread, so the
/// shared buffers need no locking.
/// </summary>
internal static unsafe class Log
{
    private static HANDLE s_handle;
    private static bool s_failed;
    private static readonly uint s_pid = PInvoke.GetCurrentProcessId();

    private static char[] s_chars = new char[1024];
    private static byte[] s_bytes = new byte[4096];
    private static int s_len;

    public static long Now() => Stopwatch.GetTimestamp();

    public static long ElapsedUs(long startTicks) =>
        (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;

    /// <summary>Writes a line. If <paramref name="hasNum"/> is set, <paramref name="num"/> is appended right after <paramref name="info"/> (no allocation).</summary>
    public static void Write(string evt, string? path, long us, string? info = null, long num = 0, bool hasNum = false)
    {
        if (Diag.NoLog) return;
        WriteForced(evt, path, us, info, num, hasNum);
    }

    public static void WriteForced(string evt, string? path, long us, string? info = null, long num = 0, bool hasNum = false)
    {
        try
        {
            if (!EnsureOpen()) return;
            s_len = 0;
            Append(Stopwatch.GetTimestamp()); Append('\t');
            Append(s_pid); Append('\t');
            Append(evt); Append('\t');
            if (path != null) Append(path);
            Append('\t');
            Append(us); Append('\t');
            if (info != null) AppendSanitized(info);
            if (hasNum) Append(num);
            Append('\r'); Append('\n');
            Flush();
        }
        catch
        {
            // Logging must never take down the host.
        }
    }

    public static void Exception(string evt, string? path, long startTicks, Exception ex) =>
        Write(evt, path, startTicks == 0 ? -1 : ElapsedUs(startTicks), "EXCEPTION " + ex.ToString());

    // ---- formatting into s_chars ----------------------------------------------------------

    private static void Ensure(int extra)
    {
        if (s_len + extra <= s_chars.Length) return;
        Array.Resize(ref s_chars, Math.Max(s_chars.Length * 2, s_len + extra));
    }

    private static void Append(char c)
    {
        Ensure(1);
        s_chars[s_len++] = c;
    }

    private static void Append(string s)
    {
        Ensure(s.Length);
        s.CopyTo(0, s_chars, s_len, s.Length);
        s_len += s.Length;
    }

    private static void AppendSanitized(string s)
    {
        Ensure(s.Length);
        foreach (char c in s)
        {
            if (c == '\r') continue;
            s_chars[s_len++] = c == '\n' || c == '\t' ? ' ' : c;
        }
    }

    private static void Append(long v)
    {
        Ensure(20);
        v.TryFormat(s_chars.AsSpan(s_len), out int written);
        s_len += written;
    }

    private static void Flush()
    {
        int max = Encoding.UTF8.GetMaxByteCount(s_len);
        if (s_bytes.Length < max) s_bytes = new byte[max];
        int n = Encoding.UTF8.GetBytes(s_chars, 0, s_len, s_bytes, 0);
        fixed (byte* p = s_bytes)
        {
            uint written;
            PInvoke.WriteFile(s_handle, p, (uint)n, &written, null);
        }
    }

    private static bool EnsureOpen()
    {
        if (s_failed) return false;
        if (s_handle != HANDLE.Null) return true;

        char* tmp = stackalloc char[300];
        uint len = PInvoke.GetTempPath(300, tmp);
        if (len == 0 || len >= 300) { s_failed = true; return false; }
        string path = new string(tmp, 0, (int)len) + "viewer-spike.log";

        const uint FILE_APPEND_DATA = 0x0004;
        fixed (char* p = path)
        {
            HANDLE h = PInvoke.CreateFile(
                p,
                FILE_APPEND_DATA,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_ALWAYS,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                HANDLE.Null);
            if (h == HANDLE.Null || h == (HANDLE)(nint)(-1)) { s_failed = true; return false; }
            s_handle = h;
        }
        s_len = 0;
        Append(Stopwatch.GetTimestamp()); Append('\t');
        Append(s_pid); Append("\tLOG_OPEN\t\t-1\tqpcFreq=");
        Append(Stopwatch.Frequency); Append("\r\n");
        Flush();
        return true;
    }
}
