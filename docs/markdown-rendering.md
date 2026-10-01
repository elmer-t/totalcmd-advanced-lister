# Markdown rendering in Advanced Viewer

How the Markdown view works, how it is measured, and what is still missing. Design decisions and
workstreams are in `docs/plan-markdown-rendering.md`; spike measurements are in `RESULTS.md`.
The architecture below follows the plan; where the code differs from the plan, this file describes the code.

## Pipeline

```
file ──ReadFile──▶ TextLoader ──bytes──▶ TextDecoder ──string──▶ MarkdownParser (Markdig, in chunks for big files)
     ──▶ Document model ──▶ ItemBuilder (flat LayoutItem[]) ──▶ BlockLayouter (IDWriteTextLayouts near the viewport)
     ──▶ BlockPainter (Direct2D)
```

| Stage | Code | Notes |
| --- | --- | --- |
| Load | `Markdown/TextLoader.cs`, `Markdown/TextDecoder.cs` | `ReadFile` into a buffer capped at 32 MB (then a truncation notice, `TextDecoder.AppendTruncationNotice`). BOM sniff for UTF-8 and UTF-16 LE/BE, otherwise UTF-8 with replacement characters; CRLF/CR → LF. No memory map, so a read error is a normal error, not an SEH `EXCEPTION_IN_PAGE_ERROR`. |
| Parse | `Markdown/MarkdownParser.cs`, `Markdown/ChunkSplitter.cs` | Markdig 1.4.0: CommonMark plus pipe tables, task lists, autolinks, strikethrough. YAML front matter is skipped. Unknown nodes degrade to plain text and never throw. Texts over 128 K characters are parsed in ~64 K-character chunks (see below). |
| Flatten | `Markdown/LayoutItem.cs` (`ItemBuilder`, `LayoutItem`) | The block tree becomes one array of leaf items (paragraph, heading, code chunk, HTML chunk, table row, rule, image). Containers turn into properties of the items inside them: x indent, quote bars (x positions, and where each bar starts), list markers ("•", "3.", "☑") and the gap above the item. Code and HTML blocks are split into items of at most 200 lines. |
| Model | `Markdown/Model/*` | `Document` → `Block`s (Heading, Paragraph, CodeBlock, BlockQuote, ListBlock/ListItem, Table, ThematicBreak, HtmlBlock, ImageBlock) with `InlineText` = display text + contiguous `InlineRun`s (`StyleFlags`, link URL). No Win32 and no Markdig types, so it is unit-tested on CoreCLR (`src/AdvancedViewer.Tests`). |
| Layout | `Markdown/BlockLayouter.cs` | `IDWriteTextLayout`s for one item at the content width (min(client − 48 DIP, 900 DIP), centred): body "Segoe UI" 14 DIP, code "Cascadia Mono" 13 DIP (falls back to "Consolas" when Cascadia is not installed, checked once with `FindFamilyName`), headings ×2.0/1.5/1.25/1.1/1.0/0.9 semibold. Weight, style, font family/size (inline code), underline (links) and strikethrough are applied per run. Colours are drawing effects applied from the runs with the current brushes (`ApplyEffects`), so the items store run ranges, not brushes. Inline-code background rectangles come from `HitTestTextRange` at layout time. Word wrapping is `DWRITE_WORD_WRAPPING_EMERGENCY_BREAK` everywhere (long URLs and code lines break inside words instead of overflowing). |
| Paint | `Markdown/BlockPainter.cs` | Quote bars, list markers, code backgrounds, inline-code backgrounds, the table grid and header background, rules, the dashed image box, then `DrawTextLayout`. `Brushes` holds one solid brush per theme colour. Only items that intersect the viewport are drawn. No allocations per paint after warm-up (the `WM_PAINT` log lines show `gc=` otherwise). |

### Incremental parsing and layout

All of this runs on the UI thread; there are no background threads. `ViewWindow` posts
`WM_APP_LAYOUT` to its own window and calls `View.DoIdleWork(deadline)` (about 6 ms per slice)
until it returns false.

- **Parsing.** Markdig parses about 15 MB/s, so a 5 MB file would block for ~350 ms. Texts over
  128 K characters are parsed in chunks: `Load` parses the first ~64 K characters, and each
  `DoIdleWork` call parses one more chunk and appends its items. `ChunkSplitter` ends a chunk
  just before a line that starts in column 0 after a blank line, outside fenced code and
  multi-line HTML blocks, where every open container has ended anyway. Link reference
  definitions are collected from the whole text up front (max 32 KB) and appended to every
  chunk, so reference links resolve across chunks. A list that a split cuts in two becomes two
  lists (item numbers are kept; tight/loose spacing may differ).
- **Heights.** Every item starts with an estimated height (characters × average glyph width,
  wrapped at the item width). `HeightIndex` is a Fenwick tree over gap + height, so "y of item
  i" and "item at y" are O(log n) and a measured height replaces an estimate in O(log n). The
  first screenful is measured synchronously in `Load` (logged as `MarkdownLayout`); the rest is
  measured in idle slices after parsing (`MarkdownLayoutDone` when finished, with the busy time).
  The scroll range is the sum of the current heights, so the thumb adjusts as estimates are
  replaced; the End key keeps the view pinned to the end while heights change.
- **Scroll position** is kept as (top item, offset into it). Measuring items above the viewport
  therefore never moves the visible text. A width change sets all heights back to estimates,
  releases the layouts and keeps the top item and its relative offset, so the reading position
  stays put; the visible items are measured again on the next paint and the rest in idle time.
- **Layouts** exist only for items near the viewport: they live in pooled `LiveLayouts` slots and
  are released when an item is more than 12 items away from the painted range. Measuring an item
  off screen creates its layouts, reads the height and releases them.

Log events (`%TEMP%\viewer-spike.log`): `MarkdownParse` (first or only parse, with characters
parsed and items), `MarkdownParseDone` (chunked files: wall time since load and parse busy time),
`MarkdownLayout` (first screenful), `MarkdownLayoutDone` (all items measured: wall time since
load and busy time), and `MarkdownSlowChunk` / `MarkdownSlowMeasure` for idle slices over 20 ms
with the number of GCs that ran in them. On the 32 MB file, slices of 50–150 ms occur during
parsing; every one of them contained a GC (non-concurrent GC with a large live heap).

### Tables

Each row is one item. Column widths are computed once per table and width: the natural width of
a column is its widest unconstrained cell, the minimum is its longest word (`DetermineMinWidth`),
both over the first 1,000 rows, plus 8 DIP padding per side. If the natural widths do not fit,
the widest columns are shrunk to a common cap (water filling) but not below their longest word;
only when even the longest words do not fit are words broken. The header row is semibold on the
header background; the grid is 1 device pixel; cells are aligned per column.

### Theme

`Hosting/Theme.cs` holds light and dark palettes: background, text, muted text, heading rule, link,
code background and text, quote bar, table grid, table header. `lcp_darkmode` (128) in `showFlags`
selects dark at load. `ListSendCommand` is exported, and a runtime switch calls `View.SetTheme`.
The Markdown view then recolours its brushes in place (`ID2D1SolidColorBrush::SetColor`), so the
layouts and their drawing effects stay valid. When the render target goes away
(`OnDeviceLost`), the view releases all live layouts first (their drawing effects reference the
brushes) and then the brushes.

### Routing

`Exports.cs` routes by extension: `.md .markdown .mdown .mkd` go to `MarkdownView`. Any other
file goes to `HexView`; that only happens with `lcp_forceshow`, because the detect string is
`EXT="MD" | EXT="MARKDOWN" | EXT="MDOWN" | EXT="MKD"`. `ViewWindow` (`Hosting/ViewWindow.cs`) is
the only code that handles HWND messages and the render target. It owns exactly one `View`.

### Adding a block type

1. Add the class to `Markdown/Model` and map the Markdig node to it in `MarkdownParser`.
2. Add parser tests in `src/AdvancedViewer.Tests`.
3. Give it an `ItemKind` and emit items for it in `ItemBuilder` (containers only add indent, bars
   or markers and recurse).
4. Handle the kind in `BlockLayouter` (`Layout`, `Estimate`, and `ApplyEffects` if it has
   coloured runs) and `BlockPainter` (decorations).
5. Add a file to `testdata/markdown/` and check it with `Harness.exe --show <file>` in light and dark.

## Measurement: the `markdown` harness phase

`scripts/run-harness.ps1 --phases markdown` runs `src/Harness/harness.cpp` in a child process
against `testfiles/markdown/*.md` (sorted). "README-class" means ≤ 1 MB. All timings are the
plugin call plus a synchronous `UpdateWindow`, so they include the first or next paint.

| Step | What |
| --- | --- |
| a. First paint | One cold `ListLoadW` (flags 0) on `readme.md`, the first plugin window in the process. Then `ListLoadW` + `UpdateWindow` + `ListCloseWindow` for every file (warm). For files > 1 MB, the **layout settle time** is the time from the end of the first paint to the last `WM_APP` message handled. The harness pumps until no `WM_APP` message arrives for 200 ms, at most 10 s. |
| b. Dark | Step a for the first 5 files with `showFlags = lcp_darkmode` (128). |
| c. Switching | `ListLoadNextW` + paint over the README-class files: `--warmup` (50), then `--switches` (1000). Private bytes and GDI/USER object counts are taken before and after. Then one switch into `big-5mb.md` and one into `huge-40mb.md`, each followed straight away by a switch back. |
| d. Scroll and resize | `big-5mb.md`, after layout has settled. 2,000 `WM_KEYDOWN VK_NEXT`, then 200 resize steps: client width 300 → 1600 → 300 px in 13 px steps, height 800. Each step resizes the parent and the plugin child, because Total Commander resizes the child itself. Each repaint is timed. |
| e. Wheel | `big-5mb.md`: 500 `WM_MOUSEWHEEL` notches down, then 500 up, each repaint timed. |

Output follows the other phases: `@@CHECK` lines in the console summary and a `phases.markdown`
object in `--json`. The JSON includes per-file times, settle times, memory snapshots, and the
index of the slowest sample for each repaint series.

`Harness.exe --show <file> [--dark]` opens one file in a visible, resizable 1000×800 window
in-process. It is for visual checks and screenshots; Esc or closing the window exits.

## Results

Measured 2026-10-01 on the merged build (`scripts/build.ps1 -Clean`, plugin 2,231,808 bytes, then
`scripts/run-harness.ps1 --phases markdown` on the 20-file corpus; same machine as `RESULTS.md`).

| Check | Threshold | Measured | Result |
| --- | --- | --- | --- |
| First paint, cold (`readme.md`, first window in process) | record | 89.1 ms | INFO |
| First paint, README-class, warm, light | median ≤ 20 ms | median 5.56 ms, max 20.4 ms (18 files) | PASS |
| First paint, README-class, dark | median ≤ 20 ms | median 3.80 ms, max 4.82 ms (4 files) | PASS |
| First paint, `big-5mb.md` | ≤ 150 ms | 27.0 ms | PASS |
| First paint, `huge-40mb.md` (32 MB cap) | record | 94.2 ms | INFO |
| Layout settle time, `big-5mb.md` / `huge-40mb.md` | record | 2.3 s (410 idle slices) / 15.2 s (2,656 slices, 32 MB cap applies) | INFO |
| Switch (`ListLoadNextW` + paint), README-class | median ≤ 20 ms | median 1.75 ms, p99 4.28 ms, max 75.3 ms (1 of 1,000) | PASS |
| Switch into `big-5mb.md` / `huge-40mb.md` | record | 12.7 ms / 65.1 ms; back to a README 1.6 / 2.0 ms | INFO |
| Leaks over switches: private bytes | ≤ +5 MB | −131 MB (garbage from the big-file steps was collected during the cycle) | PASS |
| Leaks over switches: GDI/USER objects | ±2 | GDI +0, USER +0 | PASS |
| PageDown repaint, `big-5mb.md` | avg ≤ 16 ms, worst ≤ 50 ms | avg 1.52 ms, p99 2.80 ms, worst 10.7 ms (2,000 presses) | PASS |
| Resize repaint, `big-5mb.md` | avg ≤ 16 ms, worst ≤ 50 ms (50–100 ms INFO) | avg 6.77 ms, p99 13.8 ms, worst 15.5 ms (200 steps) | PASS |
| Wheel repaint, `big-5mb.md` | avg ≤ 16 ms, worst ≤ 50 ms | avg 0.87 ms, p99 1.56 ms, worst 9.75 ms (1,000 notches) | PASS |

Hex view regression (same build, spike corpus, `--launches 5`): first file in a fresh process
median 66.2 ms, max 67.6 ms; next file median 1.61 ms, p99 2.84 ms, max 4.28 ms; large file 0.96×;
private bytes +3.98 MB over 1,000 switches (spike: +1.4 MB; the extra is managed garbage from the
one `.md` file in the mixed corpus, which creates a fresh view per visit and is collected by a
later GC); GDI/USER +0; edge cases 0 unexpected of 12; exception containment and key forwarding
pass. PageDown repaint: avg 1.53 ms, p99 2.60 ms, one sample of 10,000 at 45.5 ms (the same
single-sample outlier the spike saw; see `RESULTS.md`, note on repaint).

Note: on a busy machine, a single repaint sample above the worst-case limit is usually scheduling
noise (see `RESULTS.md`, note on repaint). Check the `worstIndex` and the p99 before treating it
as a defect.

## Known limitations

- No text selection, copy (`lc_copy`/`lc_selectall`), search (`ListSearchText`) or printing.
- Images are drawn as a placeholder box with the alt text; nothing is decoded.
- Code blocks have no syntax highlighting and wrap instead of scrolling horizontally.
- Files above 32 MB are truncated, and a notice is shown.
- Only `http`/`https` links open (`ShellExecuteW`); relative links and anchors do nothing.
- Raw HTML is shown as dim monospace text, not rendered.
- Until incremental layout finishes on a large file, the scroll range is an estimate, so the
  thumb position is approximate, and the document grows while the rest of the file is parsed
  (the End key follows the end).
- In files over 128 K characters (parsed in chunks), a list that crosses a chunk boundary is shown
  as two lists, and an HTML block of a kind that may contain blank lines other than comments,
  `<pre>`, `<script>`, `<style>` and `<textarea>` could be cut. Reference definitions beyond the
  first 32 KB of definitions do not resolve across chunks.
- Memory: the model and the item array of a 32 MB file take several hundred MB of managed memory
  until the next full GC after the file is closed.
- Table column widths are measured from the first 1,000 rows of a table.
- List markers are aligned with the first line of the item; when the item starts with a heading
  the marker sits at the heading's top, not its baseline.
- The scroll bar is the standard Win32 one. `ViewWindow` themes it with `SetWindowTheme`
  ("DarkMode_Explorer" / "Explorer"), which needs Windows 10 1809 or later; older systems keep
  the light scroll bar in dark mode.
- Each window creates its own hardware `ID2D1HwndRenderTarget`, which costs most of the cold
  first-paint time (see `RESULTS.md`, render target comparison).

## Next steps

- Selection and copy: hit testing per block, then `lc_copy` and `lc_selectall` via `ListSendCommand`.
- `ListSearchText` over the model text, scrolling the hit into view.
- Images through WIC (local relative paths first), decoded off the paint path.
- Syntax highlighting in code blocks (per-language run styling in the layouter).
- One shared D3D device with `ID2D1DeviceContext` and a swap chain per window, instead of one
  `HwndRenderTarget` per window, to cut the cold first paint and memory.
- Quick View focus handling (`itm_focus`), so keyboard focus stays where Total Commander expects it.
