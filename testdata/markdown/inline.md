# Inline formatting

Plain text, *italic with stars*, _italic with underscores_, **bold with stars**,
__bold with underscores__, ***bold italic***, and **bold with *nested italic* inside**.

Intra-word: snake_case_names_stay_plain, but un*frigging*believable is emphasised.

Strikethrough: ~~this was wrong~~ and ~single tildes~ (GFM accepts both).

Inline code: `printf("%d\n", x)`, ``code with a ` backtick``, and `  padded  `.
Emphasis markers inside code are literal: `*not italic*`.

## Links

- Inline link: [CommonMark](https://commonmark.org)
- With title (ignored): [GFM spec](https://github.github.com/gfm/ "GitHub Flavored Markdown")
- Reference link: [the spec][spec] and a collapsed one: [CommonMark][]
- Autolink: <https://example.com/path?query=1&b=2>
- Email autolink: <someone@example.com>
- Bare URL (GFM extension): https://www.example.org/docs/page.html and www.example.net
- Relative link: [license](../LICENSE) and an anchor: [top](#inline-formatting)
- Link with **bold** and `code` in the text: [**bold** `code`](https://example.com)
- Non-http scheme (must not open on click): [ftp file](ftp://example.com/file.txt), [mail](mailto:someone@example.com)

[spec]: https://spec.commonmark.org/0.31.2/
[CommonMark]: https://commonmark.org

## Images inline

An inline image ![tiny icon](icon.png) in the middle of a sentence.

## Line breaks

Soft break: this line ends without spaces
and continues here (should render as one line).

Hard break with two trailing spaces  
next line.

Hard break with a backslash\
next line.

## Entities and escapes

Entities: &amp; &lt; &gt; &quot; &copy; &trade; &nbsp;(nbsp) &#x1F600; &#169; &ouml;

Escapes: \*not italic\*, \_not italic\_, \`not code\`, \# not a heading, \[not a link\](x)

Literal characters: 3 < 5 and 7 > 2, AT&T, 100% sure.

## Inline HTML

Some <kbd>Ctrl</kbd>+<kbd>C</kbd>, a <span style="color:red">span</span>, and <br> a br tag.
H<sub>2</sub>O and E = mc<sup>2</sup>.
