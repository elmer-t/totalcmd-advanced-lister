# Lists and block quotes

## Tight bullet list

- apples
- oranges
- pears

## Loose bullet list

* first item, with a blank line after it

* second item

* third item

## Bullet markers change start a new list

- dash list
+ plus list
* star list

## Ordered lists

1. one
2. two
3. three

7. starts at seven
8. eight
9. nine
10. ten (wider number)
11. eleven

1) parenthesis delimiter
2) second

## Nesting

- Level 1
  - Level 2
    - Level 3
      - Level 4
        - Level 5
  - Back to level 2
- Level 1 again

1. Ordered outer
   - bullet inside ordered
   - another
     1. ordered inside bullet inside ordered
     2. second
2. Ordered outer, second

## List items with several blocks

1. A paragraph in a list item.

   A second paragraph in the same item, indented to the content column.

   ```js
   const insideList = true;
   ```

   > A quote inside a list item.

2. Next item.

## Task lists

- [ ] not done
- [x] done
- [X] done with capital X
- [ ] task with **bold** and a [link](https://example.com)
  - [x] nested task
  - [ ] another nested task
- not a task, plain bullet

1. [x] ordered task list item
2. [ ] second

## Block quotes

> A simple quote.

> A quote with two paragraphs.
>
> This is the second paragraph.

> Lazy continuation: this line belongs to the quote
even without the marker.

> ## A heading in a quote
>
> - a list in a quote
> - second item
>
> ```
> code in a quote
> ```

## Nested quotes

> Level one
>
> > Level two
> >
> > > Level three, with *emphasis*
> >
> > Back to two
>
> Back to one

## Edge cases

-

- (item above is empty)

* a list item
that continues lazily

10000000. a long ordinal number

- item

      indented code inside the item (needs content indent + 4)
