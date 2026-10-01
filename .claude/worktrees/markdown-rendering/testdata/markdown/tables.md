# Tables

## Simple

| Name | Value |
| --- | --- |
| alpha | 1 |
| beta | 2 |

## Alignment

| Left | Center | Right | Default |
| :--- | :---: | ---: | --- |
| a | b | c | d |
| longer left text | centered | 1,234.56 | plain |
| x | y | 7 | z |

## Inline formatting in cells

| Feature | Syntax | Example |
| --- | --- | --- |
| Bold | `**text**` | **bold** |
| Italic | `*text*` | *italic* |
| Code | `` `code` `` | `x = 1` |
| Link | `[t](url)` | [example](https://example.com) |
| Strike | `~~text~~` | ~~gone~~ |
| Pipe | `\|` | a \| b |

## No leading/trailing pipes

Column A | Column B
-------- | --------
one | two
three | four

## Ragged rows

| A | B | C |
| --- | --- | --- |
| only one cell |
| one | two |
| one | two | three | four (extra cell is dropped) |
| | empty first cell | |

## Wide table (must fit the content width)

| Id | Name | Description | Owner | Created | Updated | Status | Priority | Estimate | Tags |
| --- | --- | --- | --- | --- | --- | --- | --- | ---: | --- |
| 101 | Parser | Convert the Markdig syntax tree into the document model without leaking Markdig types | B | 2026-10-01 | 2026-10-02 | in progress | high | 3 | parser, model |
| 102 | Layout | One IDWriteTextLayout per block, created lazily and cached, incremental measuring | C | 2026-10-01 | 2026-10-03 | todo | high | 5 | layout, dwrite |
| 103 | Harness | Markdown phase with first-paint, switch, scroll and resize measurements | D | 2026-10-01 | 2026-10-01 | done | medium | 2 | test |

## Long unbreakable cell content

| Key | Value |
| --- | --- |
| url | https://example.com/a/very/long/path/that/does/not/contain/any/spaces/and/must/still/fit/somehow/index.html |
| hash | 3f786850e387550fdab836ed7e6dc881de23001b3f786850e387550fdab836ed7e6dc881de23001b |

## Header only

| Lonely | Header |
| --- | --- |

## Not a table (delimiter row missing)

| a | b |
| c | d |

## Unicode in cells

| Language | Greeting |
| --- | :---: |
| Japanese | こんにちは |
| Greek | Γειά σου |
| Arabic | مرحبا |
| Emoji | 👋🌍 |
