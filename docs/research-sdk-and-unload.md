# Research: Lister plugin SDK, pluginst.inf, plugin unloading, NativeAOT hosting pitfalls

Date: 2026-10-01. Installed TC: 11.58 (HISTORY.TXT first line: "01.07.26 Release Total Commander 11.58 official release").
Marking: [VERIFIED] = read from primary source in this session. [SECONDARY] = web-search summary or second-hand, not read in full. [UNVERIFIED] = inference or gap.

Primary sources read locally:
- SDK help: https://plugins.ghisler.com/lsplugins/listplughelp2.13_chm.zip (listplugin.chm, interface version 2.13, 2026-04-07), decompiled with hh.exe. Cited below as "SDK:<page>".
- SDK sample: https://plugins.ghisler.com/lsplugins/listplugsample.zip (old 1.x listplug.h, ANSI only; not authoritative for 2.x).
- TC help: `C:\Program Files\totalcmd\TOTALCMD.CHM`, decompiled. Cited as "TC help:<page>".
- `C:\Program Files\totalcmd\HISTORY.TXT`. Cited as "HISTORY:<date>".
- Plugin index page: https://www.ghisler.com/plugins.htm (links the 2.13 guide).

The ghisler.ch forum is behind an Anubis JavaScript challenge. WebFetch and curl only get a "Loading..." page, and the Chrome extension was not connected. Every forum claim below is therefore [SECONDARY] (search-engine summaries). Re-check the cited threads manually.

---

## 1. Lister plugin SDK 2.13: signatures and the brief

### 1.1 Exact declarations (SDK:listplug.h, [VERIFIED])

```c
#define lcp_wraptext 1 / lcp_fittowindow 2 / lcp_ansi 4 / lcp_ascii 8 / lcp_variable 12
#define lcp_forceshow 16 / lcp_fitlargeronly 32 / lcp_center 64
#define lcp_darkmode 128 / lcp_darkmodenative 256
#define itm_percent 0xFFFE  itm_fontstyle 0xFFFD  itm_wrap 0xFFFC  itm_fit 0xFFFB
#define itm_next 0xFFFA  itm_center 0xFFF9  itm_focus 0xFFF8
#define LISTPLUGIN_OK 0
#define LISTPLUGIN_ERROR 1

typedef struct {
  int size;
  DWORD PluginInterfaceVersionLow;
  DWORD PluginInterfaceVersionHi;
  char DefaultIniName[MAX_PATH];     // ANSI, MAX_PATH = 260
} ListDefaultParamStruct;            // 4+4+4+260 = 272 bytes, no padding

HWND __stdcall ListLoad(HWND ParentWin,char* FileToLoad,int ShowFlags);
HWND __stdcall ListLoadW(HWND ParentWin,WCHAR* FileToLoad,int ShowFlags);
int  __stdcall ListLoadNext(HWND ParentWin,HWND PluginWin,char* FileToLoad,int ShowFlags);
int  __stdcall ListLoadNextW(HWND ParentWin,HWND PluginWin,WCHAR* FileToLoad,int ShowFlags);
void __stdcall ListCloseWindow(HWND ListWin);
void __stdcall ListGetDetectString(char* DetectString,int maxlen);
void __stdcall ListSetDefaultParams(ListDefaultParamStruct* dps);
```

Semantics from SDK:ListLoad, ListLoadNext, ListCloseWindow, ListGetDetectString, ListSetDefaultParams, ListDefaultParamStruct and unicode_support [VERIFIED]:

- **ListLoad(W)** is called on F3 and on Ctrl+Q (Quick View), "when the definition string either doesn't exist, or its evaluation returns true". It returns the plugin window handle, or NULL on failure. "If NULL is returned, Lister will try the next plugin."
- **ListLoadNext(W)** is called on 'n'/'p' in Lister and when moving to the next or previous file in the Quick View panel. It returns LISTPLUGIN_OK (0) on success and LISTPLUGIN_ERROR (1) on failure. On error, "Lister will try to load the file with the normal ListLoad function (also with other plugins)."
- **ListCloseWindow** is called "when a user closes lister, or loads a different file". "If ListCloseWindow isn't present, DestroyWindow() is called." The `ListWin` parameter is "the window handle which needs to be destroyed", so the plugin destroys the window itself.
- **ListSetDefaultParams** is "called immediately after loading the DLL, before ListLoad". The version fields: "the value after the comma, multiplied by 100. Example: for 1.3 the low DWORD is 30 and the high DWORD is 1". For 2.13 that would be Hi=2, Low=13 [UNVERIFIED: not observed from TC 11.58]. `DefaultIniName` is a fully qualified path in the wincmd.ini directory.
- **Unicode** (SDK:unicode_support): Unicode variants exist only for ListLoad, ListLoadNext, ListPrint, ListGetPreviewBitmap and ListSearchText. "ListCloseWindow, ListGetDetectString, ListSetDefaultParams, ListSendCommand, ListNotificationReceived, ListSearchDialog do not exist in a Unicode form and must be implemented as ANSI." TC calls the W functions if present on NT-based systems.
- **64-bit** (SDK:64_bit_support): "A 64-bit plugin must have the same name as the 32-bit plugin... but '64' must be appended to the extension... 64-bit-only plugins must also end with '64'." "Total Commander 64-bit also supports the ANSI functions if it cannot find the Unicode functions." So the ANSI `ListLoad` and `ListLoadNext` exports are optional for a 64-bit-only plugin.

### 1.2 Differences between the brief and the SDK

Signatures and return values in the brief's table match the SDK. The differences are all in behaviour and omissions:

1. **Calling convention.** The SDK says `__stdcall`. On x64 there is a single convention, so `UnmanagedCallersOnly` with the default convention is fine. Do not add `CallConvStdcall` for x64 (harmless but pointless).
2. **ANSI exports are optional on x64.** The brief makes them required ("ListLoad ... Convert the path from the ANSI code page"). TC 64 prefers the W exports. Keep the ANSI ones only as a cheap fallback. TC never passes ANSI names longer than MAX_PATH-1 to ANSI functions (HISTORY:29.03.09).
3. **ListLoadNextW failure is not terminal.** On LISTPLUGIN_ERROR, TC falls back to ListLoad and other plugins or built-in modes (SDK:listloadnext). The brief says only "Return 0 (OK) or 1 (error)". What TC does with the existing plugin window after an error is not documented [UNVERIFIED]. The plugin should tolerate ListCloseWindow arriving for a window that failed ListLoadNext, and handle ListLoadNext and ListLoad on the same DLL interleaved.
4. **NULL from ListLoadW means "try the next plugin"**, not an error shown to the user. The brief's expectation for `locked.bin` ("fall back to the built-in Lister or show nothing") matches.
5. **ShowFlags are richer than the brief implies.** `lcp_forceshow` (16) means the user explicitly chose a plugin or "Image/Multimedia" from the menu: "you may try to load the file even if the plugin wasn't made for it". Also `lcp_darkmode` (128) and `lcp_darkmodenative` (256). The brief ignores dark mode. HISTORY:09.01.20 and 15.01.20 show these are sent, and also via ListSendCommand on a mode switch (HISTORY:22.01.20).
6. **Focus.** The SDK says "When lister is activated, it will set the focus to your window", and "Lister will subclass your window to catch some hotkeys like 'n' or 'p'". The brief's rule "forward every other WM_KEYDOWN to the parent with PostMessage" may therefore be partly redundant, because Lister's subclass may catch N/P/Esc first [UNVERIFIED: interaction with a plugin that handles WM_KEYDOWN itself]. Test in TC. The brief does not mention `itm_focus` (see 4.4).
7. **Per-window state.** The SDK warns "multiple Lister windows can be open at the same time! Therefore you cannot save settings in global variables." The brief's GCHandle-in-GWLP_USERDATA design agrees. The SDK suggests `cbWndExtra` or an internal list. Note that Lister subclasses the plugin window, and the SDK suggests `SetWindowLong(hwnd, GWL_ID, ...)` (the SDK sample is 32-bit-era); GWLP_USERDATA is also used by the SetWindowSubclass family, so avoid interfering [UNVERIFIED]. Prefer cbWndExtra.
8. **ListLoadNext is not optional for our purpose.** See 3.1: omitting it makes TC unload and reload the plugin per file.
9. **ListGetDetectString maxlen** is "currently 2k" bytes; the brief does not state it.
10. **ListSetDefaultParams** is ANSI-only: `DefaultIniName` can lose characters if the profile path is non-ASCII [UNVERIFIED].
11. **The SDK sample header (listplug.h in listplugsample.zip) is 1.x** (no W functions, no lcp_darkmode). Use the CHM's listplug.h page as the authoritative header.

### 1.3 Detect string: how to make TC send ALL files

What the SDK says (SDK:overview, SDK:listgetdetectstring) [VERIFIED]:

- TC reads `[ListerPlugins]` in wincmd.ini. For each plugin it checks the key `N_detect`.
  - If present, it is evaluated as a parse function for the file.
  - If absent, TC loads the plugin and calls ListGetDetectString (if exported) and stores the result as `N_detect`.
- "If nr_detect isn't present, or parsing of nr_detect returns true, the ListLoad function is called."
- TC help:inisettings3 (`[ListerPlugins]`): `0_detect=<detect string>` is "Optional detect string... Allows TC to check whether the plugin supports a file without loading the plugin."
- Grammar: operands `EXT` (always uppercase), `SIZE`, `FORCE`, `MULTIMEDIA` (always TRUE; presence means the plugin overrides the internal multimedia viewers), `[n]` byte, number, "string". Operators `& | = != < >`, and `FIND()`, `FINDI()`, `!()`. There is **no wildcard**. `EXT="*"` would compare the extension to a literal asterisk and be false for normal files. Do not use it.

Ways to accept every file:

- **Leave the buffer empty / return an empty string.** The SDK does not state the behaviour for an empty detect string explicitly [UNVERIFIED]. Conceptually, "no detect string" means "call ListLoad for everything" (SDK:overview). It is not documented whether an empty `N_detect=` value is treated as absent, or as an expression evaluating to false or true. **Test this in TC** (put `N_detect=` and a real string in wincmd.ini and watch which files arrive).
- **Use a trivially true expression**, e.g. `SIZE>=0`. This is grammatically valid [VERIFIED grammar]. A [SECONDARY] forum snippet mentions `N_detect="SIZE>0"` as a workaround for plugins without detect strings. Caveat: `SIZE>0` excludes empty files, so use `>=0`. Whether SIZE is defined for directories or locked files is [UNVERIFIED]. `FORCE=0 | FORCE=1` is another always-true form.

Caveats:

- **The detect string is cached** in wincmd.ini as `N_detect` the first time TC sees the plugin. Changing what ListGetDetectString returns later has no effect until that key is deleted or edited (SDK:overview). Delete the key when testing different strings.
- **TC 11.58 fixed detect-string parsing beyond the buffer**: HISTORY:29.06.26 "Fixed: Lister plugins, detect string: Make sure not to read beyond the length of the buffer (max 8192 bytes) (32/64)" [VERIFIED]. Content checks (`[n]`, FIND, FINDI) only look at the first 8192 bytes. For files shorter than the index, older TC builds could read stale buffer data. Do not depend on `[n]` or FIND for anything on files under 8 KB.
- **A match-all plugin is called for every file**: F3 on any file, and every file the Quick View panel visits. The SDK advises "make sure that your plugin doesn't have any static objects defined as global variables" and warns about load cost when detect is loose (SDK:overview). For us this means every F3 loads the AOT DLL and starts the runtime, so startup cost lands on every F3 even for `.txt`.
- **Plugin order matters.** The Lister plugin dialog order decides who gets the file first, and '4' cycles through matching plugins (TC help:dlg_listerconfig). Whether plugins pre-empt the built-in text, hex and image viewers on plain F3 is determined by the "Use plugins" option in the Lister Multimedia tab and "Define view method by file type" [partially VERIFIED: TC help:dlg_listerconfig; exact precedence UNVERIFIED].
- `MULTIMEDIA` in the detect string is only needed to override TC's internal media player. Do not add it.
- `[ListerPlugins64]` in wincmd.ini is a separate index of plugins that have a 64-bit version. TC derives it from `[ListerPlugins]`, with a checksum, to detect added or removed plugins (TC help:inisettings3 [VERIFIED]).

---

## 2. pluginst.inf for a 64-bit-only Lister plugin

### What is verified

- The TC help (TOTALCMD.CHM, decompiled) does **not** document pluginst.inf. A grep across the decompiled help found no match. TC help:plugins only says "Newer plugins can be installed automatically just by double clicking the plugin ZIP file."
- Format source: TotalcmdWiki https://www.ghisler.ch/wiki/index.php?title=Pluginst.inf [SECONDARY: summary of the page, which is the canonical description]. It is a simple INI file with a single section `[plugininstall]`; keys are case-insensitive:
  - `description` (required), `descriptionXXX` (optional, language code)
  - `version` (optional)
  - `type` (required): `wcx`, `wfx`, `wlx`, `wdx`, `lng`, `prg`, `key`
  - `file` (required): the file in the archive to register
  - `defaultdir` (required for plugins): suggested subfolder
  - `defaultextension` (required for wcx only)
- HISTORY entries confirming specifics [VERIFIED]:
  - 22.10.13: "pluginst.inf now supports version= field, shown on a separate line in the auto-install dialog".
  - 03.06.21: if the language uses UTF-8, the description is checked for UTF-8, otherwise the current codepage is used.
  - 17.01.12: warning when installing a 32-bit plugin in TC64.
  - 26.06.12: "Find old plugin location also when installing 64bit or combined plugin while only 32-bit plugin is installed (64)".
  - 15.03.21: "Crash installing plugins having a zone identifier from an untrusted domain (64)".
  - 19.02.21: zone data is not copied when the plugin comes from a known trusted site.
  - 02.12.19: auto-install works when the archive was opened via an internal association.
- The SDK says 64-bit-only plugin files "must also end with '64'" (SDK:64_bit_support).

### What is NOT verified

- Whether `file=` for a 64-bit-only plugin must be `Viewer.wlx64` or `Viewer.wlx` [UNVERIFIED]. The wiki summary has no 64-bit key (no `file64`). By the SDK rule the registered name ends in `64`, and HISTORY:23.12.11 shows TC stores `pluginname.wlx64` in wincmd.ini for 64-bit-only plugins ("Starting TC with /S=L:Ppluginname didn't find the plugin if its name was stored as pluginname.wlx64 instead of pluginname.wlx"). `file=Viewer.wlx64` is therefore the expected form. **Confirm by installing the zip in TC 11.58** (first manual check). Fallback if TC rejects it: ship `file=Viewer.wlx` plus both `Viewer.wlx` (a copy or stub) and `Viewer.wlx64`.
- 32-bit TC would also offer this zip, then fail to load it. Out of scope.

### Working example (UNTESTED)

Zip root must contain these two files directly (not in a subfolder): `pluginst.inf` and `Viewer.wlx64`. Encoding: UTF-8 without BOM or ASCII (keep it ASCII to avoid codepage issues).

```ini
[plugininstall]
description=Fast hex viewer (Native AOT spike, 64-bit only)
version=0.1
type=wlx
file=Viewer.wlx64
defaultdir=HexViewerSpike
```

After install TC writes the plugin into `[ListerPlugins]` and `[ListerPlugins64]` of wincmd.ini. It may ask where to put the file; `defaultdir` is only a suggestion, nested under the user's plugin base directory (a "2 level" suggestion such as `plugins\wlx\<defaultdir>`) [SECONDARY, hexacorn summary https://www.hexacorn.com/blog/?p=6277].

---

## 3. THE KEY QUESTION: does TC unload (FreeLibrary) Lister plugins?

### 3.1 Evidence that TC does unload Lister plugins in some situations

1. **SDK:listloadnext** [VERIFIED, primary]: "If you do not implement LIstLoadNext but only ListLoad, then the plugin will be unloaded and loaded again when switching through files, which results in flickering." This is Total Commander's own SDK saying the DLL is released between files when ListLoadNext is missing. It means TC has a path that frees the Lister plugin DLL (FreeLibrary) without a session end. We must export ListLoadNextW/ListLoadNext. The brief already does. A missing, mis-named or failing export would trigger unload/reload, which is dangerous for a NativeAOT DLL. If ListLoadNext returns LISTPLUGIN_ERROR, TC falls back to ListLoad, and the "no plugin window left" state could also trigger an unload [UNVERIFIED]. Cover this case in manual testing (use a locked file mid-cycle in Quick View).
2. **cm_UnloadPlugins exists and covers Lister plugins.** TC help:dlg_choosecommand [VERIFIED]: `cm_UnloadPlugins 0` unloads all plugin types, or a sum of `1`=packer, `2`=file system, `4`=lister, `8`=content, `16`=external tcmatch dll. HISTORY:08.03.22 added the parameters. The user can bind it to a button or hotkey. Another plugin or tool can send the command. Calling it while Quick View is active is reported to be dangerous (thread "(False Positiv) Calling cm_UnloadPlugins while using QuickView seems to kill TC", https://ghisler.ch/board/viewtopic.php?t=78800 [SECONDARY, title only]).
3. **Forum thread "cm_UnloadPlugins"** https://ghisler.ch/board/viewtopic.php?t=596 (and `&start=15`) [SECONDARY, search-engine summaries]:
   - Lister plugins are only unloaded "after all Lister windows are closed (or not showing any lister plugins)". This is about the explicit command, so the command only unloads Lister plugins when no Lister window or Quick View is displaying a plugin.
   - A VC++ plugin linked with `/DELAY:UNLOAD` is unloaded by cm_UnloadPlugins; without it, TC does not unload the DLL (this is the mechanism for the DLL being a delay-loaded dependency [UNVERIFIED]. Needs reading).
   - Plugins reported as not unloading even then: office.wlx, listdoc.wlx, ieview.wlx, fileinfo.wlx, eventlog.wlx.
   - Warning that if a plugin is busy in a background thread, the command "may crash... use this command only for plugin development".
   - A claim that TC "doesn't unload the DLLs when Lister is closed" (so DLLs remain loaded after Lister closes). This is the most relevant claim for the spike and **it has not been verified from the primary text**. Read the thread.
4. **Plugins are unloaded at shutdown.** HISTORY:01.12.09 "Dialog boxes in plugin unload functions were shown but not clickable... WM_NCACTIVATE wasn't handled on shutdown to avoid crash of plugin StartupGuard" and HISTORY:12.08.09 "Crash on exit after using Startup Guard plugin" [VERIFIED text, inference]. TC does call plugin unload code on exit. A NativeAOT DLL that is FreeLibrary'd at exit may crash or hang. The brief's last test step, "Exit TC: closes at normal speed with no crash" (manual check 12), covers this.
5. **No unload on idle found.** Nothing in HISTORY.TXT (grep for "unload", "FreeLibrary", "lister plugin", "wlx") or TC help describes idle-timeout unloading of Lister plugins. The only unload-related entries are: packer plugins reloaded after `ForgetPassword` (HISTORY:02.06.26 and 03.06.26: "unload/reload plugin on minimize" for header-encrypted archives, **packer plugins only**), `vmr9rotator.dll` (HISTORY:28.04.26, a TC-internal helper DLL), tcmatch (HISTORY:27.09.22 and 01.06.09), and HISTORY:30.07.20 "Prevent double loading of Lister plugins when user pressed '4' in Lister". Absence of evidence is not proof.
6. **Config options in wincmd.ini.** Searched the decompiled help (inisettings, inisettings1/2/3) for "unload", "free", "keep", "memory", "timeout". **No option affecting Lister plugin unloading was found** [VERIFIED for this help version]. Note `KeepPackerPassword` is packer-only. The `[ListerPlugins]` keys documented are `N=path` and `N_detect=` only. The forum snippet about "registry settings" in the `/DELAY:UNLOAD` thread [SECONDARY] should be read to see whether any such setting exists.

### 3.2 Net assessment

- TC can and does FreeLibrary Lister plugins in at least these cases: (a) per-file switch when the plugin lacks ListLoadNext (documented by the SDK), (b) cm_UnloadPlugins 0 or 4 when no Lister window shows a plugin, (c) process exit [inferred]. Whether TC unloads automatically when the last Lister or Quick View window closes (with the plugin otherwise healthy) is **unproven both ways**: the SDK sentence in (a) hints that unload/reload tracks "plugin has no ListLoadNext"; the forum summary says DLLs are not unloaded on Lister close.
- Because the .NET docs say unloading NativeAOT libraries is unsupported (4.1), the "decisive finding" in the brief stays valid. Plan for the stub-DLL mitigation as a fallback (4.5).

### 3.3 What must be observed manually (cannot be known from docs)

1. Process Explorer / Process Hacker (or `tasklist /m Viewer.wlx64`) on `TOTALCMD64.EXE`: is the module still listed (a) while Lister is open, (b) after the last Lister and Quick View close, (c) after 5 min idle, (d) after switching Quick View through 200 files, (e) after Quick View off then on.
2. Add a `DLL_PROCESS_DETACH` probe: a tiny native "stub" or a `LdrRegisterDllNotification`-style probe is overkill. Simpler: the plugin logs every export entry with a monotonically increasing process-wide counter initialised in static state. If the counter resets in the log (log shows `ListSetDefaultParams` again, with state back to 0), the DLL was unloaded and reloaded. Also log `ListSetDefaultParams` calls: SDK says it is called "immediately after loading the DLL", so a second call in one session means a reload.
3. Quick View cycle with a missing or failing `ListLoadNext` (return 1 for a locked file) and see whether the DLL reloads afterwards.
4. cm_UnloadPlugins with param 4 (and 0) with Lister closed, and with Quick View open: what happens to a NativeAOT DLL (crash, hang, silent no-op).
5. Exit TC with the plugin loaded: normal exit speed and no crash dialog.
6. Reading the cm_UnloadPlugins thread (t=596) in a browser to settle item 3.1.3.

---

## 4. Pitfalls of hosting a NativeAOT DLL in a native host

### 4.1 Unloading unsupported [VERIFIED]

- Microsoft Learn, "Building native libraries" https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/libraries (page dated 2024-04-17): "Only 'shared libraries' (also known as DLLs on Windows) are supported. Static libraries are not officially supported... Unloading Native AOT libraries (via `dlclose` or `FreeLibrary`, for example) is not supported." Exports are methods marked `[UnmanagedCallersOnly]` with a non-null `EntryPoint`.
- The docs do not say what happens on an unsupported unload (crash, leak or hang): [UNVERIFIED]. NativeAOT runtime threads (finalizer, and possibly GC background threads) running code in a module that is unmapped would be the likely cause of a crash [UNVERIFIED inference]. Measure with the harness (brief step 8).
- The dotnet/runtime discussion https://github.com/dotnet/runtime/discussions/109416 lists "unsupported static libraries/library unloading" as a known limitation [VERIFIED via fetch summary].
- `NativeLib=Shared` produces the DLL (property used by the brief). The Learn page does not document the property in this version of the text [as fetched].

### 4.2 Multiple NativeAOT DLLs in one process

- A forum or discussion summary says it is "possible, viable, and stable" (https://github.com/dotnet/runtime/discussions/102048 "AOT-compiled DLLs: how to think about resource utilization" [SECONDARY, not read in full]). Discussion 109416 says a collaborator confirmed the NativeAOT dll "doesn't have most of CLR components" and "only uses the GC heap", and notes no documented problems for multiple libraries [VERIFIED via summary].
- Each NativeAOT DLL carries its own runtime and GC heap [consistent with the above; UNVERIFIED in detail]. That costs memory per DLL, and objects cannot be shared across DLLs. Relevant if another .NET AOT TC plugin is installed: both would load a separate runtime. Process-wide named handles or global state could collide only if the runtime uses global names (cited concern was about multi-CLR hosting [SECONDARY]).
- A related caveat: `AppContext.BaseDirectory` and `DllImportSearchPath.AssemblyDirectory` behave unintuitively in the SharedLibrary scenario (https://github.com/dotnet/runtime/issues/112290 [SECONDARY title only]). For a TC plugin this means the base directory may be the host exe (TC) directory, not the plugin directory. Use `GetModuleFileNameW` on the plugin's own HMODULE (`GetModuleHandleExW` with `FROM_ADDRESS`) if you need the plugin directory [UNVERIFIED suggestion].

### 4.3 DllMain / loader lock

- https://github.com/dotnet/runtime/issues/116787 (fetch summary): "NativeAOT doesn't support runtime initialization under loader lock", and the current implementation has a "no-op DllMain". The runtime is therefore initialised on the first call into an export, not inside DllMain [inference from the issue text; UNVERIFIED which call exactly]. For us: runtime start-up cost lands on the first export TC calls, which is **`ListSetDefaultParams`** (called "immediately after loading the DLL", SDK) or `ListGetDetectString` (called when `N_detect` is absent). This is on TC's UI thread, not under loader lock, because TC calls it after LoadLibrary returns. The harness's "first file shown" timing must include `ListSetDefaultParams` if it is called before ListLoadW, as TC does.
- https://github.com/dotnet/runtime/issues/109543: creating a managed thread from DllMain hangs [SECONDARY]. The plugin has no DllMain and no threads (brief rule), so not applicable.
- Do not call any export from another export's DllMain-equivalent, and never call managed code from `DllMain` of a native stub that hosts the AOT DLL (see 4.5).
- https://github.com/dotnet/runtime/issues/121345 ".NET application hangs some time after loading NativeAOT-compiled library" and https://github.com/dotnet/runtime/issues/89346 "EventPipe hangs with a shared NativeAOT library" [SECONDARY, titles only]: not obviously relevant to a native host, but note them if hangs appear. Consider `DOTNET_`-style env switches only if needed [UNVERIFIED].
- Plugin structured exceptions: HISTORY:16.11.16 "Catch floating point exceptions also when loading Lister plugins" shows TC wraps plugin calls in some exception handling; an unhandled managed exception inside an `UnmanagedCallersOnly` method fails fast and kills the process. The brief's catch-all rule stands.

### 4.4 Quick View focus and window behaviour (TC 11.57 and later)

All of this is from HISTORY.TXT unless stated [VERIFIED]:

- 07.04.26: "Quick view panel: Lister plugins can now notify Total Commander when they gain focus: WM_COMMAND with high word of WPARAM set to itm_focus (WPARAM=0xFFF80000), and LPARAM set to the plugin window as returned to ListLoad call". Documented in SDK:wm_command: `itm_focus` "New in 2.13 (TC 11.57): A plugin can send this to Total Commander to inform it that it gained the focus. Only necessary if clicking on the Quick View panel does NOT update the focus header above the file list and Quick View panel."
  - Mechanism per SDK:wm_command: `PostMessage(GetParent(ListWin), WM_COMMAND, MAKELONG(value, itemtype), (LPARAM)ListWin)`. For `itm_focus` the value is 0 [UNVERIFIED; HISTORY says WPARAM=0xFFF80000, i.e. value 0].
  - Note HISTORY:21.04.26: "Sending WM_COMMAND to Lister window with itm_focus option didn't work, and was described incorrectly in the history". Behaviour changed between 11.57 builds, so test on 11.58.
- 07.04.26: "plugin window wasn't set as a child window of the quick view panel" for plugins like TC 1by1 (fixed). The plugin window must be a child of `ParentWin` (SDK:ListLoad).
- 23.03.26: "Clicking on a Lister plugin didn't mark the panel as active via header". 27.03.26: same for SLister (64-bit). Clicking inside a plugin in Quick View may not update the panel's focus colour unless `itm_focus` is sent.
- 29.03.26 and 20.04.26 and 10.04.26: focus return to the file panel or Quick View after dialogs/commands. 25.03.26: "User-defined hotkeys with Alt didn't work when Quick View had the focus (still not working when a plugin has the focus)". So forwarding WM_SYSKEYDOWN to the parent, as in the brief, is the right thing, but Alt hotkeys while the plugin has focus may still not work (TC limitation).
- 23.03.26: sending WM_USER+50 to TC with WPARAM 3 or 4 returns the handle of the Lister plugin in Quick View if active: a useful way for a harness-level check [VERIFIED text].
- 06.07.21: Quick View shows the used plugin name in the panel title.
- 16.04.26 and 24.04.26: Imagine plugin deleting a viewed file posts a close message to Lister; changes to the Quick View title via `[name]` in the title.
- SDK:ListLoad: "When lister is activated, it will set the focus to your window. If your window contains child windows, then make sure that you set the focus to the correct child". Use a single window with no child controls (the brief's design) to avoid this.
- `lcp_forceshow`: set when "the user chose 'Image/Multimedia' from the menu" (SDK:ListLoad). It is not a Quick View flag. The brief's "Quick View parent hwnd / lcp_forceshow behaviour" question: nothing in the SDK ties lcp_forceshow to Quick View [VERIFIED absence]. In Quick View the parent is the Quick View panel window, and ListLoadNext is used when the cursor moves (SDK:ListLoadNext).
- `ListSendCommand` is also used for dark-mode switching in Quick View (HISTORY:26.01.20), so export it as a no-op (returns LISTPLUGIN_ERROR or OK; the SDK return is unverified) if dark mode matters later. Not required for the spike.

### 4.5 Mitigation if TC unloads the plugin: native stub

Architecture (idea only, matches the brief's own suggestion): a tiny native `Viewer.wlx64` stub exports the Lister functions, calls `LoadLibraryW` on the real AOT DLL once (`Viewer.Core.dll`) and never calls `FreeLibrary`; forwards each export via `GetProcAddress`. TC can FreeLibrary the stub safely and the AOT DLL stays resident. On reload, the stub's `LoadLibraryW` returns the existing HMODULE (refcount only increases), and the AOT runtime is not re-initialised [UNVERIFIED but standard Windows semantics]. Danger: if the stub also keeps the window class registered, `UnregisterClass` is needed on unload or the class goes stale, since window procs live in the AOT DLL (that is fine because the AOT DLL stays loaded). Pin the AOT DLL by calling `GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN, ...)` as a belt-and-braces measure (documented Win32 behaviour: pinned modules cannot be unloaded) [UNVERIFIED for this use; the flag exists in the Win32 API docs].

Cheaper alternative worth trying first: from inside the AOT DLL itself, call `GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)&someExport, &h)` in `ListSetDefaultParams`. This pins the DLL so FreeLibrary becomes a no-op without needing a stub [UNVERIFIED but likely: documented Win32 flag semantics; test it]. If it works, the unload question is moot, apart from shutdown noise. Note the SDK says TC may call `ListLoad` again after reload; pinning keeps the runtime state consistent with TC's expectation of fresh globals only if nothing depends on `DLL_PROCESS_ATTACH` re-running [UNVERIFIED]. Add this to the spike as a cheap experiment, alongside observing whether pinning changes TC's behaviour.

### 4.6 Existing C#/NativeAOT Total Commander plugins (proof of viability)

- **tc_docker** by Maksim Zimin (maximiliysiss), https://github.com/maximiliysiss/tc_docker: WFX plugins (`docker.wfx`, `podman.wfx`, `k8s.wfx`) in .NET. The author's article https://dev.to/maximiliysiss/c-calls-c-a-tale-of-friendship-across-runtimes-2513 (2024-11-21) says "In my case, while developing a plugin for Total Commander, I chose the AOT (Ahead-of-Time Compilation) method", with no external dependencies; notes AOT limits (no Assembly.Load or Reflection, bigger binary). The repo page does not state the build in detail; confirm NativeAOT from the source `Directory.Build.props` [SECONDARY]. It is a **WFX (file system)** plugin, not a Lister plugin, but it proves that a NativeAOT DLL loads in TC through LoadLibrary and handles exports. It says nothing about unloading [VERIFIED absence in the article].
- **TcBuild** https://github.com/r-larch/tcbuild (builds .NET plugins for wfx, wdx, wlx, wcx; a stub-based hosting approach, not NativeAOT): README says "AppDomain Isolation: Not now". No statement on unloading or NativeAOT [VERIFIED via fetch]. Another related project: https://github.com/ficnar/TcBuild (fork/original) [not read]. Oleg Yuvashev's "TC .NET Interface"/"tcPluginInterface" were not found with these searches, nothing read.
- No NativeAOT **Lister** plugin was found [UNVERIFIED: search coverage limited]. So Lister-specific problems (window class, focus, Quick View) have no precedent to lean on.
- Context from other plugin authors [SECONDARY]: "Plugin DLL dependencies" https://ghisler.ch/board/viewtopic.php?t=13791 and "Writing Java Plugins for Total Commander" https://ghisler.ch/board/viewtopic.php?p=82435 describe runtime-hosting plugins; not read.

---

## 5. Short list of risks surfaced for the spike

1. Implement `ListLoadNextW` correctly and never return 1 for ordinary files; otherwise TC unloads or reloads or falls back (SDK:listloadnext).
2. Pin the DLL (`GetModuleHandleExW` PIN) in `ListSetDefaultParams`, and log each `ListSetDefaultParams` call to detect reloads.
3. Runtime init happens on the first export call, so measure `ListSetDefaultParams` as part of "first file shown".
4. Quick View: send `itm_focus` on `WM_SETFOCUS` or mouse click (TC 11.57+); parent window is the Quick View panel; plugin window must be a direct child of `ParentWin`.
5. Match-all detect string causes the AOT DLL to load on every F3; choose and test the detect string (empty vs `SIZE>=0`) and delete cached `N_detect` between experiments.
6. pluginst.inf for 64-bit-only: `file=Viewer.wlx64` is expected but unverified; test first.
