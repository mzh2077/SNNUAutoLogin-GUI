"""Render shipped ICOs through Windows, checking transparency and visible ink.

Usage: python tools/test_icons.py [publish-directory]
Uses only the standard library; tests the same DrawIconEx path as the shell.
"""
import ctypes as c
from ctypes import wintypes as w
from pathlib import Path
import struct
import sys

user = c.WinDLL("user32", use_last_error=True)
gdi = c.WinDLL("gdi32", use_last_error=True)
user.LoadImageW.argtypes = [w.HINSTANCE, w.LPCWSTR, w.UINT, c.c_int, c.c_int, w.UINT]
user.LoadImageW.restype = w.HANDLE
user.DrawIconEx.argtypes = [w.HDC, c.c_int, c.c_int, w.HICON, c.c_int, c.c_int, w.UINT, w.HBRUSH, w.UINT]
user.DestroyIcon.argtypes = [w.HICON]
gdi.CreateCompatibleDC.argtypes = [w.HDC]
gdi.CreateCompatibleDC.restype = w.HDC
gdi.CreateDIBSection.argtypes = [w.HDC, c.c_void_p, w.UINT, c.POINTER(c.c_void_p), w.HANDLE, w.DWORD]
gdi.CreateDIBSection.restype = w.HBITMAP
gdi.SelectObject.argtypes = [w.HDC, w.HANDLE]
gdi.SelectObject.restype = w.HANDLE
gdi.DeleteObject.argtypes = [w.HANDLE]
gdi.DeleteDC.argtypes = [w.HDC]


def render(path, size, background):
    icon = user.LoadImageW(None, str(path.resolve()), 1, size, size, 0x10)
    if not icon:
        raise OSError(c.get_last_error(), f"Cannot load {path} at {size}px")
    dc = gdi.CreateCompatibleDC(None)
    bits = c.c_void_p()
    # BITMAPINFOHEADER, top-down BGRA pixels.
    info = struct.pack("<IiiHHIIiiII", 40, size, -size, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    bitmap = gdi.CreateDIBSection(dc, info, 0, c.byref(bits), None, 0)
    if not bitmap:
        user.DestroyIcon(icon)
        gdi.DeleteDC(dc)
        raise OSError(c.get_last_error(), "Cannot create icon render surface")
    previous = gdi.SelectObject(dc, bitmap)
    try:
        c.memmove(bits, bytes([background, background, background, 255]) * size * size, size * size * 4)
        if not user.DrawIconEx(dc, 0, 0, icon, size, size, 0, None, 3):
            raise OSError(c.get_last_error(), "DrawIconEx failed")
        raw = c.string_at(bits, size * size * 4)
        return [tuple(raw[i:i + 3]) for i in range(0, len(raw), 4)]
    finally:
        gdi.SelectObject(dc, previous)
        gdi.DeleteObject(bitmap)
        gdi.DeleteDC(dc)
        user.DestroyIcon(icon)


def check(path, size, background):
    pixels = render(path, size, background)
    # An opaque black canvas fails on the light surface, an opaque white canvas
    # fails on the dark surface. The corners must remain the caller's background.
    assert all(max(abs(v - background) for v in pixels[i]) <= 10
               for i in [0, size - 1, size * (size - 1), size * size - 1]), (path, size, "opaque canvas")
    visible = sum(max(abs(channel - background) for channel in pixel) > 35 for pixel in pixels)
    assert size * size * 0.02 < visible < size * size * 0.9, (path, size, "missing or solid-square ink", visible)
    print(f"PASS {path.name} {size}px on {background}")


root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
for size in [16, 20, 24, 32, 40, 48, 64, 256]:
    for bg in [32, 245]:
        check(root / "Assets/app.ico", size, bg)
for state in ["connected", "disconnected", "error"]:
    for theme, bg in [("dark", 245), ("light", 32)]:
        for size in [16, 20, 24]:
            check(root / f"Assets/tray/tray_{state}_{theme}.ico", size, bg)
            assert render(root / f"Assets/tray/tray_{state}_{theme}.ico", size, bg) == render(root / "Assets/app.ico", size, bg), (state, theme, size, "tray differs from window icon")
            print(f"PASS tray/window identity {state} {theme} {size}px")
