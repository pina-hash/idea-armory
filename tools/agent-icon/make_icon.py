#!/usr/bin/env python3
"""Draws the IDEA Armory tray and app icon and writes src/Armory.Agent/Assets/armory.ico.

The mark is a heater shield (a piece of plate armor) on a rounded dark tile: a steel rim
and a green field with a light green chevron, which also reads as the "A" of Armory. It stays a
recognizable shield at 16 px in the notification area.

Standard library only. The output is deterministic, so rerunning it after a change gives a
reviewable diff of the script and a byte-identical icon when nothing changed:

    python3 tools/agent-icon/make_icon.py

Sizes: 16, 32 and 48 px as 32-bit BMP entries (the most compatible form for the tray and
Explorer), 256 px as a PNG entry (what Windows expects at that size).
"""

import math
import os
import struct
import zlib

SIZES = (16, 32, 48, 256)
SUPERSAMPLE = 4

# The IDEA emblem's palette (the window's header uses the same emblem): a near-black
# tile, a steel rim, a green plate and a light green chevron.
TILE = (16, 19, 18)
STEEL = (203, 210, 196)
GREEN_TOP = (60, 122, 76)
GREEN_BOTTOM = (31, 74, 44)
LIGHT = (159, 226, 154)


def tile(x, y):
    """Rounded square background covering the whole canvas (unit coordinates)."""
    r = 0.18
    m = 0.02
    cx = min(max(x, m + r), 1 - m - r)
    cy = min(max(y, m + r), 1 - m - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r and m <= x <= 1 - m and m <= y <= 1 - m


def shield_half_width(y, top, waist, bottom, width):
    """Half width of a heater shield at height y, or -1 outside it."""
    if y < top or y > bottom:
        return -1.0
    if y <= waist:
        return width
    t = (y - waist) / (bottom - waist)
    # (1 - t^2) joins the straight sides smoothly and ends in a point.
    return width * (1.0 - t * t) ** 0.85


def inside_shield(x, y, inset=0.0):
    top, waist, bottom, width = 0.17 + inset, 0.50, 0.88 - inset * 1.6, 0.31 - inset
    w = shield_half_width(y, top, waist, bottom, width)
    return w >= 0 and abs(x - 0.5) <= w


def segment_distance(x, y, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - (ax + t * dx), y - (ay + t * dy))


def color_at(x, y, size):
    if not tile(x, y):
        return None
    # Thicker strokes at small sizes keep the shield legible in the tray.
    rim = 0.065 if size <= 16 else 0.055 if size <= 32 else 0.045
    band = 0.11 if size <= 16 else 0.09 if size <= 48 else 0.075
    if inside_shield(x, y, rim):
        # A heraldic chevron, which also reads as the "A" of Armory.
        if segment_distance(x, y, 0.30, 0.70, 0.5, 0.33) <= band / 2 or segment_distance(x, y, 0.5, 0.33, 0.70, 0.70) <= band / 2:
            return LIGHT
        t = min(max((y - 0.17) / 0.71, 0.0), 1.0)
        return tuple(round(a + (b - a) * t) for a, b in zip(GREEN_TOP, GREEN_BOTTOM))
    if inside_shield(x, y):
        return STEEL
    return TILE


def render(size):
    """Returns rows of RGBA tuples, top row first, anti-aliased by supersampling."""
    rows = []
    n = SUPERSAMPLE
    for py in range(size):
        row = []
        for px in range(size):
            r = g = b = a = 0
            for sy in range(n):
                for sx in range(n):
                    x = (px + (sx + 0.5) / n) / size
                    y = (py + (sy + 0.5) / n) / size
                    c = color_at(x, y, size)
                    if c is not None:
                        r += c[0]
                        g += c[1]
                        b += c[2]
                        a += 1
            if a == 0:
                row.append((0, 0, 0, 0))
            else:
                row.append((round(r / a), round(g / a), round(b / a), round(255 * a / (n * n))))
        rows.append(row)
    return rows


def png(rows):
    size = len(rows)
    raw = b"".join(b"\x00" + bytes(v for p in row for v in p) for row in rows)

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def bmp(rows):
    size = len(rows)
    info = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = b"".join(bytes((p[2], p[1], p[0], p[3])) for row in reversed(rows) for p in row)
    mask_row = ((size + 31) // 32) * 4
    mask = b"\x00" * (mask_row * size)
    return info + pixels + mask


def ico(images):
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return header + entries + b"".join(data for _, data in images)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    target = os.path.normpath(os.path.join(here, "..", "..", "src", "Armory.Agent", "Assets", "armory.ico"))
    images = []
    for size in SIZES:
        rows = render(size)
        images.append((size, png(rows) if size >= 256 else bmp(rows)))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "wb") as out:
        out.write(ico(images))
    preview = os.environ.get("ARMORY_ICON_PREVIEW")
    if preview:
        with open(preview, "wb") as out:
            out.write(png(render(256)))
    print(f"wrote {target} ({', '.join(str(s) for s in SIZES)} px)")


if __name__ == "__main__":
    main()
