#!/usr/bin/env python3
"""Draws IDEA Armory's four File Explorer badges (icon overlays) into native/badges/.

ArmoryBadges.dll carries them as its icon resources 1 to 4, in this order (docs/agent/EXPLORER.md):

    attention.ico  a red triangle with an exclamation mark: can't be uploaded, can't be read,
                   changed without a check out, or a kept copy
    mine.ico       a blue disc with a pencil: checked out by you on this computer (changed or
                   not), or new here and not in Armory yet
    locked.ico     an amber padlock: checked out by someone else, or by you on another computer
    synced.ico     a green disc with a check mark: up to date, checked out by nobody

Each badge has its own SHAPE, not only its own color (a triangle, a disc with a pencil, a padlock,
a disc with a check), so it reads for color-blind students, as the tray icons do
(make_icon.py). Explorer draws an overlay image over the whole file icon, so each image is
transparent except for the badge in its lower-left corner, with a dark rim that keeps it
visible on white paper and on dark folders alike.

Standard library only, and deterministic, like make_icon.py:

    python3 tools/agent-icon/make_badges.py

Sizes: 16, 20, 24, 32, 40, 48 and 64 px as 32-bit BMP entries and 256 px as a PNG entry, so
every icon size and display scaling Explorer asks for has its own drawing.
"""

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from make_icon import bmp, ico, png, render, segment_distance, triangle_distance  # noqa: E402

SIZES = (16, 20, 24, 32, 40, 48, 64, 256)
BADGES = ("attention", "mine", "locked", "synced")

RIM = (16, 19, 18)
WHITE = (250, 252, 250)
RED = (214, 48, 49)
BLUE = (38, 118, 216)
AMBER = (240, 168, 24)
GREEN = (36, 150, 72)
STEEL = (66, 72, 70)
# The badge sits in the lower-left corner, as Windows' own shortcut arrow does.
CX, CY, R = 0.31, 0.69, 0.235


def rim_width(size):
    return 0.075 if size <= 20 else 0.06 if size <= 48 else 0.045


def stroke_width(size):
    return 0.10 if size <= 20 else 0.085 if size <= 32 else 0.07


def disc(x, y, size, fill, glyph):
    """A filled disc with a dark rim and a white glyph, or None outside it."""
    r = math.hypot(x - CX, y - CY)
    if r > R + rim_width(size):
        return None
    if r > R:
        return RIM
    return WHITE if glyph(x - CX, y - CY, size) else fill


def pencil(dx, dy, size):
    """A pencil from lower left to upper right: a body, then a pointed tip."""
    stroke = stroke_width(size) * 1.25
    # Along the pencil (u) and across it (v), rotated 45 degrees.
    u = (dx - dy) / math.sqrt(2)
    v = (dx + dy) / math.sqrt(2)
    if -0.12 <= u <= 0.10 and abs(v) <= stroke / 2:
        return True
    # The tip narrows to a point at the lower left.
    if -0.19 <= u < -0.12 and abs(v) <= (stroke / 2) * (u + 0.19) / 0.07:
        return True
    return False


def check(dx, dy, size):
    stroke = stroke_width(size)
    return (segment_distance(dx, dy, -0.12, 0.0, -0.035, 0.09) <= stroke / 2
            or segment_distance(dx, dy, -0.035, 0.09, 0.13, -0.09) <= stroke / 2)


def attention(x, y, size):
    edge = rim_width(size)
    points = ((CX + 0.02, CY - R - 0.03), (CX + R + 0.04, CY + R), (CX - R, CY + R))
    d = triangle_distance(x, y, points)
    if d < -edge:
        return None
    if d < 0:
        return RIM
    stroke = stroke_width(size)
    mx = CX + 0.02
    if segment_distance(x, y, mx, CY - 0.10, mx, CY + 0.06) <= stroke / 2 or math.hypot(x - mx, y - (CY + 0.15)) <= stroke * 0.62:
        return WHITE
    return RED


def locked(x, y, size):
    edge = rim_width(size)
    stroke = stroke_width(size)
    # The body: a rounded rectangle in the lower part of the badge, with a dark keyhole.
    left, right, top, bottom, corner = CX - 0.19, CX + 0.19, CY - 0.02, CY + 0.22, 0.04
    qx = min(max(x, left + corner), right - corner)
    qy = min(max(y, top + corner), bottom - corner)
    body = math.hypot(x - qx, y - qy) - corner
    if body <= 0:
        if math.hypot(x - CX, y - (CY + 0.08)) <= stroke * 0.55 or (abs(x - CX) <= stroke * 0.25 and CY + 0.08 <= y <= CY + 0.16):
            return RIM
        return AMBER
    if body <= edge:
        return RIM
    # The shackle: a steel half ring above the body, its legs going down into it.
    ring_r, ring_y, half = 0.12, CY - 0.07, stroke * 0.6
    if y <= ring_y:
        shackle = abs(math.hypot(x - CX, y - ring_y) - ring_r)
    elif y <= top + corner:
        shackle = min(abs(x - (CX - ring_r)), abs(x - (CX + ring_r)))
    else:
        return None
    if shackle <= half:
        return STEEL
    return None


def paint(badge):
    def color(x, y, size):
        if badge == "attention":
            return attention(x, y, size)
        if badge == "mine":
            return disc(x, y, size, BLUE, pencil)
        if badge == "locked":
            return locked(x, y, size)
        if badge == "synced":
            return disc(x, y, size, GREEN, check)
        raise ValueError(badge)
    return color


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    target = os.path.normpath(os.path.join(here, "..", "..", "native", "badges"))
    preview = os.environ.get("ARMORY_ICON_PREVIEW")
    for badge in BADGES:
        color = paint(badge)
        images = []
        for size in SIZES:
            rows = render(size, color)
            images.append((size, png(rows) if size >= 256 else bmp(rows)))
        path = os.path.join(target, badge + ".ico")
        with open(path, "wb") as out:
            out.write(ico(images))
        if preview:
            root, _ = os.path.splitext(preview)
            for size in (16, 48):
                with open(f"{root}-{badge}-{size}.png", "wb") as out:
                    out.write(png(render(size, color)))
        print(f"wrote {path} ({', '.join(str(s) for s in SIZES)} px)")


if __name__ == "__main__":
    main()
