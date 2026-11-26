"""窗口探针：枚举顶层窗口、量标题栏几何、抓图。

PowerShell 的 Add-Type 在本机被安全策略拦了，这里用 ctypes 直连 user32。
用法：
    python tools/win_probe.py list                 列出所有可见顶层窗口
    python tools/win_probe.py rect <pid|name>      打印窗口矩形与 DPI
    python tools/win_probe.py shot <pid|name> <out.png> [right] [height]
"""
import ctypes
import ctypes.wintypes as wt
import sys

user32 = ctypes.WinDLL("user32", use_last_error=True)
try:
    user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # PER_MONITOR_AWARE_V2
except Exception:
    pass

EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def windows():
    out = []

    def cb(hwnd, _):
        n = user32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 2)
        user32.GetWindowTextW(hwnd, buf, n + 2)
        pid = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        r = wt.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(r))
        out.append({
            "hwnd": hwnd,
            "pid": pid.value,
            "title": buf.value,
            "visible": bool(user32.IsWindowVisible(hwnd)),
            "min": bool(user32.IsIconic(hwnd)),
            "rect": (r.left, r.top, r.right - r.left, r.bottom - r.top),
        })
        return True

    user32.EnumWindows(EnumProc(cb), 0)
    return out


def find(key):
    ws = windows()
    if key.isdigit():
        return [w for w in ws if w["pid"] == int(key)]
    k = key.lower()
    return [w for w in ws if k in w["title"].lower()]


def dpi_of(hwnd):
    try:
        return user32.GetDpiForWindow(hwnd)
    except Exception:
        return 96


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return
    cmd = sys.argv[1]

    if cmd == "list":
        for w in windows():
            if not w["visible"]:
                continue
            x, y, cw, ch = w["rect"]
            if cw < 120 or ch < 60:
                continue
            print(f'pid={w["pid"]:<6} {cw:>5}x{ch:<5} at {x},{y:<5} dpi={dpi_of(w["hwnd"]):<4} {w["title"][:60]}')

    elif cmd == "rect":
        for w in find(sys.argv[2]):
            print(w)
            print("  dpi =", dpi_of(w["hwnd"]))

    elif cmd == "shot":
        from PIL import ImageGrab

        key, out = sys.argv[2], sys.argv[3]
        right = int(sys.argv[4]) if len(sys.argv) > 4 else 400
        height = int(sys.argv[5]) if len(sys.argv) > 5 else 90
        zoom = int(sys.argv[6]) if len(sys.argv) > 6 else 3
        ws = [w for w in find(key) if w["visible"] and not w["min"] and w["rect"][2] > 200]
        if not ws:
            print("no window")
            return
        w = ws[0]
        x, y, cw, ch = w["rect"]
        print(f"window {cw}x{ch} at {x},{y} dpi={dpi_of(w['hwnd'])}")
        im = ImageGrab.grab(bbox=(x + cw - right, y, x + cw, y + height))
        im = im.resize((im.width * zoom, im.height * zoom))
        im.save(out)
        print("saved", out, im.size)

    else:
        print(__doc__)


if __name__ == "__main__":
    main()
