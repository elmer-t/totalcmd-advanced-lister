# Encoding test: UTF-8 with BOM

This file is stored as **UTF-8 with BOM**. Every line below must show the same characters in all encoding test files.

- Latin-1: café, naïve, Zürich, øre, ß, ¿qué?
- Greek: Καλημέρα κόσμε
- Cyrillic: Съешь же ещё этих мягких французских булок
- CJK: 日本語のテキスト, 中文文本, 한국어 텍스트
- Symbols: € £ ¥ © ® ™ ° ± × ÷ → ⇒ ∞ ≈ ≠ ≤ ≥
- Emoji (outside the BMP, surrogate pairs in UTF-16): 😀 🚀 🧪

| Script | Sample |
| --- | --- |
| Hebrew (RTL) | שלום עולם |
| Arabic (RTL) | مرحبا بالعالم |

```
code: "äöü" → ok
```
