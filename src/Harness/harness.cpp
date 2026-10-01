// Harness.exe - native test harness for the Total Commander Lister plugin spike.
//
// Behaves like Total Commander: a native Win32 process with no .NET runtime of its own,
// a top-level parent window and a message loop. Every measurement phase runs in a child
// process (the same exe started with --child <phase>), so a plugin crash or hang is recorded
// as a finding instead of killing the harness.
//
// Usage (from the repo root):
//   Harness.exe [--dll out\AdvancedViewer.wlx64] [--files testfiles] [--throwdll out\throwtest\AdvancedViewer.wlx64]
//               [--json out\harness-results.json] [--launches 20] [--switches 1000]
//               [--pagedowns 10000] [--thumbs 1000] [--thumbmsg track|position]
//               [--phases loadtime,perfile,cycle,scroll,edge,throw,unload]
//
// Child protocol (stdout, one line each, ASCII):
//   @@MARK <text>      progress marker (the last one tells where a crash/hang happened)
//   @@SUM <text>       human-readable summary line
//   @@CHECK <name>|<PASS|FAIL|INFO>|<measured>|<threshold>
//   @@JSON <object>    machine-readable result of the phase

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#include <shellapi.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <map>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")

// ---------------------------------------------------------------------------------------------
// Lister plugin ABI (listplug.h, x64)
// ---------------------------------------------------------------------------------------------
typedef struct {
    int size;
    DWORD PluginInterfaceVersionLow;
    DWORD PluginInterfaceVersionHi;
    char DefaultIniName[MAX_PATH];
} ListDefaultParamStruct;

typedef HWND(__stdcall* PListLoadW)(HWND, WCHAR*, int);
typedef HWND(__stdcall* PListLoad)(HWND, char*, int);
typedef int(__stdcall* PListLoadNextW)(HWND, HWND, WCHAR*, int);
typedef int(__stdcall* PListLoadNext)(HWND, HWND, char*, int);
typedef void(__stdcall* PListCloseWindow)(HWND);
typedef void(__stdcall* PListGetDetectString)(char*, int);
typedef void(__stdcall* PListSetDefaultParams)(ListDefaultParamStruct*);

static const int LISTPLUGIN_OK = 0;
static const int LISTPLUGIN_ERROR = 1;
static const int SHOW_FLAGS = 0;  // plain hex view; no lcp_* flags needed by the spike

// ---------------------------------------------------------------------------------------------
// Utilities
// ---------------------------------------------------------------------------------------------
static double g_qpf = 0;
static int64_t qpc() { LARGE_INTEGER l; QueryPerformanceCounter(&l); return l.QuadPart; }
static double usSince(int64_t a, int64_t b) { return (double)(b - a) * 1e6 / g_qpf; }

static std::string fmt(const char* f, ...) {
    char buf[4096];
    va_list ap; va_start(ap, f);
    vsnprintf(buf, sizeof buf, f, ap);
    va_end(ap);
    return buf;
}

static std::string narrow(const std::wstring& w) {  // UTF-8, for console display only
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}
static std::wstring widen(const std::string& s, UINT cp = CP_UTF8) {
    if (s.empty()) return {};
    int n = MultiByteToWideChar(cp, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(cp, 0, s.c_str(), (int)s.size(), &w[0], n);
    return w;
}

// ---- tiny JSON builder (ASCII output, non-ASCII escaped as \uXXXX) ---------------------------
static std::string jstr(const std::wstring& w) {
    std::string o = "\"";
    for (wchar_t c : w) {
        if (c == L'"') o += "\\\"";
        else if (c == L'\\') o += "\\\\";
        else if (c < 0x20 || c > 0x7E) o += fmt("\\u%04x", (unsigned)c);
        else o += (char)c;
    }
    return o + "\"";
}
static std::string jstr(const std::string& s) { return jstr(widen(s)); }
static std::string jstr(const char* s) { return jstr(std::string(s)); }
static std::string jnum(double d) { return std::isfinite(d) ? fmt("%.3f", d) : "null"; }
static std::string jint(long long v) { return fmt("%lld", v); }
static std::string jbool(bool b) { return b ? "true" : "false"; }
struct JObj {
    std::string body;
    JObj& add(const char* k, const std::string& raw) {
        if (!body.empty()) body += ",";
        body += "\""; body += k; body += "\":"; body += raw;
        return *this;
    }
    std::string str() const { return "{" + body + "}"; }
};
static std::string jarr(const std::vector<std::string>& items) {
    std::string o = "[";
    for (size_t i = 0; i < items.size(); i++) { if (i) o += ","; o += items[i]; }
    return o + "]";
}

// ---- statistics (input in microseconds, output in milliseconds) -------------------------------
struct Stats { size_t n = 0; double min = NAN, avg = NAN, median = NAN, p99 = NAN, max = NAN; };
static Stats stats(std::vector<double> v) {
    Stats s; s.n = v.size();
    if (v.empty()) return s;
    std::sort(v.begin(), v.end());
    double sum = 0; for (double x : v) sum += x;
    s.min = v.front() / 1000; s.max = v.back() / 1000; s.avg = sum / v.size() / 1000;
    s.median = (v.size() % 2 ? v[v.size() / 2] : (v[v.size() / 2 - 1] + v[v.size() / 2]) / 2) / 1000;
    size_t i99 = (size_t)std::ceil(0.99 * v.size()); if (i99 > 0) i99--; s.p99 = v[std::min(i99, v.size() - 1)] / 1000;
    return s;
}
static std::string jstats(const Stats& s) {
    return JObj().add("n", jint((long long)s.n)).add("minMs", jnum(s.min)).add("avgMs", jnum(s.avg))
        .add("medianMs", jnum(s.median)).add("p99Ms", jnum(s.p99)).add("maxMs", jnum(s.max)).str();
}
static std::string sstats(const Stats& s) {
    return fmt("n=%zu median=%.2f ms avg=%.2f ms p99=%.2f ms max=%.2f ms", s.n, s.median, s.avg, s.p99, s.max);
}

// ---- child protocol output -----------------------------------------------------------------
static void emit(const std::string& tag, const std::string& text) {
    std::string line = tag + " " + text + "\n";
    fwrite(line.data(), 1, line.size(), stdout);
    fflush(stdout);
}
static void MARK(const std::string& t) { emit("@@MARK", t); }
static void SUM(const std::string& t) { emit("@@SUM", t); }
static std::vector<std::string> g_checks;  // JSON of checks emitted by this child
static void CHECK(const std::string& name, const char* verdict, const std::string& measured, const std::string& threshold) {
    emit("@@CHECK", name + "|" + verdict + "|" + measured + "|" + threshold);
}

// ---------------------------------------------------------------------------------------------
// Options
// ---------------------------------------------------------------------------------------------
struct Options {
    std::wstring dll = L"out\\AdvancedViewer.wlx64";
    std::wstring files = L"testfiles";
    std::wstring throwDll = L"out\\throwtest\\AdvancedViewer.wlx64";
    std::wstring json = L"out\\harness-results.json";
    std::wstring phases = L"loadtime,perfile,cycle,scroll,edge,throw,unload";
    std::wstring child;       // phase name when running as child
    std::wstring thumbMsg = L"track";
    int launches = 20, switches = 1000, pageDowns = 10000, thumbs = 1000, perFileReps = 10, warmup = 50;
};
static std::wstring fullPath(const std::wstring& p) {
    wchar_t buf[32768];
    DWORD n = GetFullPathNameW(p.c_str(), 32768, buf, nullptr);
    return n ? std::wstring(buf, n) : p;
}
static bool parseArgs(Options& o) {
    int argc; LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    for (int i = 1; i < argc; i++) {
        std::wstring a = argv[i];
        auto next = [&]() -> std::wstring { return i + 1 < argc ? argv[++i] : L""; };
        if (a == L"--dll") o.dll = next();
        else if (a == L"--files") o.files = next();
        else if (a == L"--throwdll") o.throwDll = next();
        else if (a == L"--json") o.json = next();
        else if (a == L"--phases") o.phases = next();
        else if (a == L"--child") o.child = next();
        else if (a == L"--thumbmsg") o.thumbMsg = next();
        else if (a == L"--launches") o.launches = _wtoi(next().c_str());
        else if (a == L"--switches") o.switches = _wtoi(next().c_str());
        else if (a == L"--pagedowns") o.pageDowns = _wtoi(next().c_str());
        else if (a == L"--thumbs") o.thumbs = _wtoi(next().c_str());
        else if (a == L"--reps") o.perFileReps = _wtoi(next().c_str());
        else if (a == L"--warmup") o.warmup = _wtoi(next().c_str());
        else if (a == L"-h" || a == L"--help" || a == L"/?") {
            printf("Harness.exe [--dll path] [--files dir] [--throwdll path] [--json path] [--launches N]\n"
                   "            [--switches N] [--pagedowns N] [--thumbs N] [--reps N] [--warmup N]\n"
                   "            [--thumbmsg track|position] [--phases loadtime,perfile,cycle,scroll,edge,throw,unload]\n");
            return false;
        } else { fprintf(stderr, "unknown argument: %s\n", narrow(a).c_str()); return false; }
    }
    LocalFree(argv);
    o.dll = fullPath(o.dll); o.files = fullPath(o.files); o.throwDll = fullPath(o.throwDll); o.json = fullPath(o.json);
    return true;
}

// ---------------------------------------------------------------------------------------------
// Test file set
// ---------------------------------------------------------------------------------------------
struct TestFile { std::string name; std::wstring path; long long size = -1; };
static long long fileSize(const std::wstring& p) {
    WIN32_FILE_ATTRIBUTE_DATA d;
    std::wstring q = p.size() >= MAX_PATH && p.rfind(L"\\\\?\\", 0) != 0 ? L"\\\\?\\" + p : p;
    if (!GetFileAttributesExW(q.c_str(), GetFileExInfoStandard, &d)) return -1;
    return ((long long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
}
static std::wstring findFileRecursive(const std::wstring& dir, const std::wstring& name) {
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((L"\\\\?\\" + dir + L"\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return {};
    std::wstring found;
    do {
        std::wstring n = fd.cFileName;
        if (n == L"." || n == L"..") continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) { found = findFileRecursive(dir + L"\\" + n, name); }
        else if (_wcsicmp(n.c_str(), name.c_str()) == 0) found = dir + L"\\" + n;
    } while (found.empty() && FindNextFileW(h, &fd));
    FindClose(h);
    return found;
}
static const wchar_t* UNICODE_NAME = L"na\x00EF" L"ve \x2013 \x65E5\x672C\x8A9E.bin";  // naive (i-diaeresis), en dash, Japanese "nihongo"
// The core set used by perfile and cycle. __throw__.bin is excluded (it is only for the throw test).
static std::vector<TestFile> coreSet(const std::wstring& dir) {
    std::vector<TestFile> v;
    const char* names[] = {"empty.bin", "one.bin", "small.bin", "medium.bin", "huge.bin", "locked.bin", "denied.bin"};
    for (auto n : names) v.push_back({n, dir + L"\\" + widen(n)});
    v.push_back({"unicode", dir + L"\\" + UNICODE_NAME});
    v.push_back({"longpath", findFileRecursive(dir + L"\\longpath", L"long.bin")});
    for (auto& f : v) f.size = f.path.empty() ? -1 : fileSize(f.path);
    return v;
}
static std::vector<TestFile> mixedSet(const std::wstring& dir) {
    std::vector<TestFile> v;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((dir + L"\\mixed\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return v;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        TestFile t; t.name = "mixed\\" + narrow(fd.cFileName); t.path = dir + L"\\mixed\\" + fd.cFileName;
        t.size = ((long long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
        v.push_back(t);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    std::sort(v.begin(), v.end(), [](const TestFile& a, const TestFile& b) { return a.path < b.path; });
    return v;
}

// ---------------------------------------------------------------------------------------------
// Host window (stands in for Total Commander's Lister window)
// ---------------------------------------------------------------------------------------------
static int g_fwdKeyDown = 0, g_fwdChar = 0, g_fwdSysKey = 0;
static std::vector<WPARAM> g_fwdKeys;
static LRESULT CALLBACK ParentProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_KEYDOWN: g_fwdKeyDown++; g_fwdKeys.push_back(w); return 0;
    case WM_CHAR: g_fwdChar++; return 0;
    case WM_SYSKEYDOWN: g_fwdSysKey++; return 0;
    case WM_CLOSE: return 0;  // never let anything close the host during a test
    }
    return DefWindowProcW(h, m, w, l);
}
static HWND createHost() {
    WNDCLASSEXW wc = {sizeof wc};
    wc.lpfnWndProc = ParentProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = L"ListerHarnessHost";
    RegisterClassExW(&wc);
    HWND h = CreateWindowExW(0, wc.lpszClassName, L"Lister plugin harness", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
                             60, 60, 1000, 720, nullptr, nullptr, wc.hInstance, nullptr);
    ShowWindow(h, SW_SHOWNOACTIVATE);
    UpdateWindow(h);
    return h;
}
static void pump() {
    MSG m;
    while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&m); DispatchMessageW(&m); }
}
static void pumpFor(DWORD ms) {
    DWORD end = GetTickCount() + ms;
    while ((int)(end - GetTickCount()) > 0) { pump(); MsgWaitForMultipleObjects(0, nullptr, FALSE, 5, QS_ALLINPUT); }
    pump();
}

// ---------------------------------------------------------------------------------------------
// Plugin loading
// ---------------------------------------------------------------------------------------------
struct Plugin {
    HMODULE h = nullptr;
    double loadLibraryUs = 0;
    DWORD loadError = 0;
    PListLoadW LoadW = nullptr; PListLoad Load = nullptr;
    PListLoadNextW LoadNextW = nullptr; PListLoadNext LoadNext = nullptr;
    PListCloseWindow Close = nullptr; PListGetDetectString GetDetect = nullptr; PListSetDefaultParams SetParams = nullptr;
    std::vector<std::string> missing;
    std::string detect;
};
template <class T> static void resolve(Plugin& p, T& fn, const char* name) {
    fn = (T)GetProcAddress(p.h, name);
    if (!fn) p.missing.push_back(name);
}
static bool loadPlugin(const std::wstring& path, Plugin& p) {
    int64_t t0 = qpc();
    p.h = LoadLibraryW(path.c_str());
    int64_t t1 = qpc();
    p.loadError = GetLastError();
    p.loadLibraryUs = usSince(t0, t1);
    if (!p.h) return false;
    resolve(p, p.LoadW, "ListLoadW"); resolve(p, p.Load, "ListLoad");
    resolve(p, p.LoadNextW, "ListLoadNextW"); resolve(p, p.LoadNext, "ListLoadNext");
    resolve(p, p.Close, "ListCloseWindow"); resolve(p, p.GetDetect, "ListGetDetectString");
    resolve(p, p.SetParams, "ListSetDefaultParams");
    return p.missing.empty();
}
// Same order as Total Commander: ListSetDefaultParams right after load, then ListGetDetectString.
static void initPlugin(Plugin& p) {
    ListDefaultParamStruct d = {};
    d.size = sizeof d;
    d.PluginInterfaceVersionLow = 12;  // Lister interface 2.12
    d.PluginInterfaceVersionHi = 2;
    char tmp[MAX_PATH]; GetTempPathA(MAX_PATH, tmp);
    snprintf(d.DefaultIniName, MAX_PATH, "%slsplugin-harness.ini", tmp);
    p.SetParams(&d);
    char det[2048] = {};
    p.GetDetect(det, (int)sizeof det - 1);
    p.detect = det;
}
// Loads, resolves and initialises the plugin, or prints a loud error and exits.
static void loadOrDie(const std::wstring& path, Plugin& p) {
    MARK("LoadLibraryW");
    if (!loadPlugin(path, p)) {
        if (!p.h) {
            SUM(fmt("FATAL: LoadLibraryW(%s) failed, error %lu", narrow(path).c_str(), p.loadError));
            emit("@@JSON", JObj().add("fatal", jstr("LoadLibraryW failed")).add("error", jint(p.loadError)).str());
        } else {
            std::string m; for (auto& s : p.missing) m += s + " ";
            SUM("FATAL: missing exports: " + m);
            std::vector<std::string> mj; for (auto& s : p.missing) mj.push_back(jstr(s));
            emit("@@JSON", JObj().add("fatal", jstr("missing exports")).add("missing", jarr(mj)).str());
        }
        fflush(stdout);
        ExitProcess(3);
    }
    MARK("ListSetDefaultParams+ListGetDetectString");
    initPlugin(p);
}

// ---- timed calls ----------------------------------------------------------------------------
struct CallRes { HWND h = nullptr; int rc = -1; double us = 0; DWORD err = 0; bool invalidated = false; };
static std::vector<wchar_t> wbuf(const std::wstring& s) { std::vector<wchar_t> b(s.begin(), s.end()); b.push_back(0); return b; }
static std::vector<char> abuf(const std::string& s) { std::vector<char> b(s.begin(), s.end()); b.push_back(0); return b; }

// ListLoadW followed by UpdateWindow (delivers WM_PAINT synchronously).
static CallRes timedLoadW(Plugin& p, HWND host, const std::wstring& file) {
    auto b = wbuf(file);
    CallRes r;
    SetLastError(0);
    int64_t t0 = qpc();
    r.h = p.LoadW(host, b.data(), SHOW_FLAGS);
    r.err = GetLastError();
    if (r.h) { r.invalidated = GetUpdateRect(r.h, nullptr, FALSE) != 0; UpdateWindow(r.h); }
    r.us = usSince(t0, qpc());
    return r;
}
static CallRes timedLoadA(Plugin& p, HWND host, const std::string& file) {
    auto b = abuf(file);
    CallRes r;
    SetLastError(0);
    int64_t t0 = qpc();
    r.h = p.Load(host, b.data(), SHOW_FLAGS);
    r.err = GetLastError();
    if (r.h) { r.invalidated = GetUpdateRect(r.h, nullptr, FALSE) != 0; UpdateWindow(r.h); }
    r.us = usSince(t0, qpc());
    return r;
}
static CallRes timedNextW(Plugin& p, HWND host, HWND win, const std::wstring& file) {
    auto b = wbuf(file);
    CallRes r; r.h = win;
    SetLastError(0);
    int64_t t0 = qpc();
    r.rc = p.LoadNextW(host, win, b.data(), SHOW_FLAGS);
    r.err = GetLastError();
    r.invalidated = GetUpdateRect(win, nullptr, FALSE) != 0;
    UpdateWindow(win);
    r.us = usSince(t0, qpc());
    return r;
}
static CallRes timedNextA(Plugin& p, HWND host, HWND win, const std::string& file) {
    auto b = abuf(file);
    CallRes r; r.h = win;
    int64_t t0 = qpc();
    r.rc = p.LoadNext(host, win, b.data(), SHOW_FLAGS);
    r.err = GetLastError();
    UpdateWindow(win);
    r.us = usSince(t0, qpc());
    return r;
}
static void closeWin(Plugin& p, HWND h) { if (h) { p.Close(h); pump(); } }

struct Snapshot { long long privateBytes = 0; DWORD gdi = 0, user = 0; };
static Snapshot snap() {
    pump();
    Snapshot s;
    PROCESS_MEMORY_COUNTERS_EX pmc = {sizeof pmc};
    if (GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&pmc, sizeof pmc)) s.privateBytes = (long long)pmc.PrivateUsage;
    s.gdi = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    s.user = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    return s;
}
static std::string jsnap(const Snapshot& s) {
    return JObj().add("privateBytes", jint(s.privateBytes)).add("gdiObjects", jint(s.gdi)).add("userObjects", jint(s.user)).str();
}

// ---------------------------------------------------------------------------------------------
// Child phases
// ---------------------------------------------------------------------------------------------

// Step 2 (+ first part of step 4): one fresh-process launch.
static int phaseLoadtime(const Options& o) {
    HWND host = createHost();
    pump();
    std::wstring small = o.files + L"\\small.bin";
    Plugin p;
    int64_t t0 = qpc();
    loadOrDie(o.dll, p);
    int64_t t1 = qpc();
    MARK("ListLoadW(small.bin)");
    CallRes r = timedLoadW(p, host, small);
    int64_t t2 = qpc();
    // time since the OS created this process, for context
    FILETIME c, e, k, u, now; GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u); GetSystemTimePreciseAsFileTime(&now);
    double sinceStartMs = (double)((((long long)now.dwHighDateTime << 32) | now.dwLowDateTime) - (((long long)c.dwHighDateTime << 32) | c.dwLowDateTime)) / 1e4;
    pump();
    closeWin(p, r.h);
    emit("@@JSON", JObj().add("loadLibraryMs", jnum(p.loadLibraryUs / 1000)).add("initMs", jnum(usSince(t0, t1) / 1000 - p.loadLibraryUs / 1000))
        .add("listLoadWPaintMs", jnum(r.us / 1000)).add("firstShownMs", jnum(usSince(t0, t2) / 1000))
        .add("processStartToShownMs", jnum(sinceStartMs)).add("windowReturned", jbool(r.h != nullptr))
        .add("detectString", jstr(p.detect)).str());
    return r.h ? 0 : 2;
}

// Step 4: ListLoadW + UpdateWindow per test file.
static int phasePerfile(const Options& o) {
    HWND host = createHost(); pump();
    Plugin p; loadOrDie(o.dll, p);
    auto set = coreSet(o.files);
    std::wstring small = o.files + L"\\small.bin";
    MARK("warm-up");
    CallRes first = timedLoadW(p, host, small); pump(); closeWin(p, first.h);
    for (int i = 0; i < 3; i++) { CallRes w = timedLoadW(p, host, small); pump(); closeWin(p, w.h); }
    std::map<std::string, std::vector<double>> times;
    std::map<std::string, int> okCount;
    std::map<std::string, bool> invalidated;
    for (int rep = 0; rep < o.perFileReps; rep++) {
        for (auto& f : set) {
            if (f.path.empty()) continue;
            MARK("ListLoadW(" + f.name + ")");
            CallRes r = timedLoadW(p, host, f.path);
            times[f.name].push_back(r.us);
            if (r.h) { okCount[f.name]++; invalidated[f.name] = r.invalidated; }
            pump();
            closeWin(p, r.h);
        }
    }
    std::vector<std::string> fj;
    SUM(fmt("first ListLoadW+paint in this process (small.bin, after LoadLibrary): %.2f ms", first.us / 1000));
    for (auto& f : set) {
        Stats s = stats(times[f.name]);
        fj.push_back(JObj().add("name", jstr(f.name)).add("path", jstr(f.path)).add("size", jint(f.size))
            .add("windowsReturned", jint(okCount[f.name])).add("paintPending", jbool(invalidated[f.name])).add("time", jstats(s)).str());
        SUM(fmt("  %-11s size=%-12lld ok=%d/%zu  median=%8.3f ms  max=%8.3f ms", f.name.c_str(), f.size, okCount[f.name], s.n, s.median, s.max));
    }
    Stats ss = stats(times["small.bin"]), hs = stats(times["huge.bin"]);
    double ratio = hs.median / ss.median;
    bool pass = okCount["huge.bin"] > 0 && okCount["small.bin"] > 0 && ratio <= 2.0;
    CHECK("Large file load (huge.bin vs small.bin median)", pass ? "PASS" : "FAIL",
          fmt("%.3f ms vs %.3f ms = %.2fx", hs.median, ss.median, ratio), "<= 2x");
    emit("@@JSON", JObj().add("firstLoadInProcessMs", jnum(first.us / 1000)).add("reps", jint(o.perFileReps))
        .add("files", jarr(fj)).add("hugeToSmallRatio", jnum(ratio)).str());
    return 0;
}

// Step 5: ListLoadNextW cycling, leak check.
static int phaseCycle(const Options& o) {
    HWND host = createHost(); pump();
    Plugin p; loadOrDie(o.dll, p);
    auto list = coreSet(o.files);
    list.erase(std::remove_if(list.begin(), list.end(), [](const TestFile& f) { return f.path.empty(); }), list.end());
    auto mixed = mixedSet(o.files);
    list.insert(list.end(), mixed.begin(), mixed.end());
    std::wstring small = o.files + L"\\small.bin";
    MARK("ListLoadW(small.bin)");
    Snapshot before = snap();
    CallRes r0 = timedLoadW(p, host, small);
    if (!r0.h) { SUM("FATAL: ListLoadW(small.bin) returned NULL"); emit("@@JSON", JObj().add("fatal", jstr("ListLoadW(small.bin) returned NULL")).str()); return 2; }
    HWND win = r0.h;
    Snapshot initial = snap();
    MARK("warm-up");
    size_t idx = 0;
    for (int i = 0; i < o.warmup; i++) { timedNextW(p, host, win, list[idx++ % list.size()].path); pump(); }
    timedNextW(p, host, win, small); pump();
    Snapshot start = snap();
    std::vector<double> all, ok, warm;
    std::map<std::string, int> errors;
    struct Slow { double us; std::string name; };
    std::vector<Slow> slow;
    int invalidatedCount = 0;
    MARK("cycle");
    for (int i = 0; i < o.switches; i++) {
        const TestFile& f = list[idx++ % list.size()];
        CallRes r = timedNextW(p, host, win, f.path);
        all.push_back(r.us);
        if ((size_t)(o.warmup + i) >= list.size()) warm.push_back(r.us);  // file already seen once (OS cache, AV scan)
        if (r.rc == LISTPLUGIN_OK) ok.push_back(r.us); else errors[f.name]++;
        if (r.invalidated) invalidatedCount++;
        slow.push_back({r.us, f.name});
        pump();
        if (!IsWindow(win)) { SUM(fmt("FATAL: plugin window destroyed after switch %d (%s)", i, f.name.c_str())); break; }
    }
    MARK("final ListLoadNextW(small.bin)");
    timedNextW(p, host, win, small);
    Snapshot end = snap();
    MARK("ListCloseWindow");
    closeWin(p, win);
    Snapshot afterClose = snap();

    Stats sa = stats(all), so = stats(ok), sw = stats(warm);
    std::sort(slow.begin(), slow.end(), [](const Slow& a, const Slow& b) { return a.us > b.us; });
    std::vector<std::string> slowJ, errJ;
    for (size_t i = 0; i < std::min<size_t>(10, slow.size()); i++)
        slowJ.push_back(JObj().add("file", jstr(slow[i].name)).add("ms", jnum(slow[i].us / 1000)).str());
    for (auto& e : errors) errJ.push_back(JObj().add("file", jstr(e.first)).add("count", jint(e.second)).str());
    long long privGrowth = end.privateBytes - start.privateBytes;
    int gdiDelta = (int)end.gdi - (int)start.gdi, userDelta = (int)end.user - (int)start.user;

    SUM(fmt("cycle list: %zu files (%zu core + %zu mixed), %d switches after %d warm-up switches", list.size(), list.size() - mixed.size(), mixed.size(), o.switches, o.warmup));
    SUM("ListLoadNextW+paint, all calls:  " + sstats(sa));
    SUM("ListLoadNextW+paint, OK calls:   " + sstats(so));
    SUM("ListLoadNextW+paint, files already opened once before (2nd+ pass): " + sstats(sw));
    std::string errS; for (auto& e : errors) errS += fmt("%s x%d  ", e.first.c_str(), e.second);
    SUM("calls returning LISTPLUGIN_ERROR: " + (errS.empty() ? std::string("none") : errS));
    SUM(fmt("calls with paint pending after return: %d/%zu", invalidatedCount, all.size()));
    for (size_t i = 0; i < std::min<size_t>(5, slow.size()); i++) SUM(fmt("  slow: %8.3f ms  %s", slow[i].us / 1000, slow[i].name.c_str()));
    SUM(fmt("private bytes: before load %lld, after first load %lld, start (after warm-up) %lld, end %lld, after close %lld (growth %+.2f MB)",
            before.privateBytes, initial.privateBytes, start.privateBytes, end.privateBytes, afterClose.privateBytes, privGrowth / 1048576.0));
    SUM(fmt("GDI objects: initial %lu, start %lu, end %lu, after close %lu | USER objects: initial %lu, start %lu, end %lu, after close %lu",
            initial.gdi, start.gdi, end.gdi, afterClose.gdi, initial.user, start.user, end.user, afterClose.user));

    CHECK("Next file shown (ListLoadNextW + paint), all calls", (sa.median <= 20 && sa.max <= 50) ? "PASS" : "FAIL",
          fmt("median %.2f ms, max %.2f ms, p99 %.2f ms", sa.median, sa.max, sa.p99), "median <= 20 ms, max <= 50 ms");
    CHECK("Leaks: private bytes over switches", privGrowth <= 5 * 1048576 ? "PASS" : "FAIL", fmt("%+.2f MB", privGrowth / 1048576.0), "<= 5 MB");
    CHECK("Leaks: GDI/USER objects over switches", (std::abs(gdiDelta) <= 2 && std::abs(userDelta) <= 2) ? "PASS" : "FAIL",
          fmt("GDI %+d, USER %+d", gdiDelta, userDelta), "start +-2");

    emit("@@JSON", JObj().add("listSize", jint((long long)list.size())).add("mixedFiles", jint((long long)mixed.size()))
        .add("switches", jint(o.switches)).add("warmup", jint(o.warmup))
        .add("allCalls", jstats(sa)).add("okCalls", jstats(so)).add("secondPassCalls", jstats(sw)).add("errors", jarr(errJ)).add("paintPendingCount", jint(invalidatedCount))
        .add("slowest", jarr(slowJ))
        .add("memory", JObj().add("beforeLoad", jsnap(before)).add("afterFirstLoad", jsnap(initial)).add("startAfterWarmup", jsnap(start))
                           .add("end", jsnap(end)).add("afterClose", jsnap(afterClose)).str())
        .add("privateBytesGrowth", jint(privGrowth)).add("gdiDelta", jint(gdiDelta)).add("userDelta", jint(userDelta)).str());
    return 0;
}

// Step 6: PageDown and thumb-drag repaint on the largest file.
static int phaseScroll(const Options& o) {
    HWND host = createHost(); pump();
    Plugin p; loadOrDie(o.dll, p);
    auto set = coreSet(o.files);
    TestFile big; for (auto& f : set) if (f.size > big.size) big = f;
    MARK("ListLoadW(" + big.name + ")");
    CallRes r = timedLoadW(p, host, big.path);
    if (!r.h) { SUM("FATAL: ListLoadW(largest) returned NULL"); emit("@@JSON", JObj().add("fatal", jstr("ListLoadW returned NULL")).str()); return 2; }
    HWND win = r.h;
    pump();
    SetFocus(win);
    SCROLLINFO si0 = {sizeof si0, SIF_ALL}; GetScrollInfo(win, SB_VERT, &si0);

    MARK("PageDown x" + std::to_string(o.pageDowns));
    std::vector<double> pd; int pdInval = 0;
    UINT scan = MapVirtualKeyW(VK_NEXT, MAPVK_VK_TO_VSC);
    LPARAM kl = 1 | ((LPARAM)scan << 16) | (1 << 24);  // repeat 1, scan code, extended key
    int fwdBefore = g_fwdKeyDown;
    for (int i = 0; i < o.pageDowns; i++) {
        int64_t t0 = qpc();
        SendMessageW(win, WM_KEYDOWN, VK_NEXT, kl);
        if (GetUpdateRect(win, nullptr, FALSE)) pdInval++;
        UpdateWindow(win);
        pd.push_back(usSince(t0, qpc()));
        pump();
    }
    SCROLLINFO si1 = {sizeof si1, SIF_ALL}; GetScrollInfo(win, SB_VERT, &si1);
    int pdForwarded = g_fwdKeyDown - fwdBefore;

    MARK("thumb drag x" + std::to_string(o.thumbs));
    bool usePosition = o.thumbMsg == L"position";
    WORD code = usePosition ? SB_THUMBPOSITION : SB_THUMBTRACK;
    SCROLLINFO si = {sizeof si, SIF_ALL}; GetScrollInfo(win, SB_VERT, &si);
    long long lo = si.nMin, hi = (long long)si.nMax - (si.nPage ? (long long)si.nPage - 1 : 0);
    if (hi < lo) hi = lo;
    std::vector<double> th; int thInval = 0, trackPosMatches = 0;
    uint64_t lcg = 0x9E3779B97F4A7C15ull;
    int half = o.thumbs / 2;
    for (int i = 0; i < o.thumbs; i++) {
        long long target;
        if (i < half) target = lo + (long long)((double)(hi - lo) * i / std::max(1, half - 1));  // forward sweep
        else { lcg = lcg * 6364136223846793005ull + 1442695040888963407ull; target = lo + (long long)((lcg >> 11) % (uint64_t)(hi - lo + 1)); }
        int64_t t0 = qpc();
        SCROLLINFO sp = {sizeof sp, SIF_POS}; sp.nPos = (int)target;
        SetScrollInfo(win, SB_VERT, &sp, FALSE);
        SCROLLINFO chk = {sizeof chk, SIF_POS | SIF_TRACKPOS}; GetScrollInfo(win, SB_VERT, &chk);
        if (chk.nTrackPos == (int)target) trackPosMatches++;
        SendMessageW(win, WM_VSCROLL, MAKEWPARAM(code, (WORD)(target & 0xFFFF)), 0);
        if (GetUpdateRect(win, nullptr, FALSE)) thInval++;
        UpdateWindow(win);
        th.push_back(usSince(t0, qpc()));
        pump();
    }
    if (!usePosition) {  // a real drag ends with SB_THUMBPOSITION then SB_ENDSCROLL
        SendMessageW(win, WM_VSCROLL, MAKEWPARAM(SB_THUMBPOSITION, 0), 0);
        SendMessageW(win, WM_VSCROLL, MAKEWPARAM(SB_ENDSCROLL, 0), 0);
        UpdateWindow(win);
    }
    pump();
    closeWin(p, win);

    Stats ps = stats(pd), ts = stats(th);
    SUM(fmt("file: %s (%lld bytes), scroll range %d..%d page %u", big.name.c_str(), big.size, si0.nMin, si0.nMax, si0.nPage));
    SUM("PageDown repaint:   " + sstats(ps) + fmt("  (paint pending %d/%zu, scroll pos %d -> %d, keys forwarded to host %d)", pdInval, pd.size(), si0.nPos, si1.nPos, pdForwarded));
    SUM("Thumb-drag repaint: " + sstats(ts) + fmt("  (msg %s, paint pending %d/%zu, nTrackPos==SetScrollInfo pos %d/%zu)",
                                                 usePosition ? "SB_THUMBPOSITION" : "SB_THUMBTRACK", thInval, th.size(), trackPosMatches, th.size()));
    auto over = [](const std::vector<double>& v) { return (int)std::count_if(v.begin(), v.end(), [](double x) { return x > 33000; }); };
    CHECK("Scroll repaint: PageDown", (ps.avg <= 16 && ps.max <= 33) ? "PASS" : "FAIL", fmt("avg %.2f ms, worst %.2f ms, p99 %.2f ms, %d of %zu over 33 ms", ps.avg, ps.max, ps.p99, over(pd), pd.size()), "avg <= 16 ms, worst <= 33 ms");
    CHECK("Scroll repaint: thumb drag", (ts.avg <= 16 && ts.max <= 33) ? "PASS" : "FAIL", fmt("avg %.2f ms, worst %.2f ms, p99 %.2f ms, %d of %zu over 33 ms", ts.avg, ts.max, ts.p99, over(th), th.size()), "avg <= 16 ms, worst <= 33 ms");
    if (pdInval == 0) CHECK("Scroll: PageDown caused repaints", "INFO", "no paint pending after any WM_KEYDOWN", "plugin should invalidate");
    emit("@@JSON", JObj().add("file", jstr(big.name)).add("size", jint(big.size))
        .add("scrollInfo", JObj().add("min", jint(si0.nMin)).add("max", jint(si0.nMax)).add("page", jint(si0.nPage)).str())
        .add("pageDown", JObj().add("time", jstats(ps)).add("paintPending", jint(pdInval)).add("posBefore", jint(si0.nPos)).add("posAfter", jint(si1.nPos)).add("forwardedToHost", jint(pdForwarded)).str())
        .add("thumb", JObj().add("time", jstats(ts)).add("message", jstr(usePosition ? "SB_THUMBPOSITION" : "SB_THUMBTRACK"))
                          .add("method", jstr("SetScrollInfo(SIF_POS, target) then SendMessage(WM_VSCROLL, MAKEWPARAM(code, target & 0xFFFF)) then UpdateWindow"))
                          .add("paintPending", jint(thInval)).add("trackPosEqualsPos", jint(trackPosMatches)).str()).str());
    return 0;
}

// Edge cases: locked, denied, empty, non-ASCII (W and ANSI), long path, key forwarding.
static int phaseEdge(const Options& o) {
    HWND host = createHost(); pump();
    Plugin p; loadOrDie(o.dll, p);
    std::vector<std::string> results;
    auto record = [&](const std::string& name, const std::string& api, const std::wstring& path, bool gotWindow, int rc, double us, DWORD err, const char* expect, bool ok, const std::string& note = "") {
        results.push_back(JObj().add("case", jstr(name)).add("api", jstr(api)).add("path", jstr(path)).add("pathLength", jint((long long)path.size()))
            .add("windowReturned", jbool(gotWindow)).add("rc", jint(rc)).add("ms", jnum(us / 1000)).add("lastError", jint(err))
            .add("expected", jstr(expect)).add("asExpected", jbool(ok)).add("note", jstr(note)).str());
        SUM(fmt("  %-34s %-13s -> %-9s %8.3f ms  expected %-9s %s %s", name.c_str(), api.c_str(),
                api.find("Next") != std::string::npos ? (rc == 0 ? "OK(0)" : rc == 1 ? "ERROR(1)" : fmt("rc=%d", rc).c_str()) : (gotWindow ? "HWND" : "NULL"),
                us / 1000, expect, ok ? "ok" : "UNEXPECTED", note.c_str()));
    };
    // expect: 1 = window, 0 = NULL, -1 = either (finding only, not judged)
    auto expText = [](int e) { return e == 1 ? "HWND" : e == 0 ? "NULL" : "either"; };
    auto loadW = [&](const std::string& name, const std::wstring& path, int expect, const std::string& note = "") {
        MARK("ListLoadW " + name);
        CallRes r = timedLoadW(p, host, path);
        record(name, "ListLoadW", path, r.h != nullptr, -1, r.us, r.err, expText(expect), expect < 0 || (r.h != nullptr) == (expect == 1), note);
        pump(); closeWin(p, r.h);
        return r.h != nullptr;
    };
    auto loadA = [&](const std::string& name, const std::wstring& wpath, int expectWindow) {
        BOOL usedDefault = FALSE;
        int n = WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, wpath.c_str(), -1, nullptr, 0, nullptr, &usedDefault);
        std::string a(n, 0);
        WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, wpath.c_str(), -1, &a[0], n, nullptr, &usedDefault);
        a.resize(strlen(a.c_str()));
        std::wstring back = widen(a, CP_ACP);
        int expect = expectWindow < 0 ? -1 : (expectWindow == 1 && !usedDefault) ? 1 : 0;
        MARK("ListLoad " + name);
        CallRes r = timedLoadA(p, host, a);
        record(name, "ListLoad(ANSI)", back, r.h != nullptr, -1, r.us, r.err, expText(expect), expect < 0 || (r.h != nullptr) == (expect == 1),
               fmt("ACP=%u%s", GetACP(), usedDefault ? ", name not representable in ANSI code page (chars replaced by '?')" : ""));
        pump(); closeWin(p, r.h);
    };
    const std::wstring dir = o.files;
    std::wstring small = dir + L"\\small.bin";
    loadW("empty.bin", dir + L"\\empty.bin", 1, "0-byte file must show an empty view");
    loadW("one.bin", dir + L"\\one.bin", 1);
    loadW("denied.bin", dir + L"\\denied.bin", 0, "ACL denies read");
    {
        std::wstring lp = dir + L"\\locked.bin";
        HANDLE lock = CreateFileW(lp.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (lock == INVALID_HANDLE_VALUE) SUM(fmt("WARNING: could not lock locked.bin (error %lu)", GetLastError()));
        loadW("locked.bin (held, share mode 0)", lp, 0,"held open by harness with no sharing");
        MARK("ListLoadNextW locked");
        CallRes w = timedLoadW(p, host, small);
        if (w.h) {
            CallRes n = timedNextW(p, host, w.h, lp);
            record("locked.bin (held, share mode 0)", "ListLoadNextW", lp, true, n.rc, n.us, n.err, "ERROR(1)", n.rc == LISTPLUGIN_ERROR);
            pump(); closeWin(p, w.h);
        }
        if (lock != INVALID_HANDLE_VALUE) CloseHandle(lock);
    }
    std::wstring uni = dir + L"\\" + UNICODE_NAME;
    loadW("non-ASCII name", uni, 1);
    loadA("non-ASCII name", uni, 1);  // expects NULL when the name is not representable in the ANSI code page
    loadA("small.bin", small, 1);
    {
        MARK("ListLoadNext(ANSI) small");
        CallRes w = timedLoadW(p, host, dir + L"\\one.bin");
        if (w.h) {
            std::string a = narrow(small);  // repo path is ASCII
            CallRes n = timedNextA(p, host, w.h, a);
            record("small.bin", "ListLoadNext(ANSI)", small, true, n.rc, n.us, n.err, "OK(0)", n.rc == LISTPLUGIN_OK);
            pump(); closeWin(p, w.h);
        }
    }
    std::wstring lp = findFileRecursive(dir + L"\\longpath", L"long.bin");
    if (lp.empty()) SUM("WARNING: long path test file not found");
    else {
        // Plain > MAX_PATH paths only open if the host process is long-path aware (manifest + LongPathsEnabled),
        // so these are recorded as findings, not judged.
        loadW("long path, plain", lp, -1, fmt("%zu chars; finding only (depends on host long-path awareness)", lp.size()));
        loadW("long path, \\\\?\\ prefixed", L"\\\\?\\" + lp, 1);
        loadA("long path, plain", lp, -1);
    }
    // Key forwarding: keys the plugin does not handle must be posted to the parent.
    MARK("key forwarding");
    CallRes w = timedLoadW(p, host, small);
    std::string fwdNote;
    bool fwdOk = false;
    if (w.h) {
        pump();
        g_fwdKeyDown = g_fwdChar = g_fwdSysKey = 0; g_fwdKeys.clear();
        WPARAM keys[] = {VK_ESCAPE, 'N', 'P', '1', '3', VK_F2};
        for (WPARAM k : keys) SendMessageW(w.h, WM_KEYDOWN, k, 1 | ((LPARAM)MapVirtualKeyW((UINT)k, MAPVK_VK_TO_VSC) << 16));
        SendMessageW(w.h, WM_CHAR, 'n', 1);
        SendMessageW(w.h, WM_SYSKEYDOWN, VK_MENU, 1 | (1 << 29));
        pumpFor(50);
        // The host pump calls TranslateMessage (as TC does), so forwarded WM_KEYDOWNs produce extra WM_CHARs: require >= 1.
        fwdOk = g_fwdKeyDown == 6 && g_fwdChar >= 1 && g_fwdSysKey == 1;
        fwdNote = fmt("host received WM_KEYDOWN %d/6, WM_CHAR %d (>=1), WM_SYSKEYDOWN %d/1", g_fwdKeyDown, g_fwdChar, g_fwdSysKey);
        closeWin(p, w.h);
    }
    SUM("  key forwarding (Esc,N,P,1,3,F2 keydown + 'n' char + Alt syskeydown): " + fwdNote);
    CHECK("Keys forwarded to parent (harness approximation of manual check)", fwdOk ? "PASS" : "FAIL", fwdNote, "all forwarded");
    int unexpected = 0;
    for (auto& r : results) if (r.find("\"asExpected\":false") != std::string::npos) unexpected++;
    CHECK("Edge cases (locked/denied/empty/non-ASCII/long path)", unexpected ? "FAIL" : "PASS", fmt("%d unexpected of %zu", unexpected, results.size()), "all as expected");
    emit("@@JSON", JObj().add("cases", jarr(results)).add("keyForwarding", JObj().add("ok", jbool(fwdOk)).add("detail", jstr(fwdNote)).str())
        .add("ansiCodePage", jint(GetACP())).str());
    return 0;
}

// Step 7: exception containment with the throw-test build.
static int phaseThrow(const Options& o) {
    HWND host = createHost(); pump();
    Plugin p; loadOrDie(o.dll, p);
    std::wstring tf = o.files + L"\\__throw__.bin", small = o.files + L"\\small.bin";
    MARK("ListLoadW(__throw__.bin)");
    CallRes a = timedLoadW(p, host, tf);
    pump(); closeWin(p, a.h);
    MARK("ListLoad(__throw__.bin) ANSI");
    CallRes b = timedLoadA(p, host, narrow(tf));
    pump(); closeWin(p, b.h);
    MARK("ListLoadW(small.bin)");
    CallRes c = timedLoadW(p, host, small);
    CallRes d; d.rc = -1;
    if (c.h) { MARK("ListLoadNextW(__throw__.bin)"); d = timedNextW(p, host, c.h, tf); pump(); }
    MARK("ListCloseWindow");
    closeWin(p, c.h);
    MARK("survived");
    bool ok = !a.h && !b.h && c.h && d.rc == LISTPLUGIN_ERROR;
    SUM(fmt("ListLoadW(__throw__.bin) -> %s, ListLoad(__throw__.bin) -> %s, ListLoadW(small.bin) -> %s, ListLoadNextW(__throw__.bin) -> %d; process survived",
            a.h ? "HWND" : "NULL", b.h ? "HWND" : "NULL", c.h ? "HWND" : "NULL", d.rc));
    emit("@@JSON", JObj().add("listLoadWReturnedNull", jbool(!a.h)).add("listLoadReturnedNull", jbool(!b.h))
        .add("listLoadWSmallOk", jbool(c.h != nullptr)).add("listLoadNextWRc", jint(d.rc)).add("asExpected", jbool(ok)).str());
    return ok ? 0 : 2;
}

// Step 8: ListCloseWindow then FreeLibrary, then try to load again.
static int phaseUnload(const Options& o) {
    HWND host = createHost(); pump();
    Plugin p; loadOrDie(o.dll, p);
    std::wstring small = o.files + L"\\small.bin";
    MARK("ListLoadW(small.bin)");
    CallRes r = timedLoadW(p, host, small);
    pumpFor(100);
    MARK("ListCloseWindow");
    int64_t t0 = qpc();
    if (r.h) p.Close(r.h);
    double closeUs = usSince(t0, qpc());
    pumpFor(100);
    MARK("FreeLibrary");
    t0 = qpc();
    BOOL freed = FreeLibrary(p.h);
    DWORD freeErr = GetLastError();
    double freeUs = usSince(t0, qpc());
    HMODULE still = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, o.dll.c_str(), &still);
    MARK(fmt("FreeLibrary returned %d (error %lu) in %.3f ms; module still mapped: %s", freed, freeErr, freeUs / 1000, still ? "yes" : "no"));
    SUM(fmt("ListCloseWindow %.3f ms; FreeLibrary returned %s (error %lu) in %.3f ms; module still mapped afterwards: %s",
            closeUs / 1000, freed ? "TRUE" : "FALSE", freeErr, freeUs / 1000, still ? "yes" : "no"));
    pumpFor(500);
    MARK("pumped 500 ms after FreeLibrary");
    // Reload, as a host that frees and later reloads the plugin would.
    MARK("LoadLibraryW again");
    Plugin p2;
    bool reloaded = loadPlugin(o.dll, p2);
    std::string reloadNote = fmt("reload LoadLibraryW %s in %.3f ms", p2.h ? "ok" : "FAILED", p2.loadLibraryUs / 1000);
    bool shownAgain = false;
    if (reloaded) {
        MARK("ListSetDefaultParams after reload");
        initPlugin(p2);
        MARK("ListLoadW after reload");
        CallRes r2 = timedLoadW(p2, host, small);
        shownAgain = r2.h != nullptr;
        pumpFor(100);
        closeWin(p2, r2.h);
        reloadNote += fmt(", ListLoadW after reload -> %s", shownAgain ? "HWND" : "NULL");
    }
    SUM(reloadNote);
    emit("@@JSON", JObj().add("closeMs", jnum(closeUs / 1000)).add("freeLibraryReturned", jbool(freed != FALSE)).add("freeLibraryError", jint(freeErr))
        .add("freeLibraryMs", jnum(freeUs / 1000)).add("moduleStillMapped", jbool(still != nullptr))
        .add("reloadOk", jbool(reloaded)).add("listLoadWAfterReload", jbool(shownAgain)).str());
    MARK("returning from main (process exit)");
    return 0;
}

// ---------------------------------------------------------------------------------------------
// Parent: run children, collect, summarise
// ---------------------------------------------------------------------------------------------
struct ChildRun {
    DWORD exitCode = 0; bool timedOut = false; bool started = false; double ms = 0;
    std::vector<std::string> marks, sums, checks; std::string json; std::string raw;
};
static std::wstring quote(const std::wstring& s) { return L"\"" + s + L"\""; }
static std::string exitStr(DWORD c) {
    if (c == 0xC0000005) return "0xC0000005 (access violation)";
    if (c == 0xC0000409) return "0xC0000409 (stack buffer overrun / fail-fast)";
    if (c == 0xE0434352) return "0xE0434352 (CLR exception)";
    if (c == 0x80131623) return "0x80131623 (FailFast)";
    return c > 0xFFFF ? fmt("0x%08lX", c) : fmt("%lu", c);
}
static ChildRun runChild(const Options& o, const std::wstring& phase, const std::wstring& dll, DWORD timeoutMs) {
    ChildRun cr;
    wchar_t exe[MAX_PATH]; GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring cmd = quote(exe) + L" --child " + phase + L" --dll " + quote(dll) + L" --files " + quote(o.files) +
        L" --switches " + std::to_wstring(o.switches) + L" --pagedowns " + std::to_wstring(o.pageDowns) +
        L" --thumbs " + std::to_wstring(o.thumbs) + L" --thumbmsg " + o.thumbMsg + L" --reps " + std::to_wstring(o.perFileReps) +
        L" --warmup " + std::to_wstring(o.warmup);
    SECURITY_ATTRIBUTES sa = {sizeof sa, nullptr, TRUE};
    HANDLE rd, wr;
    CreatePipe(&rd, &wr, &sa, 1 << 16);
    SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOW si = {sizeof si};
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = wr; si.hStdError = GetStdHandle(STD_ERROR_HANDLE); si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION pi = {};
    std::vector<wchar_t> cl(cmd.begin(), cmd.end()); cl.push_back(0);
    int64_t t0 = qpc();
    if (!CreateProcessW(nullptr, cl.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        CloseHandle(rd); CloseHandle(wr);
        cr.exitCode = GetLastError();
        return cr;
    }
    cr.started = true;
    CloseHandle(wr);
    std::string& raw = cr.raw;
    std::thread reader([&]() { char buf[4096]; DWORD n; while (ReadFile(rd, buf, sizeof buf, &n, nullptr) && n) raw.append(buf, n); });
    if (WaitForSingleObject(pi.hProcess, timeoutMs) == WAIT_TIMEOUT) {
        cr.timedOut = true;
        TerminateProcess(pi.hProcess, 0xDEAD);
        WaitForSingleObject(pi.hProcess, 5000);
    }
    reader.join();
    cr.ms = usSince(t0, qpc()) / 1000;
    GetExitCodeProcess(pi.hProcess, &cr.exitCode);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread); CloseHandle(rd);
    size_t pos = 0;
    while (pos < raw.size()) {
        size_t e = raw.find('\n', pos); if (e == std::string::npos) e = raw.size();
        std::string line = raw.substr(pos, e - pos); pos = e + 1;
        if (!line.empty() && line.back() == '\r') line.pop_back();
        auto starts = [&](const char* p) { return line.rfind(p, 0) == 0; };
        if (starts("@@MARK ")) cr.marks.push_back(line.substr(7));
        else if (starts("@@SUM ")) cr.sums.push_back(line.substr(6));
        else if (starts("@@CHECK ")) cr.checks.push_back(line.substr(8));
        else if (starts("@@JSON ")) cr.json = line.substr(7);
    }
    return cr;
}

static std::vector<std::string> g_allChecks;   // JSON
static std::vector<std::string> g_checkLines;  // console
static void addCheck(const std::string& name, const std::string& verdict, const std::string& measured, const std::string& threshold) {
    g_allChecks.push_back(JObj().add("check", jstr(name)).add("result", jstr(verdict)).add("measured", jstr(measured)).add("threshold", jstr(threshold)).str());
    g_checkLines.push_back(fmt("  [%-4s] %-62s %s  (threshold: %s)", verdict.c_str(), name.c_str(), measured.c_str(), threshold.c_str()));
}
static void addChildChecks(const ChildRun& cr) {
    for (auto& c : cr.checks) {
        std::vector<std::string> parts; size_t s = 0, e;
        while ((e = c.find('|', s)) != std::string::npos) { parts.push_back(c.substr(s, e - s)); s = e + 1; }
        parts.push_back(c.substr(s));
        while (parts.size() < 4) parts.push_back("");
        addCheck(parts[0], parts[1], parts[2], parts[3]);
    }
}
static std::string phaseJson(const ChildRun& cr) {
    return JObj().add("started", jbool(cr.started)).add("exitCode", jint(cr.exitCode)).add("exitCodeText", jstr(exitStr(cr.exitCode)))
        .add("timedOut", jbool(cr.timedOut)).add("wallMs", jnum(cr.ms))
        .add("lastMark", cr.marks.empty() ? "null" : jstr(cr.marks.back()))
        .add("result", cr.json.empty() ? "null" : cr.json).str();
}
static void printPhase(const char* title, const ChildRun& cr) {
    printf("\n== %s ==\n", title);
    for (auto& s : cr.sums) printf("%s\n", s.c_str());
    bool crashed = !cr.started || cr.timedOut || cr.json.empty();
    if (crashed || cr.exitCode != 0)
        printf("  child: %s, exit code %s, last marker: %s\n", cr.timedOut ? "HUNG (killed after timeout)" : cr.started ? "exited" : "FAILED TO START",
               exitStr(cr.exitCode).c_str(), cr.marks.empty() ? "(none)" : cr.marks.back().c_str());
}
static bool hasPhase(const Options& o, const wchar_t* name) {
    std::wstring s = L"," + o.phases + L",";
    return s.find(std::wstring(L",") + name + L",") != std::wstring::npos;
}
static std::string regStr(HKEY root, const wchar_t* key, const wchar_t* val) {
    wchar_t buf[512]; DWORD sz = sizeof buf;
    if (RegGetValueW(root, key, val, RRF_RT_REG_SZ, nullptr, buf, &sz) != ERROR_SUCCESS) return "";
    return narrow(buf);
}
static DWORD regDword(HKEY root, const wchar_t* key, const wchar_t* val) {
    DWORD v = 0, sz = sizeof v;
    RegGetValueW(root, key, val, RRF_RT_REG_DWORD, nullptr, &v, &sz);
    return v;
}

static int runParent(const Options& o) {
    SetConsoleOutputCP(CP_UTF8);
    printf("Lister plugin harness\n  plugin:    %s\n  throw DLL: %s\n  files:     %s\n",
           narrow(o.dll).c_str(), narrow(o.throwDll).c_str(), narrow(o.files).c_str());
    long long dllSize = fileSize(o.dll);
    if (dllSize < 0) { printf("FATAL: plugin not found: %s\n", narrow(o.dll).c_str()); return 1; }
    printf("  plugin size: %lld bytes (%.2f MB)\n", dllSize, dllSize / 1048576.0);
    addCheck("Plugin size", dllSize <= 10 * 1048576 ? "PASS" : "FAIL", fmt("%.2f MB%s", dllSize / 1048576.0, dllSize <= 5 * 1048576 ? " (within 5 MB target)" : ""), "<= 10 MB (target <= 5 MB)");

    const wchar_t* cv = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";
    std::string winBuild = regStr(HKEY_LOCAL_MACHINE, cv, L"CurrentBuild") + "." + std::to_string(regDword(HKEY_LOCAL_MACHINE, cv, L"UBR"));
    std::string cpu = regStr(HKEY_LOCAL_MACHINE, L"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", L"ProcessorNameString");
    printf("  Windows build %s, CPU %s\n", winBuild.c_str(), cpu.c_str());

    auto set = coreSet(o.files);
    for (auto& f : set) if (f.size < 0) printf("  WARNING: test file missing: %s (%s)\n", f.name.c_str(), narrow(f.path).c_str());

    JObj phases;
    // Probe: resolve exports in a child; fail loudly.
    ChildRun probe = runChild(o, L"loadtime", o.dll, 60000);
    if (!probe.json.empty() && probe.json.find("\"fatal\"") != std::string::npos) {
        printPhase("Export check", probe);
        printf("\nFATAL: plugin cannot be used, aborting.\n");
        phases.add("probe", phaseJson(probe));
        return 1;
    }
    {
        std::string det; size_t p = probe.json.find("\"detectString\":");
        if (p != std::string::npos) det = probe.json.substr(p + 15, probe.json.find('}', p) - p - 15);
        printf("\n== Exports ==\nall 7 exports resolved; ListGetDetectString = %s\n", det.c_str());
    }

    if (hasPhase(o, L"loadtime")) {
        std::vector<double> ll, shown, init, startToShown; std::vector<std::string> runs; int failures = 0;
        for (int i = 0; i < o.launches; i++) {
            ChildRun cr = runChild(o, L"loadtime", o.dll, 60000);
            runs.push_back(phaseJson(cr));
            auto num = [&](const char* k) {
                size_t p = cr.json.find(std::string("\"") + k + "\":");
                return p == std::string::npos ? NAN : atof(cr.json.c_str() + p + strlen(k) + 3);
            };
            if (cr.exitCode != 0 || cr.json.empty()) { failures++; continue; }
            ll.push_back(num("loadLibraryMs") * 1000); shown.push_back(num("firstShownMs") * 1000);
            init.push_back(num("initMs") * 1000); startToShown.push_back(num("processStartToShownMs") * 1000);
        }
        Stats sl = stats(ll), ss = stats(shown), si = stats(init), sp = stats(startToShown);
        printf("\n== Step 2: fresh-process launches (%d, %d failed) ==\n", o.launches, failures);
        printf("LoadLibraryW alone:                                 %s\n", sstats(sl).c_str());
        printf("SetDefaultParams+GetDetectString:                   %s\n", sstats(si).c_str());
        printf("LoadLibraryW + ListLoadW(small.bin) + UpdateWindow: %s\n", sstats(ss).c_str());
        printf("process creation -> first file shown (context):     %s\n", sstats(sp).c_str());
        addCheck("First file shown, fresh process (LoadLibrary..first paint)", (failures == 0 && ss.median <= 150 && ss.max <= 300) ? "PASS" : "FAIL",
                 fmt("median %.2f ms, max %.2f ms (LoadLibraryW alone: median %.2f, max %.2f)", ss.median, ss.max, sl.median, sl.max), "median <= 150 ms, max <= 300 ms");
        phases.add("loadtime", JObj().add("launches", jint(o.launches)).add("failures", jint(failures)).add("loadLibrary", jstats(sl))
            .add("setParamsAndDetect", jstats(si)).add("firstShown", jstats(ss)).add("processStartToShown", jstats(sp)).add("runs", jarr(runs)).str());
    }
    struct P { const wchar_t* name; const char* title; DWORD timeout; };
    P list[] = {
        {L"perfile", "Step 4: ListLoadW + UpdateWindow per test file", 300000},
        {L"cycle", "Step 5: ListLoadNextW cycling", 900000},
        {L"scroll", "Step 6: scroll repaint on the largest file", 900000},
        {L"edge", "Edge cases", 120000},
    };
    for (auto& ph : list) {
        if (!hasPhase(o, ph.name)) continue;
        printf("\n(running %s...)\n", narrow(ph.name).c_str()); fflush(stdout);
        ChildRun cr = runChild(o, ph.name, o.dll, ph.timeout);
        printPhase(ph.title, cr);
        addChildChecks(cr);
        if (cr.json.empty() || cr.timedOut) addCheck(std::string("Phase ") + narrow(ph.name) + " completed", "FAIL", cr.timedOut ? "hung" : "crashed, exit " + exitStr(cr.exitCode), "no crash");
        phases.add(narrow(ph.name).c_str(), phaseJson(cr));
    }
    if (hasPhase(o, L"throw")) {
        if (fileSize(o.throwDll) < 0) {
            printf("\n== Step 7: exception containment ==\nSKIPPED: throw-test DLL not found: %s\n", narrow(o.throwDll).c_str());
            addCheck("Exception containment", "SKIP", "throw-test DLL not found", "NULL / ERROR returned; host survives");
            phases.add("throw", "null");
        } else {
            ChildRun cr = runChild(o, L"throw", o.throwDll, 60000);
            printPhase("Step 7: exception containment (throw-test DLL)", cr);
            bool survived = !cr.marks.empty() && cr.marks.back() == "survived" && !cr.timedOut;
            bool asExpected = cr.json.find("\"asExpected\":true") != std::string::npos;
            addCheck("Exception containment", survived && asExpected ? "PASS" : "FAIL",
                     fmt("%s; exit %s; last marker: %s", asExpected ? "error values returned" : "unexpected return values",
                         exitStr(cr.exitCode).c_str(), cr.marks.empty() ? "-" : cr.marks.back().c_str()), "NULL / ERROR returned; host survives");
            phases.add("throw", phaseJson(cr));
        }
    }
    if (hasPhase(o, L"unload")) {
        ChildRun cr = runChild(o, L"unload", o.dll, 30000);
        printPhase("Step 8: ListCloseWindow + FreeLibrary", cr);
        std::string what = cr.timedOut ? "HANG (killed after 30 s)" : fmt("process exit code %s", exitStr(cr.exitCode).c_str());
        printf("  outcome: %s; last marker: %s\n", what.c_str(), cr.marks.empty() ? "(none)" : cr.marks.back().c_str());
        addCheck("Unload: ListCloseWindow + FreeLibrary (finding, not pass/fail)", "INFO",
                 what + "; last marker: " + (cr.marks.empty() ? std::string("-") : cr.marks.back()), "record behaviour");
        std::vector<std::string> mj; for (auto& m : cr.marks) mj.push_back(jstr(m));
        phases.add("unload", JObj().add("phase", phaseJson(cr)).add("marks", jarr(mj)).str());
    }

    printf("\n== Pass criteria (harness-measurable) ==\n");
    for (auto& l : g_checkLines) printf("%s\n", l.c_str());
    printf("  Not measurable here: build warnings, manual Total Commander checks, Process Explorer unload check.\n");

    SYSTEMTIME st; GetLocalTime(&st);
    std::string json = JObj()
        .add("generated", jstr(fmt("%04d-%02d-%02dT%02d:%02d:%02d", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond)))
        .add("plugin", jstr(o.dll)).add("pluginSizeBytes", jint(dllSize)).add("throwDll", jstr(o.throwDll)).add("testFiles", jstr(o.files))
        .add("environment", JObj().add("windowsBuild", jstr(winBuild)).add("cpu", jstr(cpu)).add("ansiCodePage", jint(GetACP())).str())
        .add("settings", JObj().add("launches", jint(o.launches)).add("switches", jint(o.switches)).add("warmup", jint(o.warmup))
                             .add("pageDowns", jint(o.pageDowns)).add("thumbs", jint(o.thumbs)).add("thumbMessage", jstr(o.thumbMsg)).add("perFileReps", jint(o.perFileReps)).str())
        .add("checks", jarr(g_allChecks)).add("phases", phases.str()).str();
    std::wstring jdir = o.json.substr(0, o.json.find_last_of(L'\\'));
    CreateDirectoryW(jdir.c_str(), nullptr);
    FILE* f = _wfopen(o.json.c_str(), L"wb");
    if (f) { fwrite(json.data(), 1, json.size(), f); fclose(f); printf("\nJSON results: %s\n", narrow(o.json).c_str()); }
    else printf("\nERROR: could not write %s\n", narrow(o.json).c_str());
    for (auto& c : g_allChecks) if (c.find("\"result\":\"FAIL\"") != std::string::npos) return 2;
    return 0;
}

int wmain() {
    LARGE_INTEGER f; QueryPerformanceFrequency(&f); g_qpf = (double)f.QuadPart;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);  // as TC 64-bit is DPI aware
    Options o;
    if (!parseArgs(o)) return 1;
    if (o.child.empty()) return runParent(o);
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);  // no WER dialog in children; crash = exit code
    if (o.child == L"loadtime") return phaseLoadtime(o);
    if (o.child == L"perfile") return phasePerfile(o);
    if (o.child == L"cycle") return phaseCycle(o);
    if (o.child == L"scroll") return phaseScroll(o);
    if (o.child == L"edge") return phaseEdge(o);
    if (o.child == L"throw") return phaseThrow(o);
    if (o.child == L"unload") return phaseUnload(o);
    fprintf(stderr, "unknown child phase\n");
    return 1;
}
