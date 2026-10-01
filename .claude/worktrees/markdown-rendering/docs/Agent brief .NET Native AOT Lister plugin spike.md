# Agent brief: .NET Native AOT Lister plugin spike

Oct 1, 2026 · @E

## Purpose

This spike decides whether to build a fast file viewer as a Total Commander Lister plugin in C# compiled with .NET Native AOT, or to switch to Rust. It is time-boxed to one working day.

The spike does not build the viewer. It builds the smallest plugin that exercises the risky parts: C-ABI exports from a Native AOT DLL, a child window inside Total Commander's Lister, Direct2D/DirectWrite rendering under AOT, and memory-mapped access to very large files. The output is a measured go/no-go recommendation, not reusable product code.

The four questions to answer:

1. Does a Native AOT DLL load and run correctly as a 64-bit Lister plugin (`.wlx64`)?
2. Are load time, DLL size and memory use within the thresholds in this brief?
3. Does it stay stable when Quick View cycles through hundreds of files, including very large and unreadable ones?
4. Does Total Commander ever unload Lister plugins during a session? Native AOT DLLs cannot be unloaded safely, so this decides whether the runtime-in-host risk is real.

## Scope

In scope:

- A Native AOT DLL exporting the Lister plugin functions listed under What to build.
- One view only: a hex view (offset, hex bytes, ASCII) over a memory-mapped file, rendered with Direct2D/DirectWrite.
- A small test harness exe that loads the plugin outside Total Commander and measures it.
- A results report with measured numbers and a recommendation.

Out of scope (do not start these, even if time remains):

- Text view, markdown rendering, image rendering, encoding detection.
- Settings, configuration UI, search (`ListSearchText`), printing (`ListPrint`).
- 32-bit Total Commander and `.wlx` (32-bit) builds.
- Code signing, installer polish, performance tuning beyond what is needed to measure.

If time remains after all checks pass, spend it on the GDI-vs-DirectWrite comparison described under Time-box, not on new features.

## Environment and constraints

The spike must run on a Windows 10/11 x64 machine. Native AOT does not cross-compile from macOS, and Total Commander only runs on Windows.

Toolchain:

- .NET 10 SDK.
- Visual Studio 2022 or later with the "Desktop development with C++" workload. Native AOT needs the MSVC linker.
- 64-bit Total Commander, current release, installed on the same machine.
- Sysinternals Process Explorer, for checking whether the plugin DLL stays loaded.

Project settings (plugin project):

- `PublishAot=true`, `NativeLib=Shared`, `RuntimeIdentifier=win-x64`, `AllowUnsafeBlocks=true`, `InvariantGlobalization=true`.
- Treat every trim and AOT warning (IL2xxx, IL3xxx) as an error. The spike fails if a warning has to be suppressed to build.
- Output `Viewer.dll` is copied and renamed to `Viewer.wlx64` by the build script.

Hard rules for the code:

- The only NuGet package allowed is `Microsoft.Windows.CsWin32`. No WinForms, WPF, WinUI, Avalonia or Vortice.
- Configure CsWin32 with `"allowMarshaling": false` so Direct2D and DirectWrite COM interfaces are generated as unmanaged structs. Built-in COM interop is not available under Native AOT.
- Exports use `[UnmanagedCallersOnly(EntryPoint = "...")]` with blittable parameters only (`nint`, `char*`, `byte*`, `int`).
- The window procedure is an `[UnmanagedCallersOnly]` static method passed as a function pointer. Per-window state is reached through a `GCHandle` stored in `GWLP_USERDATA`.
- Every export and the window procedure wrap their whole body in a catch-all. An exception that escapes crashes Total Commander. Log the exception and return the API's error value.
- No background threads. Total Commander calls the plugin on its UI thread; keep all work there.

## What to build

Build two projects in one solution: the plugin (`Viewer`, C# Native AOT) and a native test harness (`Harness`). Before writing code, download and read Total Commander's Lister plugin SDK from ghisler.com. Where this brief and the SDK disagree on a signature or return value, follow the SDK and note the difference in the report.

### Plugin exports

| Export | Signature (C) | Required behaviour |
| --- | --- | --- |
| `ListLoadW` | `HWND ListLoadW(HWND parent, WCHAR* file, int showFlags)` | Open the file, create the child window, return its handle. Return `NULL` if the file cannot be opened. |
| `ListLoad` | `HWND ListLoad(HWND parent, char* file, int showFlags)` | Convert the path from the ANSI code page and call the `ListLoadW` code path. |
| `ListLoadNextW` | `int ListLoadNextW(HWND parent, HWND listWin, WCHAR* file, int showFlags)` | Release the current file, open the new one in the same window, repaint. Return 0 (OK) or 1 (error). |
| `ListLoadNext` | `int ListLoadNext(HWND parent, HWND listWin, char* file, int showFlags)` | ANSI wrapper around `ListLoadNextW`. |
| `ListCloseWindow` | `void ListCloseWindow(HWND listWin)` | Unmap and close the file, free the `GCHandle`, destroy the window. |
| `ListGetDetectString` | `void ListGetDetectString(char* detect, int maxLen)` | Accept all files. Check the SDK for the exact string that achieves this. |
| `ListSetDefaultParams` | `void ListSetDefaultParams(ListDefaultParamStruct* dps)` | Log the interface version and ini path. No other behaviour. |

Verify the exports with `dumpbin /exports Viewer.wlx64` and include the output in the report.

### Window and input

- Create the window with `WS_CHILD | WS_VISIBLE | WS_VSCROLL`, sized to the parent's client area. Total Commander resizes it afterwards; handle `WM_SIZE`.
- Handle Up, Down, PageUp, PageDown, Home, End, Ctrl+Home, Ctrl+End and the mouse wheel.
- Forward every other `WM_KEYDOWN`, `WM_SYSKEYDOWN` and `WM_CHAR` to the parent window with `PostMessage`. Esc, N/P and Lister's mode keys (1–7) must keep working.

### Hex view

- Open the file with `CreateFileW` using `FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE`, then map it read-only with `CreateFileMapping` and `MapViewOfFile`. Never read the whole file into managed memory.
- Handle 0-byte files separately. `CreateFileMapping` fails on empty files; show an empty view instead of failing.
- Each row shows 16 bytes: offset (8 hex digits, 16 if the file is 4 GB or larger), the hex bytes in two groups of 8, and an ASCII column with `.` for non-printable bytes.
- Render only visible rows, with Direct2D (`ID2D1HwndRenderTarget`) and DirectWrite, font Consolas.
- Win32 scroll bars use 32-bit positions. Map the 64-bit row index onto 0–`int.MaxValue` so thumb dragging works on files larger than 32 GB.

### Logging

Append one line per event to `%TEMP%\viewer-spike.log`: QPC timestamp, export or message name, file path, duration in microseconds, and any exception text. For `ListLoadW` and `ListLoadNextW`, log the time from export entry to the end of the first `WM_PAINT`.

### Test harness

Write the harness in C++ (Win32 console app, no managed code), so it behaves like Total Commander: a native process with no .NET runtime of its own. It must:

1. Create a top-level parent window and run a message loop.
2. Time `LoadLibraryW` on the plugin. Run this as 20 separate process launches and report median and maximum.
3. Resolve every export with `GetProcAddress` and fail loudly if one is missing.
4. Time `ListLoadW` followed by `UpdateWindow` (which delivers `WM_PAINT` synchronously) for each test file.
5. Cycle `ListLoadNextW` through all test files 1,000 times. Record per-call time, process private bytes, and GDI/USER object counts (`GetGuiResources`) at start and end.
6. On the largest test file, send 10,000 PageDown key presses and 1,000 thumb-drag positions. Record average and worst time per repaint.
7. Load a file named `__throw__.bin`. A debug build of the plugin throws on that name; confirm the export returns its error value and the harness process survives.
8. As the last step, call `ListCloseWindow` then `FreeLibrary`, and record what happens. A crash or hang here is an expected finding, not a harness bug.

## Measurements and pass criteria

The recommendation is "go" only if every Must check passes. A failed Should check is reported with an estimate of the work needed to fix it. The thresholds are proposals for this spike, not measured baselines.

| Check | Threshold | Measured by | Level |
| --- | --- | --- | --- |
| Build warnings | Zero IL2xxx/IL3xxx warnings, none suppressed | Build output | Must |
| Plugin size | ≤ 10 MB (target ≤ 5 MB) | File size of `Viewer.wlx64` | Must |
| First file shown, fresh process | Median ≤ 150 ms, max ≤ 300 ms (includes runtime start and Direct2D/DirectWrite setup) | Harness steps 2 and 4 | Must |
| Next file shown (`ListLoadNextW` + paint) | Median ≤ 20 ms, max ≤ 50 ms | Harness step 5 | Must |
| Large file load | 4.5 GB file loads within 2× the time of a 1 KB file | Harness step 4 | Must |
| Leaks over 1,000 file switches | Private bytes grow ≤ 5 MB; GDI and USER object counts return to start ±2 | Harness step 5 | Must |
| Exception containment | Export returns its error value; host survives | Harness step 7 | Must |
| Works in Lister and Quick View, keys forwarded | All manual checks pass | Manual checks | Must |
| Scroll repaint | Average ≤ 16 ms, worst ≤ 33 ms | Harness step 6 | Should |
| File not blocked while shown | Total Commander can rename or delete the file while the plugin shows it | Manual checks | Should |
| Plugin stays loaded for the session | DLL listed in Process Explorer after all Lister windows close | Manual checks | Decisive finding, see below |

If Total Commander unloads the plugin during a session, report it as a blocker regardless of the other results. A Native AOT DLL cannot be unloaded safely, so the fix would be architectural (for example, a small native stub DLL that loads the AOT DLL once and never frees it). Describe the observed behaviour precisely so that option can be evaluated.

## Test files

Generate all test files with a script (`make-testfiles.ps1`) into a single folder, so the harness and the manual checks use the same set.

| File | How to create | What it tests |
| --- | --- | --- |
| `empty.bin` (0 bytes) | `New-Item` | The `CreateFileMapping` special case |
| `one.bin` (1 byte) | Write one byte | Partial last row |
| `small.bin` (1 KB) | Random bytes | Baseline load time |
| `medium.bin` (100 MB) | Random bytes | Mapping and scrolling at moderate size |
| `huge.bin` (4.5 GB) | `fsutil file createnew` | 16-digit offsets, load time independent of size, scroll bar mapping |
| `locked.bin` | Held open by a helper process with no sharing | `ListLoadW` returns `NULL` cleanly |
| `denied.bin` | ACL denying read to the current user | Access-denied path |
| `naïve – 日本語.bin` | Non-ASCII file name | `ListLoadW` path handling and the ANSI fallback |
| Long path (> 260 characters) | Nested folders | Long path handling |
| `__throw__.bin` | Any content | Exception containment (debug build only) |
| 200 mixed files | Copy of a real folder (source code, images, documents) | Quick View cycling in the manual checks |

## Manual checks in Total Commander

The agent cannot operate Total Commander, so Elmer runs these checks and records the results. The agent provides a zip containing `Viewer.wlx64` and a `pluginst.inf`, so opening the zip in Total Commander offers to install the plugin. Tick each check and note anything unexpected next to it.

- [x] Install the plugin from the zip. F3 on `small.bin` shows the hex view.
- [x] F3 on `huge.bin` shows the view with no noticeable delay compared to `small.bin`.
- [x] Esc closes Lister
- [x] N and P move to the next and previous file. -> Does not respond to N / P keystrokes
- [x] Lister's number keys and Options menu switch to the built-in modes and back to the plugin.
- [x] Resizing and maximizing the Lister window redraws the view correctly.
- [x] Two Lister windows open at the same time on different files both work and scroll independently.
- [x] Quick View (Ctrl+Q): hold the Down arrow through the 200 mixed files. No crash, no error dialogs, focus stays in the file panel.
- [x] Quick View on `empty.bin`, `locked.bin`, `denied.bin` and the non-ASCII file: no crash; locked and denied files fall back to the built-in Lister or show nothing.
- [x] While Quick View shows `medium.bin`, rename it (Shift+F6) and then delete it (F8). Record whether each works.
- [x] After moving the cursor off a file, renaming and deleting it works.
- [x] Close all Lister windows and Quick View. In Process Explorer, check whether `Viewer.wlx64` is still loaded in `TOTALCMD64.EXE`. Check again after 5 minutes idle. -> No sub processes visible at all under TOTALCMD64.EXE
- [x] Exit Total Commander. It closes at normal speed with no crash, hang or error dialog.

When done, send the agent this checklist with notes and the file `%TEMP%\viewer-spike.log`.

## Time-box and stop rules

The spike has 8 working hours. Use this order so that the riskiest question is answered first:

1. Hours 0–1: read the SDK, scaffold both projects, configure CsWin32. A stub `ListLoadW` that returns `NULL` must build with Native AOT, appear in `dumpbin /exports`, and be callable from the harness.
2. Hours 1–3: child window, file mapping, hex view with Direct2D/DirectWrite.
3. Hours 3–5: test file script and harness steps 2–8.
4. Hours 5–6.5: run all measurements; fix Must failures caused by plugin code.
5. Hours 6.5–8: write the report and the install zip; hand over for manual checks.

Stop rules:

- If after 2 hours there is no AOT-built DLL whose exports the harness can call, stop and report the blocker. That alone is a no-go finding.
- If Direct2D/DirectWrite through CsWin32 does not render after 2 hours of work on it, switch the hex view to GDI (`ExtTextOutW`), finish the spike, and report the DirectWrite problem as a major finding. The full viewer needs DirectWrite for text and markdown.
- If a Must threshold fails because of the .NET runtime itself (start-up, size, unloading) rather than plugin code, do not try to optimise it away. Report the number and the cause.
- Never suppress a trim or AOT warning to make the build pass.
- Do not start any out-of-scope work. If time remains, compare GDI and DirectWrite rendering on first-paint time and DLL size, and add the result to the report.

## Deliverables

Repository layout:

- `src/Viewer/`: the plugin project, `NativeMethods.txt` and `NativeMethods.json`.
- `src/Harness/`: the C++ harness.
- `scripts/build.ps1`: publishes with Native AOT, renames the DLL to `Viewer.wlx64`, and builds the install zip with `pluginst.inf`. Check the `pluginst.inf` format for 64-bit plugins in Total Commander's help.
- `scripts/make-testfiles.ps1`: creates the test file set.
- `RESULTS.md`: the report.

`RESULTS.md` contains, in this order:

1. Recommendation: go or no-go, with the reason in at most three sentences.
2. The pass-criteria table with a Measured column and Pass/Fail for each row.
3. AOT and CsWin32 problems hit during the spike, with the workaround used or the reason none was found.
4. Unload findings: the `FreeLibrary` result from the harness and the Process Explorer result from the manual checks.
5. Differences found between this brief and the Lister plugin SDK.
6. Problems the full viewer will face that the spike exposed (for example, text layout cost or focus handling in Quick View).
7. Environment: Windows build, .NET SDK version, Total Commander version, CPU and disk type.
8. `dumpbin /exports` output.

The agent writes `RESULTS.md` at the end of the day with the harness results and Manual checks marked as pending. After Elmer returns the checklist and log, the agent adds the manual results and states the final recommendation.
