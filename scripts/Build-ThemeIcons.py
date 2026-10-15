"""Build small, antialiased PNG theme glyphs without an SVG runtime dependency."""
from pathlib import Path
import math
import struct
import zlib

DESTINATION = Path(__file__).resolve().parents[1] / "src/PolyMount.Tray/resources"


def glyph(name, points, color, stroke):
    size, samples = 32, 4
    pixels = bytearray()
    for y in range(size):
        pixels.append(0)
        for x in range(size):
            coverage = 0
            for sy in range(samples):
                for sx in range(samples):
                    px, py = x + (sx + .5) / samples, y + (sy + .5) / samples
                    for (ax, ay), (bx, by) in zip(points, points[1:]):
                        dx, dy = bx - ax, by - ay
                        t = max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
                        if math.hypot(px - ax - t * dx, py - ay - t * dy) <= stroke / 2:
                            coverage += 1
                            break
            pixels.extend((*color, round(255 * coverage / (samples * samples))))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(pixels)) + chunk(b"IEND", b"")
    (DESTINATION / name).write_bytes(png)


if __name__ == "__main__":
    if not DESTINATION.is_dir():
        raise RuntimeError("Theme resource directory does not exist")
    glyph("check.png", [(8, 16), (14, 22), (24, 10)], (255, 255, 255), 3)
    glyph("chevron-left.png", [(20, 8), (12, 16), (20, 24)], (82, 103, 134), 2.5)
    glyph("chevron-right.png", [(12, 8), (20, 16), (12, 24)], (82, 103, 134), 2.5)
