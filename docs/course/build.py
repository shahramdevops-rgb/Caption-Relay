"""Builds docs/CaptionRelay-course.pdf from the chapter fragments and SVG diagrams in docs/course/.

    python docs/course/build.py

Pass 1 prints every chapter on its own (each chapter starts on a new page) and counts its pages, so the table of
contents can show page numbers. Pass 2 prints the whole course with Microsoft Edge (or Chrome) in headless mode.
Only the standard library is used.
"""

import os
import re
import shutil
import subprocess
import sys
import tempfile
from html.parser import HTMLParser
from pathlib import Path

HERE = Path(__file__).resolve().parent
OUT_PDF = HERE.parent / "CaptionRelay-course.pdf"
BROWSERS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
]


def browser():
    for b in BROWSERS:
        if os.path.exists(b):
            return b
    sys.exit("Edge or Chrome not found")


def svg(name):
    """Inline SVG with its ids prefixed, so markers of different diagrams never collide."""
    text = (HERE / "diagrams" / (name + ".svg")).read_text(encoding="utf-8")
    text = re.sub(r"<\?xml[^>]*\?>\s*", "", text)
    p = name.replace("_", "-") + "-"
    text = re.sub(r'\bid="([^"]+)"', lambda m: f'id="{p}{m.group(1)}"', text)
    text = re.sub(r"url\(#([^)]+)\)", lambda m: f"url(#{p}{m.group(1)})", text)
    text = re.sub(r'href="#([^"]+)"', lambda m: f'href="#{p}{m.group(1)}"', text)
    # Inline <style> rules are global to the page: scope every class name to this diagram as well.
    text = re.sub(r'class="([^"]+)"', lambda m: 'class="' + " ".join(p + c for c in m.group(1).split()) + '"', text)
    text = re.sub(r"<style>(.*?)</style>", lambda m: "<style>" + re.sub(r"\.([A-Za-z][\w-]*)", lambda c: "." + p + c.group(1), m.group(1)) + "</style>", text, flags=re.S)
    return text


def expand(html):
    """Body of a chapter file, with every diagram <img class="dia"> replaced by the inline SVG."""
    m = re.search(r"<body>(.*)</body>", html, re.S)
    body = m.group(1) if m else html
    return re.sub(r'<img class="dia" src="\.\./diagrams/([\w-]+)\.svg"[^>]*>', lambda m: svg(m.group(1)), body)


def page(body):
    css = (HERE / "style.css").read_text(encoding="utf-8")
    return (
        '<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">'
        "<title>CaptionRelay - design course</title>"
        f"<style>{css}</style></head><body>{body}</body></html>"
    )


def chapters():
    return sorted((HERE / "chapters").glob("*.html"))


def toc_entries(files):
    """(anchor id, number, Persian title, English title) of every chapter that has data-toc."""
    entries = []
    for f in files:
        html = f.read_text(encoding="utf-8")
        m = re.search(r'<section[^>]*id="([^"]+)"[^>]*data-num="([^"]*)"[^>]*data-fa="([^"]*)"[^>]*data-en="([^"]*)"', html)
        if m:
            entries.append((f, m.group(1), m.group(2), m.group(3), m.group(4)))
    return entries


def toc_html(entries, pages):
    rows = []
    for f, anchor, num, fa, en in entries:
        n = pages.get(f, "")
        rows.append(
            f'<a class="toc-row" href="#{anchor}"><span class="toc-num">{num}</span>'
            f'<span class="toc-fa">{fa}</span><span class="toc-en">{en}</span>'
            f'<span class="toc-dots"></span><span class="toc-page">{n}</span></a>'
        )
    return '<nav class="toc">' + "".join(rows) + "</nav>"


def print_pdf(exe, html_file, pdf_file, profile):
    args = [
        exe,
        "--headless",
        "--disable-gpu",
        "--no-first-run",
        "--no-pdf-header-footer",
        "--generate-pdf-document-outline",
        f"--user-data-dir={profile}",
        f"--print-to-pdf={pdf_file}",
        html_file.as_uri(),
    ]
    subprocess.run(args, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
    if not pdf_file.exists():
        sys.exit(f"printing failed: {html_file}")


def page_count(pdf_file):
    data = pdf_file.read_bytes()
    counts = [int(c) for c in re.findall(rb"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)", data, re.S)]
    counts += [int(c) for c in re.findall(rb"/Count\s+(\d+)[^>]*?/Type\s*/Pages\b", data, re.S)]
    if not counts:
        sys.exit(f"could not count pages of {pdf_file}")
    return max(counts)


class _RtlAudit(HTMLParser):
    """Checks the Persian blocks (paragraphs, list items, cells, captions, titles) for two bidi problems.

    1. A Persian line that starts with a Latin word: every Persian block must start with a Persian word.
    2. English on both sides of a quote mark, bracket, dash, arrow or Persian comma, e.g. "... UI» (BeginInvoke)": the bidi algorithm puts the
       punctuation in the wrong place. Text inside <code> and <bdi> is isolated and exempt from this check.
    Blocks inside English containers (interview boxes, Q&A, SVG ...) are skipped.
    """

    BLOCKS = {"p", "li", "td", "th", "dt", "dd", "figcaption", "h1", "h2", "h3", "div"}
    LTR_CLASSES = {"interview", "qa", "en", "ltr", "sub", "kicker", "title", "title-en", "meta", "toc", "chapter-num", "en-tag"}
    ISOLATED = {"code", "bdi"}
    MIXED = re.compile(r"[A-Za-z0-9][^\w؀-ۿ]*[«»()\[\]“”—–→←،][^\w؀-ۿ]*[A-Za-z]")

    def __init__(self):
        super().__init__()
        self.stack = []  # (tag, is_ltr)
        self.blocks = []  # open blocks: [full text, text outside isolated runs] or None
        self.isolated = 0
        self.problems = []

    def handle_starttag(self, tag, attrs):
        if tag in ("img", "br", "meta", "link", "hr"):
            return
        a = dict(attrs)
        classes = set((a.get("class") or "").split())
        ltr = bool(self.stack and self.stack[-1][1]) or tag == "svg" or a.get("dir") == "ltr" or bool(classes & self.LTR_CLASSES)
        self.stack.append((tag, ltr))
        self.blocks.append(["", ""] if tag in self.BLOCKS and not ltr else None)
        if tag in self.ISOLATED:
            self.isolated += 1
            for b in self.blocks:
                if b is not None:
                    b[1] += " ک "  # an isolated run behaves like a neutral Persian word

    def handle_endtag(self, tag):
        while self.stack:
            t, _ = self.stack.pop()
            block = self.blocks.pop()
            if t in self.ISOLATED:
                self.isolated -= 1
            if block is not None:
                self.check(*block)
            if t == tag:
                break

    def handle_data(self, data):
        if self.stack and self.stack[-1][1]:
            return  # text of an English element is its own block, not part of the Persian one around it
        for b in self.blocks:
            if b is not None:
                b[0] += data
                if not self.isolated:
                    b[1] += data

    def check(self, text, outside):
        text = " ".join(text.split())
        if not re.search(r"[؀-ۿ]", text):
            return
        first = next((c for c in text if c.isalpha()), "")
        if first and not ("؀" <= first <= "ۿ"):
            self.problems.append("starts with a Latin word: " + text[:70])
        o2 = " ".join(outside.split())
        if re.search(r"«[^«»؀-ۿ]*[A-Za-z][^«»؀-ۿ]*»", o2):
            self.problems.append("English inside «»; use <code> instead: " + o2[:70])
        m = self.MIXED.search(" ".join(outside.split()))
        if m:
            o = " ".join(outside.split())
            self.problems.append("English around a quote/bracket: ..." + o[max(0, m.start() - 25):m.end() + 25])


def rtl_audit(files):
    bad = 0
    for f in files:
        a = _RtlAudit()
        a.feed(re.sub(r"<svg.*?</svg>", "", expand(f.read_text(encoding="utf-8")), flags=re.S))
        for t in dict.fromkeys(a.problems):
            print(f"RTL: {f.name}: {t}")
            bad += 1
    return bad


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    files = chapters()
    if rtl_audit(files) and "--force" not in sys.argv:
        sys.exit("fix the RTL problems above (or pass --force)")
    exe = browser()
    entries = toc_entries(files)
    tmp = Path(tempfile.mkdtemp(prefix="captionrelay-course-"))
    profile = tmp / "profile"
    try:
        # Pass 1: pages per chapter (the TOC chapter is printed with a dummy TOC of the same size).
        start = {}
        current = 1
        dummy_toc = toc_html(entries, {f: "00" for f, *_ in entries})
        for i, f in enumerate(files):
            body = expand(f.read_text(encoding="utf-8")).replace("<!--toc-->", dummy_toc)
            h = tmp / f"part{i:02}.html"
            h.write_text(page(body), encoding="utf-8")
            pdf = tmp / f"part{i:02}.pdf"
            print_pdf(exe, h, pdf, profile)
            n = page_count(pdf)
            start[f] = current
            print(f"{f.name}: page {current}, {n} page(s)")
            current += n

        # Pass 2: the whole course.
        toc = toc_html(entries, start)
        body = "".join(expand(f.read_text(encoding="utf-8")) for f in files).replace("<!--toc-->", toc)
        h = tmp / "course.html"
        h.write_text(page(body), encoding="utf-8")
        if "--html" in sys.argv:
            shutil.copy(h, HERE / "course-preview.html")
        out = tmp / "course.pdf"
        print_pdf(exe, h, out, profile)
        total = page_count(out)
        if total != current - 1:
            print(f"warning: course has {total} pages, chapters added up to {current - 1}")
        shutil.copy(out, OUT_PDF)
        print(f"wrote {OUT_PDF} ({total} pages)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
