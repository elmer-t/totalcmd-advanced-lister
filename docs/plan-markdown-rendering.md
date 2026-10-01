# Plan: Markdown rendering for Advanced Viewer

Oct 1, 2026. Follows the GO decision in `RESULTS.md`. The spike code in `src/AdvancedViewer/` is the
base: Native AOT, CsWin32 with `allowMarshaling:false`, Direct2D/DirectWrite, GCHandle in
`GWLP_USERDATA`, catch-all in every export and in the WndProc, allocation-free hot paths, no
background threads. All of that stays.

## Goal

F3 or Quick View on a `.md` file shows rendered Markdown (CommonMark + GFM tables, task lists,
strikethrough, autolinks) in Total Commander, with the same responsiveness as the hex view:
first paint well under 150 ms in a fresh process, file switches in a few milliseconds for
ordinary READMEs, smooth pixel scrolling, correct re-layout on resize, light and dark theme.

## Decisions

| Topic | Decision | Why |
| --- | --- | --- |
| Parser | **Markdig 1.4.0** NuGet package | Probe (`mdprobe`) published under the plugin's exact AOT settings with 0 warnings, `TreatWarningsAsErrors` + `IlcTreatWarningsAsErrors` on. Adds ~1 MB to the DLL (well inside the 10 MB limit). Writing a CommonMark parser is weeks of work for no gain. |
| Render model | Markdig AST → our own `Document` model (blocks + styled inline runs) → DirectWrite layouts | Keeps DirectWrite code independent of Markdig types; the model is unit-testable on CoreCLR without Direct2D. |
| Text input | `ReadFile` into a bounded buffer (cap 32 MB, then a truncation notice), **not** a memory map | We decode the whole file anyway, and reading avoids `EXCEPTION_IN_PAGE_ERROR` (SEH, uncatchable under AOT) on bad media. Hex view keeps the mapping. |
| Encoding | UTF-8/UTF-16 BOM sniff; otherwise UTF-8 with replacement chars | Markdown is UTF-8 in practice. No ANSI heuristics. |
| Layout | One `IDWriteTextLayout` per block, created lazily and cached; **incremental layout** in time-sliced chunks posted to the window itself (`WM_APP`), never on a background thread | First paint only needs the first screenful. Large files keep the UI responsive; the scroll range grows as blocks get measured (estimated height for unmeasured blocks). |
| Scrolling | Pixel-based (`int` positions in DIPs, clamped) | Natural for variable-height blocks. |
| Width | Content width = min(client − 2×24 DIP, 900 DIP), centered when the client is wider | Readable line length, works in a narrow Quick View panel. Code blocks wrap (no horizontal scroll). |
| Fonts | Body "Segoe UI" 14 DIP; headings 2.0/1.5/1.25/1.1/1.0/0.9 ×; code "Cascadia Mono" with fallback "Consolas" | Always present on Windows 10+. |
| Theme | Light by default; `lcp_darkmode` (128) in `showFlags` selects dark; `ListSendCommand` is exported and logged | SDK 2.13: TC sends the dark flags on load and switches via `ListSendCommand`. |
| Images | Placeholder box with the alt text | WIC decoding is a later feature. |
| Links | Colored + underlined; click opens `http(s)` links with `ShellExecuteW`; hand cursor on hover | Cheap, expected by users. Other schemes are ignored. |
| Detect string | `EXT="MD" \| EXT="MARKDOWN" \| EXT="MDOWN" \| EXT="MKD"` | Narrow, so TC's built-in viewers keep other files and the runtime starts only when needed. With `lcp_forceshow` any file is accepted. |
| Routing | `.md`-family → `MarkdownView`; anything else (only reachable with `lcp_forceshow`) → `HexView` | The hex view stays useful and keeps its measured code. |
| Out of scope now | Text selection/copy (`lc_copy`), `ListSearchText`, printing, images, 32-bit builds, settings UI, syntax highlighting inside code blocks | Listed in "Next steps" at the end. |

## Architecture

```
src/AdvancedViewer/
  Exports.cs                 Lister exports (unchanged ABI), routing by extension, ListSendCommand
  Hosting/
    ViewWindow.cs            the Lister child window: class registration, WndProc, GCHandle,
                             render target + brushes lifecycle, DPI, scroll bar, key forwarding,
                             WM_APP_LAYOUT idle-work pump. Owns exactly one View.
    View.cs                  abstract per-window content: Load/Unload, OnSize, OnPaint, keys,
                             scroll, mouse, idle work
    Theme.cs                 colors for light/dark (D2D1_COLOR_F values) + which is active
    Graphics.cs              shared device-independent factories and text formats (existing)
  Hex/
    HexView.cs               the spike's hex view, now a View (logic unchanged)
    MappedFile.cs
  Markdown/
    Model/                   Document, Block types, InlineRun, StyleFlags (no Win32, no Markdig)
    MarkdownParser.cs        Markdig pipeline → Document
    TextLoader.cs            ReadFile + BOM/UTF-8 decode, size cap
    MarkdownView.cs          View implementation: owns Document + BlockLayout cache, scrolling,
                             hit testing, link clicks
    BlockLayouter.cs         Document block → IDWriteTextLayout with ranges styled, measured
                             height; table column sizing; list/quote indents
    BlockPainter.cs          draws one block: backgrounds, bars, rules, grid, then DrawTextLayout
src/AdvancedViewer.Tests/    xunit on CoreCLR; compiles Markdown/Model + MarkdownParser +
                             TextLoader sources directly (no Win32), tests parser → model
src/Harness/harness.cpp      + `markdown` phase (see Workstream D)
testdata/markdown/           committed corpus of .md files (small, hand-written feature files)
scripts/make-testfiles.ps1   also copies the corpus to testfiles/markdown/ and generates big files
```

### Document model (contract between B and C)

```csharp
namespace AdvancedViewer.Markdown.Model;

sealed class Document { List<Block> Blocks; }

abstract class Block { }
sealed class Heading      : Block { int Level; InlineText Text; }
sealed class Paragraph    : Block { InlineText Text; }
sealed class CodeBlock    : Block { string Code; string? Language; }   // code has '\n' line ends, no trailing newline
sealed class BlockQuote   : Block { List<Block> Children; }
sealed class ListBlock    : Block { bool Ordered; int Start; bool Tight; List<ListItem> Items; }
sealed class ListItem             { bool? Checked; List<Block> Children; }  // Checked != null → task item
sealed class Table        : Block { List<TableRow> Rows; ColumnAlign[] Aligns; }  // Rows[0] is the header
sealed class TableRow             { List<InlineText> Cells; }
sealed class ThematicBreak: Block { }
sealed class HtmlBlock    : Block { string Html; }                     // rendered as dim monospace text
sealed class ImageBlock   : Block { string Alt; string Url; }         // paragraph that is only an image

sealed class InlineText { string Text; List<InlineRun> Runs; }         // Runs cover Text contiguously, in order
readonly record struct InlineRun(int Start, int Length, StyleFlags Style, string? LinkUrl);
[Flags] enum StyleFlags { None=0, Bold=1, Italic=2, Code=4, Strike=8, Link=16, ImagePlaceholder=32 }
enum ColumnAlign { None, Left, Center, Right }
```

Rules: `InlineText.Text` is the final display text (entity-decoded, soft breaks → ' ', hard breaks →
'\n', images → "[alt]" with `ImagePlaceholder`, inline HTML → its raw text with `Code`). Runs never
overlap and never span a '\n' boundary requirement is NOT imposed (DirectWrite ranges can span lines).
Nested emphasis merges flags. Autolinks and raw links produce `Link` runs with `LinkUrl` set.

### View seam (contract between A and C)

`ViewWindow` is the only code that touches HWND messages and the render target. It calls the
abstract `View`:

```csharp
abstract unsafe class View
{
    protected ViewWindow Window;                         // set by ViewWindow before Load
    public string Path { get; protected set; }
    public abstract bool Load(string path, int showFlags, out string? error);  // first load and LoadNext
    public abstract void Unload();                       // release file, layouts, device-independent objects
    public abstract void OnSize(int widthPx, int heightPx, float dpiScale);
    public abstract void OnPaint(ID2D1RenderTarget* rt, Theme theme); // inside BeginDraw/EndDraw; rt is cleared already
    public abstract void OnDeviceLost();                 // release anything created from the render target
    public abstract bool OnKeyDown(int vk, bool ctrl, bool shift);    // true = handled, else forwarded to TC
    public abstract void OnVScroll(int sbCode, int trackPos);
    public abstract void OnMouseWheel(int notches, bool ctrl);
    public virtual  bool OnMouseMove(int xPx, int yPx) => false;      // true = view set the cursor
    public virtual  void OnMouseDown(int xPx, int yPx) { }
    public virtual  bool DoIdleWork(long deadlineTicks) => false;     // true = more work left; ViewWindow re-posts WM_APP_LAYOUT
    public abstract void SetTheme(Theme theme);          // dark/light switch at runtime
}
```

`ViewWindow` services for views: `Hwnd`, `DpiScale`, `ClientWidthPx/HeightPx`, `Invalidate()`,
`SetScrollRange(int totalDip, int pageDip, int posDip)`, `RequestIdleWork()`, `Theme`.

## Workstreams (4 subagents, Opus 5.5)

Wave 1 runs A, B and D in parallel. Wave 2 runs C after A and B merge. The lead (this session)
reviews each result, builds with `scripts/build.ps1`, runs the harness, and merges.

### A. Hosting refactor, routing, text loading, test corpus

1. Extract `Hosting/ViewWindow.cs` + `Hosting/View.cs` + `Hosting/Theme.cs` from `HexView.cs`
   and turn `HexView` into a `View`. Behaviour of the hex view must not change (harness must still
   pass all phases). Keep the GCHandle/`s_liveHandles` protection, first-paint logging
   (`ListLoadW->firstpaint`), `D2DERR_RECREATE_TARGET` handling, DPI handling, key forwarding.
2. Add the `WM_APP_LAYOUT` idle pump: `RequestIdleWork()` posts the message once (coalesced);
   the handler calls `View.DoIdleWork(deadline ≈ 6 ms)` and re-posts while it returns true.
3. `Theme`: light and dark palettes (background, text, muted text, heading rule, link, code
   background, code text, quote bar, table grid, table header background, selection later).
   `lcp_darkmode` in `showFlags` selects dark at `Load`.
4. `Exports.cs`: route by extension (`.md .markdown .mdown .mkd` → `MarkdownView`, else `HexView`);
   new detect string; export `ListSendCommand(HWND, int command, int parameter)` → log and
   return 0; if the SDK (docs/research notes, TC help) names a dark-mode command, apply it via
   `View.SetTheme`.
5. `Markdown/TextLoader.cs`: `ReadFile` into `byte[]` (cap 32 MB, `Truncated` flag), BOM sniff
   (UTF-8, UTF-16 LE/BE), decode to `string`. Unit-testable (no Win32 in the decode part).
6. Stub `Markdown/MarkdownView.cs` that compiles: loads text, draws it as plain wrapped text with
   one layout, so routing and theme can be verified before C lands. C replaces the body.
7. `testdata/markdown/`: hand-written corpus (one file per feature family plus a realistic README,
   a CommonMark-spec-style torture file, a tables file, nested lists/quotes, long lines, a file
   with UTF-8 BOM, a UTF-16 file, an empty file, a file that is only front matter, a file with
   CRLF). `scripts/make-testfiles.ps1`: copy corpus to `testfiles/markdown/`, and generate
   `big-5mb.md` (repeat of the README with headings numbered) and `huge-40mb.md` (exceeds the cap).
8. `scripts/build.ps1` unchanged in interface; must still produce 0 warnings.

### B. Document model, parser, tests

1. `Markdown/Model/*` exactly as the contract above (pure C#, no Win32, no Markdig types leak).
2. `Markdown/MarkdownParser.cs`: Markdig pipeline = `UsePipeTables().UseTaskLists().UseAutoLinks()
   .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UseGridTables()`? (grid tables optional;
   decide and document). Front matter (`---` YAML) is skipped via `UseYamlFrontMatter()` and not
   rendered. Convert every Markdig block/inline into the model; unknown nodes degrade to plain text,
   never throw. Entities decoded. Link titles ignored. Reference links resolved by Markdig.
3. `src/AdvancedViewer.Tests/` (xunit, net10.0, CoreCLR): include the Model, Parser and
   `TextLoader` decode sources via `<Compile Include>`; tests for each block/inline kind, nesting,
   tight/loose lists, task items, table alignment and ragged rows, hard/soft breaks, entities,
   inline HTML, images, autolinks, strikethrough, BOM decode, cap/truncation. `dotnet test` green.
4. Confirm the plugin still publishes with 0 warnings after adding the Markdig
   `PackageReference` (version 1.4.0, `PrivateAssets` not needed). Note the DLL size delta in the
   report back.

### C. Layout and painting (after A and B)

1. `BlockLayouter`: for each block create an `IDWriteTextLayout` at the given width with
   `SetFontWeight/Style/FamilyName/Size/Underline/Strikethrough` per run and `SetDrawingEffect`
   with a brush for link/code colors (brushes come from the painter per theme; drawing effects
   must be re-applied when the theme or device changes, so store run ranges, not brushes, in the
   cache). Headings: size scale + bottom rule for H1/H2 + extra top spacing. Paragraph spacing
   0.75 em. Lists: marker column 24 DIP per level, bullets • ◦ ▪, ordered "n." right-aligned,
   task items ☐/☑ (Segoe UI Symbol). Block quotes: 4 DIP bar + 16 DIP indent, nestable. Code
   blocks: mono, 12 DIP padding, background, wrap with `DWRITE_WORD_WRAPPING_WRAP` plus
   emergency break. Tables: natural width per column from an unconstrained layout of each cell,
   clamp to content width by shrinking the widest columns proportionally; header row bold with
   background; 1 px grid; 8 DIP cell padding; per-column alignment. Thematic break: 1 px rule.
   HTML blocks: mono, muted. Image placeholder: dashed box with alt text.
2. `MarkdownView`: holds `Document`, a `BlockEntry[]` (block, measured height or −1, y offset
   once known), incremental measuring in `DoIdleWork` (measure until deadline, update scroll
   range: known heights + average × remaining), viewport in pixels, `OnPaint` draws only blocks
   intersecting the viewport (binary search on y), `OnSize` with a width change discards layouts
   and keeps the top block index + fraction, `SetTheme` keeps layouts and swaps brushes, link hit
   testing with `HitTestPoint` on the hovered block, hand cursor, `ShellExecuteW` on click of
   `http`/`https`. Keys: Up/Down 40 DIP, PageUp/PageDown viewport − 40 DIP, Home/End, Ctrl+Home/End;
   wheel per `SPI_GETWHEELSCROLLLINES`.
3. Allocation discipline: no per-paint allocations after warm-up (reuse rects, arrays); layouts
   cached per block; log `WM_PAINT` as the hex view does; log `MarkdownParse`, `MarkdownLayout`
   (first screenful) and `MarkdownLayoutDone` events with durations.
4. Fonts: `Graphics` gains the body, heading, code and symbol text formats, created once.
5. Verify visually with the harness `--show` option (if D adds one) or a scratch host, in light
   and dark, at 100 % and 150 % DPI, on every corpus file. Report screenshots/notes.

### D. Harness markdown phase, measurements, docs

1. `harness.cpp`: new phase `markdown` (in the default phase list after `scroll`): for every
   file in `testfiles/markdown/`, `ListLoadW` + `UpdateWindow` timing (first paint), then a
   `ListLoadNextW` cycle (`--switches`, default 1000) over the corpus with private bytes and
   GDI/USER counts before/after, then on `big-5mb.md`: 2,000 PageDown presses and 200 resize
   steps (width 300→1600 px and back) timing each `UpdateWindow`, plus wheel messages. Pass
   thresholds: first paint of README-class files median ≤ 20 ms (warm process), `big-5mb.md`
   first paint ≤ 150 ms (incremental layout should make it independent of size), switch median
   ≤ 20 ms, repaint avg ≤ 16 ms / worst ≤ 50 ms, leak ≤ 5 MB and GDI/USER ±2. Use
   `SHOW_FLAGS` 0 and additionally run the first-paint pass with `lcp_darkmode` (128). Emit
   `@@CHECK` lines and JSON like the other phases.
2. `--show <file> [--dark]` option: load one file into a visible 1000×800 parent window and
   pump messages until closed, for manual inspection and screenshots.
3. `scripts/run-harness.ps1` unchanged; document the new phase in the usage header.
4. `docs/markdown-rendering.md`: how the pipeline works, how to add a block type, the
   measurement results table (filled by the lead after the final run), known limitations, next
   steps (selection/copy, search, images via WIC, syntax highlighting, shared D3D device).

## Verification by the lead

- `scripts/build.ps1 -Clean` → 0 warnings, exports intact, size reported.
- `dotnet test src/AdvancedViewer.Tests` green.
- `scripts/run-harness.ps1` all phases pass, including the new `markdown` phase; hex phases
  unchanged against `RESULTS.md` numbers (within noise).
- Visual check through `--show` on the corpus (light + dark); fix list sent back to the owning
  agent.
- Code review of every diff for: catch-all coverage, COM Release pairing (every `Create*` has a
  `Release`), no allocations in `OnPaint`, no static state that breaks on a TC reload, no
  background threads.
- Commit on branch `worktree-markdown-rendering`, push, and hand over with manual TC checks
  listed for Elmer.

## Manual checks in Total Commander (after merge)

- [ ] Install the new zip. F3 on `testfiles/markdown/readme.md` renders Markdown. F3 on `small.bin`
      uses TC's built-in viewer (detect string is narrow). Lister menu → plugin forces the hex view.
- [ ] Dark mode on (Configuration → Colors → Dark mode): Lister and Quick View show the dark theme.
- [ ] Quick View through `testfiles/markdown/`: no flicker, no focus loss, Down arrow cycles.
- [ ] Resize Lister from narrow to wide: text reflows, scroll position stays near the same block.
- [ ] Click an `https` link: the browser opens. Esc, N/P, 1–7 keys still work.
- [ ] `big-5mb.md` and `huge-40mb.md` open without a noticeable delay; the scroll thumb shrinks as
      layout progresses; End key reaches the end once layout is complete.
