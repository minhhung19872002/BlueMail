"""Draws the BlueMail PWA icons (web/public/icons) without image libraries: a blue rounded square with a white
envelope. Re-run after changing the colours. Usage: python scripts/gen-icons.py"""
import pathlib
import struct
import zlib

OUT = pathlib.Path(__file__).resolve().parent.parent / "web" / "public" / "icons"
BLUE, WHITE = (11, 87, 208), (255, 255, 255)


def png(size, pixel):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            raw.extend(pixel(x / size, y / size))

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 4 + 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def icon(margin, radius):
    def pixel(u, v):
        # rounded square
        cu, cv = min(max(u, margin + radius), 1 - margin - radius), min(max(v, margin + radius), 1 - margin - radius)
        inside = (u - cu) ** 2 + (v - cv) ** 2 <= radius ** 2 and margin <= u <= 1 - margin and margin <= v <= 1 - margin
        if not inside:
            return (0, 0, 0, 0)
        # envelope: body 0.27..0.73 x 0.34..0.66, flap as a V from the top corners to the centre
        if 0.27 <= u <= 0.73 and 0.34 <= v <= 0.66:
            border = min(u - 0.27, 0.73 - u, v - 0.34, 0.66 - v) < 0.025
            flap = abs(v - (0.34 + 0.826 * (0.23 - abs(u - 0.5)))) < 0.022  # V from the top corners to the centre
            return WHITE + (255,) if border or flap else BLUE + (255,)
        return BLUE + (255,)
    return pixel


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "icon-192.png").write_bytes(png(192, icon(0.04, 0.18)))
    (OUT / "icon-512.png").write_bytes(png(512, icon(0.04, 0.18)))
    (OUT / "maskable-512.png").write_bytes(png(512, icon(0.0, 0.0)))  # full bleed: the platform applies its own mask
    (OUT / "apple-touch-icon.png").write_bytes(png(180, icon(0.0, 0.0)))
    print("icons written to", OUT)


if __name__ == "__main__":
    main()
