#!/usr/bin/env python3
"""Draws the IDEA Armory app icon and its tray icons into src/Armory.Agent/Assets/.

The mark is a heater shield (a piece of plate armor) on a rounded dark tile: a steel rim
and a green field with a light green chevron, which also reads as the "A" of Armory. It stays a
recognizable shield at 16 px in the notification area.

armory.ico is the app icon. tray-<state>.ico are the notification-area icons for each sync
state the window shows (AgentView.sync.state). Each state has its own badge SHAPE, not only
its own color, so it reads for color-blind students and on any taskbar:

    synced     the plain shield
    syncing    a blue disc with a circular arrow
    paused     a light disc with two pause bars
    offline    the shield turned gray, with a gray disc crossed by a slash
    attention  an amber triangle with an exclamation mark

Standard library only. The output is deterministic, so rerunning it after a change gives a
reviewable diff of the script and a byte-identical icon when nothing changed:

    python3 tools/agent-icon/make_icon.py

Sizes: 16, 32 and 48 px as 32-bit BMP entries (the most compatible form for the tray and
Explorer), 256 px as a PNG entry (what Windows expects at that size). The tray icons carry
16, 20, 24, 32, 40 and 48 px, the notification-area size at 100% to 300% display scaling.
"""

import math
import os
import struct
import zlib

SIZES = (16, 32, 48, 256)
TRAY_SIZES = (16, 20, 24, 32, 40, 48)
TRAY_STATES = ("synced", "syncing", "paused", "offline", "attention")
SUPERSAMPLE = 4

# The IDEA emblem's palette (the window's header uses the same emblem): a near-black
# tile, a steel rim, a green plate and a light green chevron.
TILE = (16, 19, 18)
STEEL = (203, 210, 196)
GREEN_TOP = (60, 122, 76)
GREEN_BOTTOM = (31, 74, 44)
LIGHT = (159, 226, 154)
# Tray badges.
WHITE = (245, 248, 246)
BLUE = (52, 140, 230)
PAUSE = (205, 212, 200)
GRAY = (122, 128, 124)
AMBER = (245, 183, 49)
BADGE_X, BADGE_Y, BADGE_R = 0.70, 0.70, 0.29


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


def gray(c):
    """The same lightness without color (the offline shield)."""
    v = round(0.30 * c[0] + 0.59 * c[1] + 0.11 * c[2])
    return (v, v, v)


def triangle_distance(x, y, points):
    """Smallest signed distance from (x, y) to the triangle's edges, positive inside."""
    d = float("inf")
    for i in range(3):
        ax, ay = points[i]
        bx, by = points[(i + 1) % 3]
        cx, cy = points[(i + 2) % 3]
        nx, ny = by - ay, ax - bx
        length = math.hypot(nx, ny)
        nx, ny = nx / length, ny / length
        if (cx - ax) * nx + (cy - ay) * ny < 0:
            nx, ny = -nx, -ny
        d = min(d, (x - ax) * nx + (y - ay) * ny)
    return d


def badge_at(x, y, size, state):
    """The state badge's color at (x, y), or None where the shield shows through."""
    edge = 0.075 if size <= 20 else 0.06
    if state == "attention":
        points = ((0.71, 0.36), (0.985, 0.975), (0.435, 0.975))
        d = triangle_distance(x, y, points)
        if d < -edge:
            return None
        if d < 0:
            return TILE
        stroke = 0.085 if size <= 20 else 0.07
        if segment_distance(x, y, 0.71, 0.57, 0.71, 0.76) <= stroke / 2 or math.hypot(x - 0.71, y - 0.87) <= stroke * 0.62:
            return TILE
        return AMBER
    r = math.hypot(x - BADGE_X, y - BADGE_Y)
    if r > BADGE_R + edge:
        return None
    if r > BADGE_R:
        return TILE
    stroke = 0.085 if size <= 20 else 0.07
    dx, dy = x - BADGE_X, y - BADGE_Y
    if state == "syncing":
        # A circular arrow: a ring open at the upper right, with an arrowhead at the gap.
        ring = 0.135
        angle = math.degrees(math.atan2(-dy, dx)) % 360
        if abs(r - ring) <= stroke / 2 and not (0 <= angle <= 70):
            return WHITE
        head = ((BADGE_X + ring - 0.085, BADGE_Y - 0.005), (BADGE_X + ring + 0.085, BADGE_Y - 0.005), (BADGE_X + ring, BADGE_Y + 0.085))
        if triangle_distance(x, y, head) >= 0:
            return WHITE
        return BLUE
    if state == "paused":
        bar = stroke * 0.95
        if abs(dy) <= 0.12 and (abs(dx + 0.065) <= bar / 2 or abs(dx - 0.065) <= bar / 2):
            return TILE
        return PAUSE
    if state == "offline":
        if segment_distance(x, y, BADGE_X - 0.13, BADGE_Y + 0.13, BADGE_X + 0.13, BADGE_Y - 0.13) <= stroke / 2:
            return WHITE
        return GRAY
    raise ValueError(state)


def tray_color_at(x, y, size, state):
    if state != "synced":
        badge = badge_at(x, y, size, state)
        if badge is not None:
            return badge
    c = color_at(x, y, size)
    if c is not None and state == "offline":
        return gray(c)
    return c


def render(size, color=None):
    """Returns rows of RGBA tuples, top row first, anti-aliased by supersampling."""
    color = color or color_at
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
                    c = color(x, y, size)
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
    assets = os.path.normpath(os.path.join(here, "..", "..", "src", "Armory.Agent", "Assets"))
    target = os.path.join(assets, "armory.ico")
    images = []
    for size in SIZES:
        rows = render(size)
        images.append((size, png(rows) if size >= 256 else bmp(rows)))
    os.makedirs(assets, exist_ok=True)
    with open(target, "wb") as out:
        out.write(ico(images))
    preview = os.environ.get("ARMORY_ICON_PREVIEW")
    if preview:
        with open(preview, "wb") as out:
            out.write(png(render(256)))
    print(f"wrote {target} ({', '.join(str(s) for s in SIZES)} px)")
    for state in TRAY_STATES:
        def paint(x, y, size, state=state):
            return tray_color_at(x, y, size, state)
        tray = os.path.join(assets, f"tray-{state}.ico")
        with open(tray, "wb") as out:
            out.write(ico([(size, bmp(render(size, paint))) for size in TRAY_SIZES]))
        if preview:
            root, _ = os.path.splitext(preview)
            for size in (16, 48):
                with open(f"{root}-{state}-{size}.png", "wb") as out:
                    out.write(png(render(size, paint)))
        print(f"wrote {tray} ({', '.join(str(s) for s in TRAY_SIZES)} px)")


if __name__ == "__main__":
    main()
