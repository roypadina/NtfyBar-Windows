#!/usr/bin/env python3
"""Builds src/NtfyBar.App/Resources/NtfyBar.ico from the macOS app icon (macOS only: uses `sips`).

16-48 px frames are classic 32-bit DIBs (safe for every Win32/GDI+ loader, incl. NotifyIcon);
256 px is PNG-compressed, as Windows expects for large icons.

Usage: python3 tools/make-icon.py <AppIcon-1024.png> [out.ico]
"""
import os
import struct
import subprocess
import sys
import tempfile

DIB_SIZES = [16, 20, 24, 32, 40, 48, 64]
PNG_SIZES = [256]


def sips(src, size, fmt, out):
    subprocess.run(["sips", "-z", str(size), str(size), "-s", "format", fmt, src, "--out", out],
                   check=True, stdout=subprocess.DEVNULL)
    with open(out, "rb") as f:
        return f.read()


def dib_from_bmp(bmp, size):
    """sips writes a top-down 32-bit BGRA BMP (V5 header). ICO wants a bottom-up BITMAPINFOHEADER
    with doubled height, the same pixels, then a 1-bit AND mask (all zero: alpha does the work)."""
    offset = struct.unpack_from("<I", bmp, 10)[0]
    width, height, _, bpp = struct.unpack_from("<iiHH", bmp, 18)
    assert width == size and abs(height) == size and bpp == 32, (width, height, bpp)
    row = size * 4
    pixels = bmp[offset:offset + row * size]
    rows = [pixels[i * row:(i + 1) * row] for i in range(size)]
    if height < 0:  # top-down -> bottom-up
        rows.reverse()
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, row * size, 0, 0, 0, 0)
    mask_row = ((size + 31) // 32) * 4
    return header + b"".join(rows) + b"\0" * (mask_row * size)


def main():
    src = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
        os.path.dirname(__file__), "..", "src", "NtfyBar.App", "Resources", "NtfyBar.ico")
    frames = []
    with tempfile.TemporaryDirectory() as tmp:
        for s in DIB_SIZES:
            frames.append((s, dib_from_bmp(sips(src, s, "bmp", os.path.join(tmp, f"{s}.bmp")), s)))
        for s in PNG_SIZES:
            frames.append((s, sips(src, s, "png", os.path.join(tmp, f"{s}.png"))))

    data = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    blobs = b""
    for size, blob in frames:
        dim = 0 if size >= 256 else size
        data += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset + len(blobs))
        blobs += blob
    with open(out, "wb") as f:
        f.write(data + blobs)
    print(f"wrote {out} ({len(frames)} frames)")


if __name__ == "__main__":
    main()
