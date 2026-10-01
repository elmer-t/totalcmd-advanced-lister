# Torture test (CommonMark spec style)

Tricky inputs taken in spirit from the CommonMark spec examples. The renderer must not throw
on any of them; exact output only matters where it is obviously wrong.

## Tabs

	foo	baz		bim

  - foo

	bar

## Precedence

- `one
- two`

## Thematic break vs list

* * *
- foo
***
- bar

## ATX heading edge cases

#5 bolt

\## foo

# foo *bar* \*baz\*

#                  foo                     

### foo ### b

# foo #

## Setext edge cases

Foo *bar
baz*
====

  Foo
---

Foo
= =

## Indented code vs paragraph continuation

  a simple
    indented code? no, a paragraph continuation

    chunk1

    chunk2



    chunk3

## Fences

``
foo
``

```
aaa
~~~
```

``` aa ```
foo

```ruby startline=3 $%@#$
def foo(x)
  return 3
end
```

## Link reference definitions

[foo]: /url "title"

[foo]

   [foo2]: 
      /url  
           'the title'  

[foo2]

[Foo*bar\]]:my_(url) 'title (with parens)'

[Foo*bar\]]

## Paragraphs and blank lines

aaa
             bbb
                                       ccc

  

aaa  


## Block quotes

>     code

>    not code

> - foo
- bar

> ```
foo
```

## List items

- one

 two

-    one

     two

 -    one

      two

1.  A paragraph
    with two lines.

        indented code

    > A block quote.

- foo
  - bar
    - baz


      bim

## Backslash escapes

\!\"\#\$\%\&\'\(\)\*\+\,\-\.\/\:\;\<\=\>\?\@\[\\\]\^\_\`\{\|\}\~

\	\A\a\ \3\φ\«

## Entities

&nbsp; &amp; &copy; &AElig; &Dcaron;
&frac34; &HilbertSpace; &DifferentialD;
&ClockwiseContourIntegral; &ngE;

&#35; &#1234; &#992; &#0;

&nbsp &x; &#; &#x;
&#87654321;
&abcdef0;
&ThisIsNotDefined; &hi?;

## Code spans

`foo`

`` foo ` bar ``

` `` `

`foo   bar 
baz`

`foo\`bar`

*foo`*`

## Emphasis

*foo bar*

a * foo bar*

a*"foo"*

* a *

foo*bar*

5*6*78

_foo bar_

_ foo bar_

foo_bar_

**foo bar**

** foo bar**

*(*foo*)*

*foo**bar**baz*

***foo** bar*

foo***bar***baz

*foo [bar](/url)*

**foo*

*foo**

__foo, __bar__, baz__

## Links

[link](/uri "title")

[link]()

[link](<>)

[link](/my uri)

[link](foo(and(bar)))

[link](#fragment)

[a](<b)c>)

[link [foo [bar]]](/uri)

[link *foo **bar** `#`*](/uri)

[![moon](moon.jpg)](/uri)

[foo *bar](baz*)

## Autolinks

<http://foo.bar.baz>

<MAILTO:FOO@BAR.BAZ>

<a+b+c:d>

<http://foo.bar/baz bim>

http://example.com

## Raw HTML

<a><bab><c2c>

<a  /><b2
data="foo" >

Foo <responsive-image src="foo.jpg" />

<33> <__>

## Hard line breaks

foo  
baz

foo\
baz

foo       
baz

`code  
span`

foo\

### foo\

## Very deep nesting

> > > > > > > > > > deep quote

- a
  - b
    - c
      - d
        - e
          - f
            - g
              - h
                - i
                  - j

*a **b *c **d *e **f *g***h***i**j*

## Unicode

Ünïcödé text, Ελληνικά, русский, עברית (RTL), العربية (RTL), 中文, 日本語, 한국어.
Combining: é vs é. Emoji with ZWJ: 👩‍💻 👨‍👩‍👧‍👦 🏳️‍🌈. Flags: 🇳🇱 🇯🇵.
Zero-width space:​here. Soft hyphen: super­cali­fragi­listic.
