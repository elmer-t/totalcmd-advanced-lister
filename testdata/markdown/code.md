# Code blocks

## Fenced, with language

```csharp
[UnmanagedCallersOnly(EntryPoint = "ListLoadW")]
public static nint ListLoadW(nint parentWin, char* fileToLoad, int showFlags)
{
    try
    {
        return LoadCore(parentWin, new string(fileToLoad), showFlags);
    }
    catch (Exception ex)
    {
        Log.Exception("ListLoadW", null, 0, ex);
        return 0;
    }
}
```

```python
def fib(n: int) -> int:
    """Return the n-th Fibonacci number."""
    a, b = 0, 1
    for _ in range(n):
        a, b = b, a + b
    return a

print([fib(i) for i in range(10)])
```

```json
{
  "name": "advanced-viewer",
  "version": "0.2.0",
  "features": ["markdown", "hex"],
  "nested": { "ok": true, "count": 3 }
}
```

## Fenced with tildes and no language

~~~
Tilde fences work too.
    Indentation inside is preserved.
~~~

## Longer fence containing a shorter one

````markdown
```
inner fence is just text
```
````

## Indented code block

    10 PRINT "HELLO"
    20 GOTO 10

## Tabs and trailing whitespace

```
	one tab
		two tabs
trailing spaces   
```

## Long lines in code (must wrap, no horizontal scroll)

```sh
curl -sSL -H "Authorization: Bearer $TOKEN" -H "Accept: application/json" "https://api.example.com/v2/organizations/acme/projects/advanced-viewer/releases?per_page=100&page=1" | jq -r '.[] | select(.draft == false) | "\(.tag_name)\t\(.published_at)\t\(.assets | length) assets"'
```

## Empty code block

```
```

## Unclosed fence at end of file runs to the end

```text
This fence is never closed.
Everything below is code.

# Not a heading
