---
title: Torture test
layout: none
---

Torture test
============

A setext H1 above, an ATX H2 below, then every construct the viewer knows plus the
edge cases CommonMark spec examples like to poke at.

## Headings ##

# H1
## H2 with `code` and *emphasis*
### H3 with a [link](https://example.com "title")
#### H4 trailing hashes ####
##### H5 \#escaped hash
###### H6
####### seven hashes is a paragraph
#hashtag is a paragraph too

#

Setext H2 with **bold**
-----------------------

## Thematic breaks

***
---
___
 * * *
-  -  -

## Paragraphs and breaks

Line one
line two (soft break)  
line three after two-space hard break\
line four after backslash hard break
   indented continuation line

Trailing spaces at paragraph end are not a break.  

## Emphasis

*italic* _italic_ **bold** __bold__ ***both*** ___both___ **_mixed_** *__mixed__*
**bold *bold italic ~~bold italic strike~~ bold italic* bold**
snake_case_word stays plain, but _this_ is italic. 2*3*4 and a * b * c.
*unclosed **also unclosed ~~and this
~~strike~~ ~single tilde~ ~~~triple~~~
**bold with `code` inside** and *[link](https://a.example) inside italic*
foo*bar*baz *foo**bar**baz* **foo*bar*baz**

## Code spans

`plain` ``with ` backtick`` ` padded ` `` ` `` `<b>not html</b>` `&amp; not entity`
`unclosed code span

## Entities and escapes

&amp; &lt; &gt; &quot; &#39; &copy; &reg; &trade; &hellip; &mdash; &nbsp;x
&#35; &#1234; &#x22; &#XD06; &#0; &#99999999; &MadeUpEntity; &amp
\*not\* \_not\_ \`not\` \# \[not\](link) \\ backslash \< \> \!

## Links

[inline](https://example.com) [with title](https://example.com "Title")
[empty url]() [angle](<https://example.com/a b>) [relative](../docs/x.md#anchor)
[ref full][ref] [ref collapsed][] [ref] [Ref Case-Insensitive][REF]
[nested *emphasis* and `code` in label](https://example.com/n)
[undefined ref] [also][undefined]
<https://autolink.example/path?q=1&r=2> <mailto:a@b.example> <user@mail.example>
Bare: https://bare.example/x?y=z, www.bare.example, and http://localhost:8080/p.
Not a link: [text] (space) [text]

[ref]: https://ref.example/ "Ref title"
[collapsed]: /c
[ref collapsed]: https://collapsed.example
[ref case-insensitive]: https://case.example

## Images

![Block image](images/block.png "Block")

Inline ![small *alt*](images/i.png) image and a linked badge
[![build passing](https://ci.example/badge.svg)](https://ci.example).

![ref image][imgref]

[imgref]: images/ref.png

![a](1.png) ![b](2.png)

## Inline HTML

Press <kbd>Ctrl</kbd>+<kbd>C</kbd>. Comment <!-- hidden? --> and <span style="color:red">span</span>.
Not HTML: a < b > c and <3 and <not a tag.

## HTML blocks

<div align="center">
  <img src="logo.png" alt="logo">
  <p>Centered</p>
</div>

<!--
multi-line comment
-->

<details>
<summary>Click</summary>

Markdown *inside* details.

</details>

## Code blocks

```csharp
// fenced with language
public static int Main() => 0;
```

~~~ python extra info words
def f():
    return "~~~"
~~~

````
```
nested fence
```
````

    indented code block
      keeps relative indent

    after blank line

```
unclosed fence inside a section is closed by the end of its container
```

## Block quotes

> Quote paragraph
continued lazily
>
> > Nested quote with **bold**
> >
> > - list in nested quote
>
> ```
> code in quote
> ```
>
> # Heading in quote

>No space after marker

## Lists

- tight 1
- tight 2
  - nested tight
    - deeper
      - deepest
- tight 3

* loose 1

* loose 2

  second paragraph in loose 2

1. one
2. two
3. three

7) starts at seven
8) eight

0. starts at zero

1. ordered
   - [ ] unchecked task
   - [x] checked task
   - [X] upper-case checked
   - [ ] task with *emphasis* and [link](https://t.example)
   - [] not a task
   - [ ]no space is not a task either
2. after tasks

- item with code

  ```
  code in item
  ```

- item with quote
  > quote in item
  > - list in quote in item
  >   > quote in list in quote in list

-
  empty first line item

- 

+ plus bullet
+ another

10. multi-digit start
11. next

## Tables

| Left | Center | Right | None |
|:-----|:------:|------:|------|
| l | c | r | n |
| **bold** | `code` | [link](https://tbl.example) | ~~strike~~ |
| escaped \| pipe | &amp; | ![img](t.png) | <b>html</b> |
| ragged |
| too | many | cells | here | extra |

No outer pipes | second
--- | ---
a | b

| single column |
|---|
| row |

Not a table because there is no delimiter row:
| a | b |
| c | d |

## Edge cases

Trailing backslash at end of paragraph\

Tabs	inside	text and	→ arrows.

Unicode: héllo wörld — “quotes” 中文 🎉 𝄞 é

Very long line: Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur.

***bold italic at start** italic rest*

**[bold link](https://bl.example)** [**link with bold**](https://lb.example)

Footnote-like[^1] syntax is not enabled.

[^1]: So this is a paragraph or a reference definition.

Final paragraph with no trailing newline.