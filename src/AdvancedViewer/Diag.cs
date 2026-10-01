using System;
using Windows.Win32;
using Windows.Win32.System.ProcessStatus;

namespace AdvancedViewer;

/// <summary>
/// Spike diagnostics, all off unless the environment variable is set in the host process:
///   VIEWER_SPIKE_DIAG=N     every N-th ListLoadNextW logs a DIAG line: private bytes, handle count,
///                           GC heap size/committed and gen0/1/2 collection counts.
///   VIEWER_SPIKE_GC=1       full blocking GC after every ListLoadNextW (separates managed garbage
///                           from native growth).
///   VIEWER_SPIKE_NOLOG=1    no log lines except DIAG lines (measures logging cost/effect).
/// Paints that overlapped a garbage collection are tagged "gc=N" in the WM_PAINT log line.
/// </summary>
internal static unsafe class Diag
{
    public static readonly int Every = ParseInt(Environment.GetEnvironmentVariable("VIEWER_SPIKE_DIAG"));
    public static readonly bool ForceGc = Environment.GetEnvironmentVariable("VIEWER_SPIKE_GC") == "1";
    public static readonly bool NoLog = Environment.GetEnvironmentVariable("VIEWER_SPIKE_NOLOG") == "1";

    private static long s_switches;

    private static int ParseInt(string? s) => int.TryParse(s, out int v) && v > 0 ? v : 0;

    public static int GcCount() => GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);

    public static void AfterLoadNext()
    {
        if (ForceGc)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        if (Every == 0) return;
        if (++s_switches % Every != 0) return;

        PROCESS_MEMORY_COUNTERS_EX pmc = default;
        pmc.cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX);
        PInvoke.K32GetProcessMemoryInfo(PInvoke.GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&pmc, pmc.cb);
        uint handles = 0;
        PInvoke.GetProcessHandleCount(PInvoke.GetCurrentProcess(), &handles);
        GCMemoryInfo gi = GC.GetGCMemoryInfo();
        Log.WriteForced("DIAG", null, -1,
            $"switch={s_switches} priv={pmc.PrivateUsage} handles={handles} gcHeap={gi.HeapSizeBytes} gcCommitted={gi.TotalCommittedBytes} " +
            $"totalMem={GC.GetTotalMemory(false)} allocated={GC.GetTotalAllocatedBytes()} gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)}");
    }
}
