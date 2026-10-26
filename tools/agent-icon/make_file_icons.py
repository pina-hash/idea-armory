#!/usr/bin/env python3
"""Draws the Armory window's file-type icons (0.5.0, ledger 0437) into src/Armory.Agent/wwwroot/index.html.

Every kind in Armory.Core's FileKinds (src/Armory.Core/FileKinds.cs) has one drawing here: a SolidWorks
part, assembly and drawing (the three the window always had), a drawing template, a library feature, a
block, eDrawings, CAD exchange files, meshes, print files, drawings for cutting, laser jobs, PDFs,
documents, spreadsheets, slides, text, code, pictures, video, archives, and the generic file for anything
else. A picture Windows can make of the file (ShellThumbnails) still wins over the icon.

The Armory look, as the window's other icons (index.html's sprite): line drawings on a 20 x 20 grid,
1.3 px strokes in the text's own ink (currentColor, so both themes and every state color work), round
joins, no fill, no text and no brand marks. Each kind has its own silhouette, so the row icon reads at
20 px and the same drawing is crisp at 192 px in the picture slot. The four kinds the window always had
keep their ids and their exact drawings (i-part, i-asm, i-drw, i-file); every other kind is i-k-<kind>.

Standard library only. It replaces the block between the two "file kinds" markers in index.html and
writes a review sheet, docs/agent/screens/file-icons.svg (every icon at 20, 48 and 192 px, light and
dark), so a change is a reviewable diff:

    python3 tools/agent-icon/make_file_icons.py

Run with --check to fail (exit 1) when index.html is not what this script writes (the window tests run
the same comparison in C#: AgentViewContractTests).
"""

import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
INDEX = os.path.join(ROOT, "src", "Armory.Agent", "wwwroot", "index.html")
SHEET = os.path.join(ROOT, "docs", "agent", "screens", "file-icons.svg")
BEGIN = "<!-- file kinds: made by tools/agent-icon/make_file_icons.py; edit the script, not these lines -->"
END = "<!-- /file kinds -->"

# The sheet every document-like kind starts from: a page with its corner folded.
PAGE = "M4.5 2.5h7l4 4v11h-11zM11.5 2.5v4h4"

# kind -> (what it shows, the drawing's path data). Order is FileKinds.Kinds' order.
ICONS = [
    ("part", "A SolidWorks part: one solid.",
     "M10 2.5 16.5 6v8L10 17.5 3.5 14V6zM3.5 6 10 9.5 16.5 6M10 9.5v8"),
    ("asm", "An assembly is two parts mated, so it never reads as the one-cube part at 20px.",
     "M6.2 8.3 10.18 10.6v4.6L6.2 17.5l-3.98-2.3v-4.6zM2.22 10.6 6.2 12.9l3.98-2.3M6.2 12.9v4.6M13.8 2.5l3.98 2.3v4.6l-3.98 2.3-3.98-2.3V4.8zM9.82 4.8l3.98 2.3 3.98-2.3M13.8 7.1v4.6"),
    ("drw", "A drawing: a page with two lines of notes.",
     PAGE + "M7 10.5h6M7 13.5h4"),
    ("template", "A drawing template: a page with its title block and nothing drawn yet.",
     PAGE + "M7 12h6v3.5H7zM10 12v3.5"),
    ("feature", "A library feature: a solid with a plus, ready to add to a part.",
     "M8 6.25 13 9v5.5l-5 2.75-5-2.75V9zM3 9l5 2.75L13 9M8 11.75v5.5M15.5 2.5v5M13 5h5"),
    ("block", "A block: a sketch shape grouped in a frame.",
     "M3.5 4.5h13v11h-13zM6.5 12.5 10 7l3.5 5.5z"),
    ("edrawing", "eDrawings: a page with an eye, for viewing.",
     PAGE + "M6 13.25c1.2-1.9 2.55-2.85 4-2.85s2.8.95 4 2.85c-1.2 1.9-2.55 2.85-4 2.85S7.2 15.15 6 13.25zM10 12.4a.85.85 0 1 0 0 1.7.85.85 0 1 0 0-1.7z"),
    ("cad", "CAD exchange (STEP, IGES, Parasolid): a wire box, every edge seen.",
     "M3.5 7.5h9v9h-9zM7.5 3.5h9v9h-9M3.5 7.5l4-4M12.5 7.5l4-4M12.5 16.5l4-4M7.5 3.5v9M3.5 16.5l4-4h5"),
    ("mesh", "A mesh (STL, 3MF, OBJ): a surface of triangles.",
     "M10 3 17 16H3zM6.5 9.5h7M6.5 9.5 10 16l3.5-6.5"),
    ("print", "A print file (G-code): a nozzle laying down layers.",
     "M7.5 2.5h5V6L10 8.75 7.5 6zM6 11h8M4.5 13.75h11M3 16.5h14"),
    ("vector", "A drawing for cutting (DXF, DWG, SVG): a curve with its two end points.",
     "M4 15c2.5-8.5 9.5-1.5 12-10M2.75 13.75h2.5v2.5h-2.5zM14.75 3.75h2.5v2.5h-2.5zM4 15l3.5-5.5"),
    ("laser", "A laser job (LightBurn, xTool): the head, its beam and the sheet it cuts.",
     "M7 2.5h6v3l-1.5 2h-3L7 5.5zM10 7.5v5.25M3 15.5h14M7.25 13.5 5.75 12M12.75 13.5l1.5-1.5"),
    ("pdf", "A PDF: a page with a label across it (the page's edge stops at the label).",
     "M4.5 10.5v-8h7l4 4v4M4.5 15v2.5h11V15M11.5 2.5v4h4M2.5 10.5h13v4.5h-13z"),
    ("doc", "A document: a page of paragraphs.",
     PAGE + "M7 9.5h6M7 12h6M7 14.5h3.5"),
    ("sheet", "A spreadsheet: a page with a grid.",
     PAGE + "M7 9.5h6v6H7zM7 12.5h6M10 9.5v6"),
    ("slides", "Slides: a screen on its stand, a chart on it.",
     "M3 4h14v9.5H3zM10 13.5V17M7.5 17h5M6 10.5l2.5-2.5 2 2 3.5-3.5"),
    ("text", "Text: a page with a T.",
     PAGE + "M7.5 9.5h5M10 9.5v6"),
    ("code", "Code: a page with angle brackets.",
     PAGE + "M8.5 10 6.5 12l2 2M11.5 10l2 2-2 2"),
    ("image", "A picture: hills and the sun in a frame.",
     "M3 4.5h14v11H3zM3 13l4-4 3 3 2-2 5 5M13.5 6.75a1.25 1.25 0 1 0 0 2.5 1.25 1.25 0 1 0 0-2.5z"),
    ("video", "A video: a frame with a play mark.",
     "M3 4.5h14v11H3zM8.5 7.5v5l4-2.5z"),
    ("archive", "An archive (ZIP): a page with a zipper.",
     PAGE + "M8.5 2.5V4H10v1.5H8.5V7H10v1.5H8.5V10M8 11.5h3v3.5H8z"),
    ("file", "Anything else: a plain page.", PAGE),
]

KEEP = {"part", "asm", "drw", "file"}


def icon_id(kind):
    return "i-" + kind if kind in KEEP else "i-k-" + kind


def symbol(kind, words, path):
    return (f'<!-- {words} -->\n'
            f'<symbol id="{icon_id(kind)}" viewBox="0 0 20 20"><path d="{path}" fill="none" stroke="currentColor" '
            f'stroke-width="1.3" stroke-linejoin="round" stroke-linecap="round" /></symbol>')


def block(indent):
    lines = [BEGIN]
    for kind, words, path in ICONS:
        lines.extend(symbol(kind, words, path).split("\n"))
    lines.append(END)
    return "\n".join(indent + line for line in lines)


def index_text():
    with open(INDEX, encoding="utf-8") as handle:
        text = handle.read()
    begin = text.find(BEGIN)
    end = text.find(END)
    if begin < 0 or end < 0:
        raise SystemExit("index.html has no file kinds markers")
    start = text.rfind("\n", 0, begin) + 1
    indent = text[start:begin]
    return text[:start] + block(indent) + text[end + len(END):]


def sheet():
    sizes = (20, 48, 192)
    cell = 230
    rows = len(ICONS)
    width = 110 + cell * len(sizes) * 2
    height = 40 + rows * cell
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">',
           '<rect width="100%" height="100%" fill="#f4f6f3"/>',
           f'<rect x="{110 + cell * len(sizes)}" y="0" width="{cell * len(sizes)}" height="{height}" fill="#151a17"/>']
    for row, (kind, _, path) in enumerate(ICONS):
        y = 40 + row * cell
        out.append(f'<text x="10" y="{y + 100}" font-family="Segoe UI, sans-serif" font-size="16" fill="#151a17">{kind}</text>')
        for theme, ink, offset in (("light", "#151a17", 110), ("dark", "#e9efe8", 110 + cell * len(sizes))):
            for column, size in enumerate(sizes):
                x = offset + column * cell + (cell - size) / 2
                top = y + (cell - size) / 2
                scale = size / 20
                out.append(f'<g transform="translate({x:g} {top:g}) scale({scale:g})" color="{ink}"><path d="{path}" fill="none" '
                           f'stroke="currentColor" stroke-width="1.3" stroke-linejoin="round" stroke-linecap="round"/></g>')
    out.append("</svg>")
    return "\n".join(out) + "\n"


def main():
    text = index_text()
    if "--check" in sys.argv:
        with open(INDEX, encoding="utf-8") as handle:
            if handle.read() != text:
                print("index.html's file kind icons are not what tools/agent-icon/make_file_icons.py writes")
                return 1
        return 0
    with open(INDEX, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    os.makedirs(os.path.dirname(SHEET), exist_ok=True)
    with open(SHEET, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(sheet())
    print(f"wrote {len(ICONS)} file kind icons into {os.path.relpath(INDEX, ROOT)} and {os.path.relpath(SHEET, ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
