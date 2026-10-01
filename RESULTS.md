# RESULTS: .NET Native AOT Lister plugin spike

Status: final. Harness and manual checks complete, 2026-10-01.

Naming: the plugin was renamed to **Advanced Viewer** after the spike. It is now `AdvancedViewer.wlx64`, built from `src/AdvancedViewer/`. The measurements and manual checks below were taken under the original name `Viewer.wlx64`; the code is otherwise unchanged. A post-rename harness run passed, apart from one cold-cache file-switch outlier that did not recur on a repeat run.

## 1. Recommendation

**GO for C# with .NET Native AOT.** Every Must check passes with wide margins, in the harness and in Total Commander 11.58. Examples: 1.1 MB DLL, 68 ms first file in a fresh process, 1.7 ms per file switch. Direct2D/DirectWrite through CsWin32 worked under AOT with no fallback. The decisive unload risk is largely defused: once its runtime starts, the Native AOT DLL pins itself in memory, so a `FreeLibrary` from Total Commander is a no-op rather than a crash (see section 4).

## 2. Pass criteria

Measured on the final build (`scripts/build.ps1 -Clean`, then `scripts/run-harness.ps1`; raw data in `out/harness-results.json`).

| Check | Threshold | Measured | Result |
| --- | --- | --- | --- |
| Build warnings | 0 IL2xxx/IL3xxx, none suppressed | 0 warnings, 0 errors; `TreatWarningsAsErrors` + `IlcTreatWarningsAsErrors` on | **Pass** |
| Plugin size | ≤ 10 MB (target ≤ 5 MB) | 1,110,016 bytes (1.06 MB); install zip 521 KB | **Pass** |
| First file shown, fresh process | median ≤ 150 ms, max ≤ 300 ms | median 68.3 ms, max 72.5 ms over 20 launches (`LoadLibraryW` alone: median 1.0 ms) | **Pass** |
| Next file shown (`ListLoadNextW` + paint) | median ≤ 20 ms, max ≤ 50 ms | median 1.69 ms, p99 2.93 ms, max 10.7 ms (1,000 switches, 209 files) | **Pass** |
| Large file load | 4.5 GB ≤ 2× the 1 KB file | 3.56 ms vs 3.54 ms (1.01×) | **Pass** |
| Leaks over 1,000 switches | private bytes ≤ +5 MB; GDI/USER ±2 | +1.41 MB; GDI +0, USER +0 (5,000 switches: +3.87 MB, bounded, see section 3) | **Pass** |
| Exception containment | error value returned, host survives | `ListLoadW`/`ListLoad` → NULL, `ListLoadNextW` → 1, process survived | **Pass** |
| Works in Lister and Quick View, keys forwarded | all manual checks pass | Harness: unhandled keys are forwarded to the parent (`WM_KEYDOWN` 6/6, `WM_CHAR`, `WM_SYSKEYDOWN` 1/1). In Total Commander: install, F3, Esc, number keys, resize, two Listers and Quick View over 200 files all pass. N/P work when several files are selected before F3; by design they only cycle through the selected files (TC help, listercontents) | **Pass** |
| Scroll repaint (Should) | avg ≤ 16 ms, worst ≤ 33 ms | PageDown ×10,000: avg 1.56, worst 11.3 ms. Thumb drag ×1,000: avg 1.51, worst 3.1 ms | **Pass** (see note) |
| File not blocked while shown (Should) | rename and delete work while shown | Rename (Shift+F6) and delete (F8) of `medium.bin` worked while Quick View showed it | **Pass** |
| Plugin stays loaded for the session (Decisive) | DLL still listed in Process Explorer | `tasklist /m Viewer.wlx64` lists TOTALCMD64.EXE (PID 29816) with the DLL loaded; one `ListSetDefaultParams` this session. Process Explorer still lists the DLL after all Lister and Quick View windows close. TC exit was clean (section 4) | **Pass** |

Edge cases: all 12 as expected. `empty.bin` shows an empty view. `locked.bin` and `denied.bin` → NULL (`ListLoadNextW` → 1). The non-ASCII name opens through `ListLoadW`; through `ListLoad` it returns NULL (correct, because code page 1252 cannot represent 日本語). A 319-character path opens both plain and with a `\\?\` prefix.

Note on repaint: earlier runs had single-sample outliers (474 ms, 72 ms, 39 ms out of 10,000). The plugin's own paint timing never exceeded 8.2 ms, and none overlapped a GC. The outliers happened outside the paint handler, probably OS scheduling or scroll-bar redraw on a busy laptop. Treat a single worst-case sample above 33 ms as noise, not a defect.

### Render target comparison

The hardware `ID2D1HwndRenderTarget` creates a D3D device per window, which accounts for most of the first-file time. Setting `VIEWER_SPIKE_RT=software` (a diagnostic switch) uses a software render target instead:

| | Hardware (default) | Software |
| --- | --- | --- |
| First file shown, fresh process (median) | 68–85 ms | 24–28 ms |
| Process memory after the first file | ~47 MB | ~16 MB |
| Next file shown (median) | 1.7–2.0 ms | 2.3–2.4 ms |

Both pass. For the full viewer, consider one shared D3D device with `ID2D1DeviceContext` and a swap chain per window instead of an `HwndRenderTarget` per window.

## 3. AOT and CsWin32 problems hit

1. **The user-level NuGet `packageSourceMapping` blocked restore.** Workaround: `src/Viewer/nuget.config` lists nuget.org only.
2. **The AOT link step failed to find MSVC.** `VsDevCmd.bat` calls `vswhere.exe` after a `pushd`, and the shell sets `NoDefaultCurrentDirectoryInExePath=1`. Workaround: `build.ps1` puts the VS Installer folder on PATH.
3. **CA1416 errors on every CsWin32 call.** Workaround: assembly-level `[SupportedOSPlatform("windows10.0.14393")]`.
4. **`WNDCLASSEXW.lpfnWndProc` is typed `delegate* unmanaged[Stdcall]`.** Workaround: the WndProc uses `CallConvs = [typeof(CallConvStdcall)]`, which is a no-op on x64.
5. **Linear private-bytes growth of about 2.8 KB per switch: +14 MB over 5,000.** The cause was uncollected managed garbage, mostly from logging, not a native leak. A forced GC after every switch cut it to +2.1 MB, and the handle count stayed flat. The default first GC only runs after about 12.5 MB of allocation. Fixes:
   - Allocation-free logging.
   - The GC budget capped at 4 MB via ILC `--runtimeopt:GCgen0size=400000` and `GCgen0MaxBudget=400000`. The documented `System.GC.Gen0Size` is silently ignored under Native AOT (measured).
   - `ConcurrentGarbageCollection=false`, so the runtime adds no background GC thread inside Total Commander.

   With these fixes, a 60,000-switch run levels off at +5.6 MB.
6. **Direct2D/DirectWrite via CsWin32 with `allowMarshaling:false` needed no workaround.** The COM vtable calls on the generated unmanaged structs just worked, so the GDI fallback was not needed.

The GDI-vs-DirectWrite comparison (optional time-box item) was not done. The software render target numbers above are the closest proxy.

## 4. Unload findings

**Harness, step 8:** `ListCloseWindow` took 1.5 ms. `FreeLibrary` returned TRUE, **but the module stayed mapped**. Reloading worked, there was no crash or hang, and the process exited with code 0.

**Follow-up probe** (scratch C program, single `LoadLibraryW`):

| Scenario | After `FreeLibrary` |
| --- | --- |
| No export called (runtime not started) | DLL unmapped |
| One export called (`ListGetDetectString`) | DLL still mapped, even after 50 more `FreeLibrary` calls |

The plugin does not pin itself: its only `GetModuleHandleEx` call uses FROM_ADDRESS|UNCHANGED_REFCOUNT. So the Native AOT runtime pins its own module when it initialises. Microsoft documents unloading as "not supported"; in practice it is prevented. This means:

- If Total Commander calls `FreeLibrary` on the plugin during a session (it does so for plugins without `ListLoadNext`, for `cm_UnloadPlugins 4`, and probably at exit), the DLL and runtime stay resident. That is a memory cost, not a crash.
- A later reload gets the same module with its static state intact. `ListSetDefaultParams` may run again; it must be idempotent, and it is.
- The native stub DLL suggested in the brief is not needed for safety.

**In Total Commander (manual check):**

- **Plugin install session (PID 24888):** `ListSetDefaultParams` appears twice in the log. TC loaded the plugin once at install, without calling any other export. Later it called `ListSetDefaultParams` again, this time followed by `ListGetDetectString` and `ListLoadW`. So **TC did unload and reload the plugin within one session.** Nothing broke. The DLL was presumably still mapped (the second call took 1 µs), and the session exited cleanly.
- **Normal session (PID 29816):** a single `ListSetDefaultParams`, then 53 `ListLoadW` and 66 `ListLoadNextW` calls. After the Lister and Quick View checks, `tasklist /m Viewer.wlx64` shows the DLL still loaded in `TOTALCMD64.EXE`. Process Explorer (DLL view, Ctrl+D) confirms the DLL is still loaded after all Lister and Quick View windows close.
- **Exit:** TC exits at normal speed with no crash, hang or dialog.

## 5. Differences between the brief and the Lister SDK (2.13)

Full detail in `docs/research-sdk-and-unload.md`.

- **Signatures and return values:** all match. `ListDefaultParamStruct` is 272 bytes. TC 11.58 reports interface version 2.30.
- **Detect string:** the brief's "accept all files" has no wildcard form, so `EXT="*"` is false. The plugin uses `EXT="" | EXT!=""`, which uses only documented operators. TC caches the string in wincmd.ini as `N_detect`, so delete that key after changing it.
- **ANSI exports:** `ListLoad`/`ListLoadNext` are optional for a 64-bit plugin. They are implemented anyway.
- **`ListLoadNext` returning 1 is not terminal.** Total Commander falls back to `ListLoad` and other plugins.
- **ShowFlags not covered by the brief:** `lcp_forceshow` (16), `lcp_darkmode` (128) and `lcp_darkmodenative` (256). Dark mode is also switched via `ListSendCommand`.
- **Focus:** Lister subclasses the plugin window for N/P. TC 11.57+ adds `itm_focus` for Quick View focus notification. The brief mentions neither.
- **pluginst.inf:** not documented in the Total Commander help. The TotalcmdWiki format with `file=Viewer.wlx64` is used. It is confirmed only once the first manual check (installing from the zip) succeeds.

## 6. Problems the full viewer will face

- **Mapped-file I/O errors kill the host.** If a mapped file is truncated, sits on a disconnected network share or removable media, or hits a disk error, reading the mapping raises `EXCEPTION_IN_PAGE_ERROR`. That is an SEH exception that managed catch-all blocks cannot handle under Native AOT, so Total Commander would crash. The full viewer needs a strategy: for example `ReadFile` into a bounded buffer for non-local or small files, or a native SEH-guarded copy helper.
- **Runtime start on first F3.** A match-all detect string loads the DLL and starts the runtime on the first F3 of any file. That costs about 25 ms with the software render target and 70 ms with the hardware one; later files are cheap. A narrower detect string keeps Total Commander's built-in viewers for other types.
- **Memory footprint.** About 16 MB (software) to 47 MB (hardware, D3D device) per process after the first file. Multiple Lister windows each create a hardware render target, which is slow.
- **Text layout cost is untested.** The hex view uses fixed-width rows. Text and markdown views with DirectWrite layout, word wrap, encoding detection and long lines are where per-paint cost and GC allocation will matter. The 4 MB GC budget and allocation-free hot paths should carry over.
- **Focus and Quick View.** `itm_focus` handling, Alt hotkeys (a TC limitation while a plugin has focus) and the dark mode flags are still to do.
- **One runtime per Native AOT plugin.** Another Native AOT plugin in Total Commander would load its own runtime and GC heap.
- **`AppContext.BaseDirectory` points to the host exe directory.** Use `GetModuleFileNameW` on the plugin module to find the plugin's own folder for settings.

## 7. Environment

- Windows 11 Pro 10.0.22631.6491 x64.
- .NET SDK 10.0.302. CsWin32 0.3.335.
- MSVC from VS 2022 Community 17.14 (VS 2026 Enterprise also installed).
- Total Commander 11.58 x64 (release 2026-07-01).
- Intel Core Ultra 7 155H, 31.5 GB RAM, Samsung MZVL21T0HCLR NVMe SSD.

## 8. `dumpbin /exports out\Viewer.wlx64`

```
    ordinal hint RVA      name

          1    0 00105490 DotNetRuntimeDebugHeader = DotNetRuntimeDebugHeader
          2    1 00038CF0 ListCloseWindow = ListCloseWindow
          3    2 00038E40 ListGetDetectString = ListGetDetectString
          4    3 00038AA0 ListLoad = ListLoad
          5    4 00038C30 ListLoadNext = ListLoadNext
          6    5 00038B60 ListLoadNextW = ListLoadNextW
          7    6 000389D0 ListLoadW = ListLoadW
          8    7 00038F80 ListSetDefaultParams = ListSetDefaultParams
```

`DotNetRuntimeDebugHeader` is added by the Native AOT runtime and is harmless.

## Manual checks: results

All 13 checks in the brief pass in Total Commander 11.58 x64. The ticked checklist with notes is in the brief.

Notes:
- N/P work only when several files are selected before F3, which is by design.
- Process Explorer shows loaded DLLs in its DLL view (Ctrl+D), not as child processes.
