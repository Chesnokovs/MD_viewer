"""Generate assets/app.ico without third-party image libraries.

Draws a rounded-square badge with a white "M" glyph, renders it at several
sizes with 4x supersampling, and packs PNG-encoded frames into an .ico file.
"""
import struct
import zlib

ACCENT_TOP = (0x5B, 0x8D, 0xEF)
ACCENT_BOTTOM = (0x3B, 0x5B, 0xDB)
GLYPH = (0xFF, 0xFF, 0xFF)
SS = 4  # supersampling factor
SIZES = (256, 128, 64, 48, 32, 16)

# "M" outline in a 0..1 unit box, y grows downward.
M_POLY = [
    (0.16, 0.78), (0.16, 0.22), (0.30, 0.22), (0.50, 0.52),
    (0.70, 0.22), (0.84, 0.22), (0.84, 0.78), (0.70, 0.78),
    (0.70, 0.44), (0.555, 0.66), (0.445, 0.66), (0.30, 0.44),
    (0.30, 0.78),
]


def point_in_poly(x, y, poly):
    inside = False
    n = len(poly)
    for i in range(n):
        x0, y0 = poly[i]
        x1, y1 = poly[(i + 1) % n]
        if (y0 > y) != (y1 > y):
            xr = x0 + (y - y0) * (x1 - x0) / (y1 - y0)
            if x < xr:
                inside = not inside
    return inside


def in_rounded_rect(x, y, size, radius, inset):
    lo, hi = inset, size - inset
    if x < lo or y < lo or x > hi or y > hi:
        return False
    cx = min(max(x, lo + radius), hi - radius)
    cy = min(max(y, lo + radius), hi - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius


def render(size):
    big = size * SS
    radius = big * 0.22
    inset = big * 0.03
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            r = g = b = a = 0
            for sy in range(SS):
                for sx in range(SS):
                    x = px * SS + sx + 0.5
                    y = py * SS + sy + 0.5
                    if not in_rounded_rect(x, y, big, radius, inset):
                        continue
                    t = y / big
                    br = round(ACCENT_TOP[0] + (ACCENT_BOTTOM[0] - ACCENT_TOP[0]) * t)
                    bg = round(ACCENT_TOP[1] + (ACCENT_BOTTOM[1] - ACCENT_TOP[1]) * t)
                    bb = round(ACCENT_TOP[2] + (ACCENT_BOTTOM[2] - ACCENT_TOP[2]) * t)
                    if point_in_poly(x / big, y / big, M_POLY):
                        br, bg, bb = GLYPH
                    r += br
                    g += bg
                    b += bb
                    a += 255
            n = SS * SS
            if a == 0:
                row += b"\x00\x00\x00\x00"
            else:
                cover = a / (255 * n)
                opaque = a / 255
                row += bytes((
                    round(r / opaque), round(g / opaque), round(b / opaque),
                    round(255 * cover),
                ))
        rows.append(bytes(row))
    return rows


def png(rows, size):
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c))

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def main():
    frames = [(s, png(render(s), s)) for s in SIZES]
    out = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries, blobs = b"", b""
    for size, data in frames:
        entries += struct.pack(
            "<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    with open("assets/app.ico", "wb") as f:
        f.write(out + entries + blobs)
    print("assets/app.ico", len(out + entries + blobs), "bytes")


main()
