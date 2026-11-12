# CaptionRelay design course — sources

`docs/CaptionRelay-course.pdf` is built from these files:

| Path | What |
|---|---|
| `chapters/NN-name.html` | One file per chapter, in order. Each is a complete right-to-left HTML page that links `../style.css`, so it can also be opened directly in a browser. |
| `diagrams/*.svg` | The diagrams, referenced from chapters as `<img class="dia" src="../diagrams/NAME.svg">`. |
| `style.css` | Print stylesheet (A4, page numbers, boxes, tables, cover). |
| `build.py` | Builds the PDF (standard library only). |

## Build

```
python docs/course/build.py          # writes docs/CaptionRelay-course.pdf
python docs/course/build.py --html   # also keeps docs/course/course-preview.html
```

Needs Microsoft Edge or Google Chrome (headless printing). The build:

1. runs an RTL check on every chapter and stops if it finds a problem (`--force` skips it);
2. prints each chapter on its own to count its pages, for the table of contents;
3. inlines the SVG diagrams (their ids and CSS classes are prefixed per diagram, because inline `<style>` is global)
   and prints the whole course with a PDF outline.

## Writing rules (right-to-left text)

- Every Persian paragraph, list item, table cell, caption and title starts with a Persian word.
- Never put English on both sides of a quote mark, bracket, dash, arrow or Persian comma — the bidi algorithm reorders
  them. Join English names with «و», or wrap identifiers in `<code>` (isolated, left-to-right).
- English UI strings go in `<code>`, not in «».
- Number ranges and units in a Persian cell go in `<bdi>`.
- English blocks (`.interview`, `.qa`) are left-to-right and are not checked.
