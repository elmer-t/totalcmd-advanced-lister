# Other blocks: rules, HTML, images

## Thematic breaks

Three dashes:

---

Three stars:

***

Underscores with spaces:

_ _ _ _

Text right before a rule
---
(that was a setext H2, not a rule)

## HTML blocks

<div align="center">
  <img src="logo.png" alt="Logo" width="120">
  <p>Centered HTML block, shown as dim monospace text.</p>
</div>

<!-- An HTML comment block. -->

<table>
  <tr><td>raw</td><td>html table</td></tr>
</table>

<details>
<summary>Click to expand</summary>

Markdown inside details, after a blank line.

</details>

## Images

A paragraph that is only an image becomes an image block:

![Architecture diagram of the viewer pipeline](docs/pipeline.png)

![](no-alt-text.png)

Linked image (badge):

[![Build](https://img.shields.io/badge/build-passing-green.svg)](https://example.com/ci)

Two images on one line: ![one](a.png) ![two](b.png)

Image with a reference: ![logo][logo-ref]

[logo-ref]: https://example.com/logo.svg "Logo title"

## Footnote-like text (not a GFM feature here)

Some claim.[^1]

[^1]: The footnote text.

## Link reference definitions alone produce no output

[unused]: https://example.com/unused
