# Tidepool

[![Build status](https://img.shields.io/badge/build-passing-brightgreen.svg)](https://example.com/tidepool/actions)
[![Crates.io](https://img.shields.io/badge/crates.io-v0.14.2-orange.svg)](https://example.com/crates/tidepool)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Tidepool** is a small, fast log collector for developer machines and CI runners. It tails
files, journald and container output, tags every line with where it came from, and lets you
query the last few hours with a tiny SQL-like language. No server, no agent fleet, no YAML
sprawl: one binary and one config file.

> **Status:** beta. The on-disk format is stable since 0.12; the query language may still
> change before 1.0. See the [changelog](#changelog) for breaking changes.

![Screenshot of the tidepool TUI showing three panes of colored log lines](docs/img/tui-overview.png)

## Contents

1. [Features](#features)
2. [Installation](#installation)
3. [Quick start](#quick-start)
4. [Configuration](#configuration)
   1. [Sources](#sources)
   2. [Retention](#retention)
   3. [Environment variables](#environment-variables)
5. [Query language](#query-language)
6. [Performance](#performance)
7. [FAQ](#faq)
8. [Contributing](#contributing)
9. [Changelog](#changelog)
10. [License](#license)

## Features

- Tails **plain files**, rotated files (`app.log`, `app.log.1`, `app.log.2.gz`) and whole
  directories with glob patterns.
- Reads **journald** on Linux and the **Windows Event Log** on Windows.
- Follows **container output** from Docker and Podman, including containers that start later.
- Stores everything in a compact, append-only segment format with a per-segment index:
  - 6 to 10 times smaller than the raw text for typical application logs;
  - queries over a day of logs usually finish in under 100 ms.
- A terminal UI with live tail, filters, bookmarks and a timeline.
- Exports to JSON Lines, CSV and plain text.
- ~~Requires a running daemon~~ Runs in the foreground or as a user service; your choice.

### What it is not

Tidepool is *not* a replacement for a centralized logging stack. It keeps data on the
machine it runs on, it has no authentication, and it does not ship logs anywhere. If you need
that, look at a real log pipeline and use tidepool only as a local debugging aid.

## Installation

### Prebuilt binaries

Download the archive for your platform from the [releases page](https://example.com/tidepool/releases),
unpack it, and put `tidepool` (or `tidepool.exe`) somewhere on your `PATH`.

| Platform | Archive | Size | Notes |
| --- | --- | ---: | --- |
| Linux x86-64 | `tidepool-x86_64-unknown-linux-musl.tar.gz` | 4.1 MB | static, any distro |
| Linux ARM64 | `tidepool-aarch64-unknown-linux-musl.tar.gz` | 3.8 MB | Raspberry Pi 4 and later |
| macOS (Apple silicon) | `tidepool-aarch64-apple-darwin.tar.gz` | 3.9 MB | notarized |
| macOS (Intel) | `tidepool-x86_64-apple-darwin.tar.gz` | 4.2 MB | notarized |
| Windows x64 | `tidepool-x86_64-pc-windows-msvc.zip` | 3.6 MB | needs the VC++ runtime |

### Package managers

```sh
# Homebrew
brew install tidepool

# Scoop
scoop bucket add extras
scoop install tidepool

# Cargo (builds from source, needs Rust 1.80 or later)
cargo install tidepool --locked
```

### From source

```sh
git clone https://example.com/tidepool.git
cd tidepool
cargo build --release
./target/release/tidepool --version
```

## Quick start

Start collecting from a directory of logs and open the UI:

```sh
tidepool collect ./logs/*.log --name myapp &
tidepool ui
```

Ask a question from the command line instead:

```sh
tidepool query "level >= warn and source = 'myapp' since 2h" --format table
```

The first run creates `~/.local/share/tidepool` (Linux), `~/Library/Application Support/tidepool`
(macOS) or `%LOCALAPPDATA%\tidepool` (Windows). Delete that folder to start over.

### Keyboard shortcuts in the UI

| Key | Action |
| :---: | :--- |
| `/` | Filter |
| `n` / `N` | Next / previous match |
| `b` | Toggle bookmark on the current line |
| `Tab` | Switch pane |
| `g` / `G` | Jump to start / end |
| `Ctrl`+`C` | Quit |

## Configuration

The config file is TOML and lives next to the data directory as `tidepool.toml`. Every
setting has a default, so an empty file is valid. A typical setup:

```toml
[storage]
path = "~/.local/share/tidepool"
max_size = "2 GiB"
retention = "7d"

[[source]]
name = "api"
kind = "file"
paths = ["/var/log/api/*.log"]
multiline = { start = '^\d{4}-\d{2}-\d{2}' }

[[source]]
name = "containers"
kind = "docker"
include = ["web-*", "worker-*"]
```

### Sources

Each `[[source]]` table describes one input. The common keys are:

- `name` (**required**): a short identifier, used in queries as `source = '...'`.
- `kind` (**required**): one of `file`, `journald`, `eventlog`, `docker`, `podman`.
- `tags`: extra key/value pairs attached to every line, e.g. `{ env = "dev" }`.
- `parser`: how to split a line into fields.
  - `auto` (default) tries JSON, then logfmt, then plain text.
  - `json`, `logfmt` and `plain` force one format.
  - `regex` takes a pattern with named groups:

    ```toml
    parser = { regex = '^(?P<ts>\S+) (?P<level>\w+) (?P<msg>.*)$' }
    ```

- `multiline`: joins continuation lines (stack traces) onto the previous entry.

### Retention

Old segments are deleted when **either** limit is reached:

1. `retention` (age), default `3d`;
2. `max_size` (total size on disk), default `1 GiB`.

Retention runs once a minute and never blocks collection.

> [!NOTE]
> Bookmarked lines are kept until you remove the bookmark, even past the retention limit.

### Environment variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `TIDEPOOL_HOME` | platform data dir | Overrides `storage.path` |
| `TIDEPOOL_LOG` | `warn` | Tidepool's own log level |
| `TIDEPOOL_NO_COLOR` | unset | Disables colors in the CLI and UI |
| `NO_COLOR` | unset | Same, see <https://no-color.org> |

## Query language

Queries are a filter expression followed by optional clauses:

```
<filter> [since <duration>] [until <time>] [order by <field> [desc]] [limit <n>]
```

Filters compare fields with `=`, `!=`, `<`, `<=`, `>`, `>=`, `~` (regex match) and `contains`,
and combine them with `and`, `or` and `not`. Some examples:

```sql
-- Errors from the API in the last 30 minutes
source = 'api' and level = error since 30m

-- Slow requests, slowest first
msg ~ 'request finished' and duration_ms > 500 order by duration_ms desc limit 20

-- Everything mentioning a request id, from all sources
contains '7f3c9a21'
```

Field names come from the parser. For plain text lines only `ts`, `level`, `msg`, `source`
and the configured tags exist. Levels are ordered `trace < debug < info < warn < error < fatal`.

### Output formats

`--format` accepts `text` (default), `table`, `json` and `csv`. JSON output is one object per
line, ready for `jq`:

```sh
tidepool query "level >= error since 1d" --format json | jq -r '.msg' | sort | uniq -c | sort -rn
```

## Performance

Measured on a 2023 laptop (8 cores, NVMe SSD) with 10 GB of mixed application logs:

| Operation | Time | Memory |
| --- | ---: | ---: |
| Ingest, 1 source | 310 MB/s | 45 MB |
| Ingest, 8 sources | 1.1 GB/s | 120 MB |
| Query, indexed field, 1 day | 38 ms | 30 MB |
| Query, full-text regex, 1 day | 1.9 s | 64 MB |
| Open UI on 10 GB | 0.4 s | 80 MB |

Numbers vary a lot with the log format; JSON logs compress less than plain text. Run
`tidepool bench` to measure on your own data.

## FAQ

**Does tidepool modify or delete my log files?**
No. It only reads them. Rotated and compressed files are read in place.

**Why is the first query after startup slower?**
The segment indexes are memory-mapped lazily. The first query pages them in; later queries
hit the page cache.

**Can I run several collectors at once?**
Yes, as long as each uses its own `storage.path`. Two collectors on the same directory refuse
to start and print the PID of the one that holds the lock.

**What about Windows paths with spaces?**
Quote them in the shell and in TOML: `paths = ['C:\Program Files\App\logs\*.log']`. Single
quotes in TOML are literal strings, so backslashes need no escaping.

<details>
<summary>Why not use SQLite for storage?</summary>

We tried. Append throughput was fine, but the database grew 3 to 4 times larger than the
segment format, and vacuuming during retention stalled ingestion for seconds.

</details>

## Contributing

Contributions are welcome. Before you open a pull request:

- [x] Read the [code of conduct](CODE_OF_CONDUCT.md).
- [x] Run `cargo fmt` and `cargo clippy --all-targets -- -D warnings`.
- [ ] Add a test for the bug you fixed or the feature you added.
- [ ] Update this README if you changed user-visible behaviour.

Bigger changes should start as an issue so we can agree on the design first. The rough
architecture:

```
           +-----------+      +-----------+      +-----------+
 files --> |  readers  | ---> |  parsers  | ---> |  segment  | --> disk
 journald  | (1/source)|      | (pool)    |      |  writer   |
 docker    +-----------+      +-----------+      +-----------+
                                                       |
                                    query engine <-----+
```

### Running the tests

```sh
cargo test                      # unit and integration tests
cargo test --features docker    # also the Docker source (needs a running daemon)
./scripts/fuzz.sh parser 300    # fuzz the parsers for 5 minutes
```

Tests that need network access are marked `#[ignore]`; run them with `cargo test -- --ignored`.

### Release checklist

1. Bump the version in `Cargo.toml` and run `cargo update -p tidepool`.
2. Move the *Unreleased* changelog entries under the new version heading.
3. Tag the commit: `git tag -s v0.14.3 -m "tidepool 0.14.3"`.
4. Push the tag; CI builds the archives and drafts the release.
5. Check the draft, then publish it.

## Changelog

### Unreleased

- Windows Event Log source: filter by channel and event id.
- `tidepool export --since` accepts absolute times.

### 0.14.2 (2026-08-30)

- Fixed: a rotated file that was truncated in place was read twice.
- Fixed: the UI timeline ignored the local time zone on macOS.

### 0.14.0 (2026-07-12)

- **Breaking:** `max_age` was renamed to `retention`. The old name still works and prints a
  deprecation warning.
- New `contains` operator, about 4 times faster than an equivalent `~` regex.
- New `podman` source.

### 0.12.0 (2026-04-02)

- On-disk format v3 (stable from now on). Run `tidepool migrate` once after upgrading.

---

## License

MIT. See [LICENSE](LICENSE). Copyright &copy; 2024&ndash;2026 the tidepool authors.

Tidepool bundles third-party code under their own licenses; `tidepool licenses` prints the
full list.
