"""从 Assets/icon-main.svg、icon-small.svg 生成全部图标资源。

只依赖 Pillow + 本机 Edge（headless 渲染 SVG）。所有尺寸都从一张 2048px 的母版
LANCZOS 降采样，避免小尺寸各画一遍导致笔画粗细不一致。

产出：
  Assets/app.ico                     16/20/24/32(小图) + 40/48/64/256(大图)
  Assets/app_16/32/64/256.png        关于页、标题栏、磁贴共用
  Assets/Square44x44.png 等 MSIX 磁贴   居中、四周 15% 边距
  Assets/tray/tray_{connected,disconnected,error}_{dark,light}.ico   16/20/24 单色

说明：源 SVG 的画布是 1024，图形本身只占左下约 66%。这里按「墨迹外框」等比裁切并
四周留 5% 空隙再缩放，图形的形状、比例、颜色都不动——不裁的话 16px 下会挤在角落里
只剩一小点。
"""
import os
import re
import struct
import subprocess
import tempfile
import pathlib
import zlib

from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
ASSETS = HERE / "Assets"
TRAY = ASSETS / "tray"

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

MASTER = 2048          # 母版边长
INK_PAD = 0.05         # 裁切后四周留白（占成品边长）

ICO_SMALL = [16, 20, 24, 32]      # 用 icon-small.svg
ICO_LARGE = [40, 48, 64, 256]     # 用 icon-main.svg

# 托盘单色：深色墨（浅色任务栏）/ 白色（深色任务栏）
INK_LIGHT_TASKBAR = "#1A1A1A"
INK_DARK_TASKBAR = "#FFFFFF"

_browser = None


def browser():
    global _browser
    if _browser:
        return _browser
    for p in (EDGE, CHROME):
        if os.path.exists(p):
            _browser = p
            return p
    raise SystemExit("找不到 Edge / Chrome，无法渲染 SVG")


def read_svg(path):
    """取出 <svg> 内部的 defs/图形；顺手去掉 c2pa metadata（只是签名清单，不参与绘制）。"""
    s = pathlib.Path(path).read_text(encoding="utf-8")
    s = re.sub(r"<metadata>.*?</metadata>", "", s, flags=re.S)
    inner = re.sub(r"^.*?<svg[^>]*>", "", s, flags=re.S)
    inner = re.sub(r"</svg>\s*$", "", inner, flags=re.S)
    return inner.strip()


def _shot(inner, box, px, out):
    """把 svg 内部内容按 viewBox=box 渲染成 px×px 的 PNG（透明底）。"""
    vx, vy, vw, vh = box
    html = (
        '<!doctype html><html><head><meta charset="utf-8"><style>'
        "html,body{margin:0;padding:0;background:transparent;overflow:hidden}"
        f"svg{{display:block;width:{px}px;height:{px}px}}"
        "</style></head><body>"
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{vx} {vy} {vw} {vh}">{inner}</svg>'
        "</body></html>"
    )
    tmp = pathlib.Path(tempfile.mkdtemp(prefix="icon_"))
    page = tmp / "a.html"
    page.write_text(html, encoding="utf-8")
    cmd = [
        browser(), "--headless=new", "--disable-gpu", "--hide-scrollbars",
        "--force-device-scale-factor=1", f"--user-data-dir={tmp / 'prof'}",
        f"--window-size={px},{px}", "--default-background-color=00000000",
        f"--screenshot={out}", page.as_uri(),
    ]
    subprocess.run(cmd, capture_output=True, timeout=180)
    return Image.open(out).convert("RGBA")


def ink_box(img):
    b = img.getbbox()
    if b is None:
        raise SystemExit("SVG 渲染为空")
    return b


def square_box(img, pad_ratio=INK_PAD):
    """把墨迹外框扩成正方形，四周加 pad_ratio 的留白，返回 (x,y,w,h)。"""
    x0, y0, x1, y1 = ink_box(img)
    w, h = x1 - x0, y1 - y0
    side = max(w, h) * (1 + 2 * pad_ratio)
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return (cx - side / 2, cy - side / 2, side, side)


def master(inner):
    """先按原画布渲一张，量出墨迹外框，再按外框裁一次。"""
    raw = _shot(inner, (0, 0, 1024, 1024), MASTER, str(pathlib.Path(tempfile.mkdtemp()) / "raw.png"))
    x, y, side, _ = square_box(raw)
    k = 1024 / MASTER
    return _shot(inner, (x * k, y * k, side * k, side * k),
                 MASTER, str(pathlib.Path(tempfile.mkdtemp()) / "crop.png"))


def scale(img, size):
    return img.resize((size, size), Image.LANCZOS) if img.size != (size, size) else img


# ─────────────────────────── ICO ───────────────────────────

def png_bytes(img):
    tmp = pathlib.Path(tempfile.mkdtemp()) / "x.png"
    img.save(tmp, format="PNG", optimize=True)
    return tmp.read_bytes()


def write_ico(path, entries):
    """entries: [(size, PIL.Image)]；256 的目录项宽高记 0，data 统一存 PNG。"""
    entries = sorted(entries, key=lambda e: e[0])
    blobs, dirs = b"", b""
    offset = 6 + 16 * len(entries)
    for size, img in entries:
        data = png_bytes(img)
        w = 0 if size >= 256 else size
        dirs += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    pathlib.Path(path).write_bytes(struct.pack("<HHH", 0, 1, len(entries)) + dirs + blobs)


# ─────────────────────────── 托盘 ───────────────────────────

TRAY_ARCS = [
    ("M220 235.7 A569.3 569.3 0 0 1 789.3 805", 100),
    ("M220 475.7 A329.3 329.3 0 0 1 549.3 805", 100),
]

BADGE_C, BADGE_R = 815, 215      # 感叹号角标的圆心与半径（1024 画布）
SLASH_GAP, SLASH_W = 260, 120    # 斜线两侧挖空的宽度、斜线本身的宽度


def tray_svg(color, state):
    """单色托盘图标：只留 icon-small 里的两层波纹（去掉学士帽、去掉直腿），
    state = connected / disconnected / error。

    角标都用 mask 挖透明缝，不依赖托盘背景色，浅色/深色任务栏都能贴。
    """
    body = "".join(
        f'<path d="{d}" fill="none" stroke="{color}" stroke-width="{w}" '
        f'stroke-linecap="round"/>'
        for d, w in TRAY_ARCS
    )

    mask = ""
    overlay = ""

    if state == "disconnected":
        # 一道斜线压在波纹上，两侧挖 200 单位的透明缝
        mask = ('<mask id="cut"><rect width="1024" height="1024" fill="#fff"/>'
                f'<path d="M170 150 L870 850" stroke="#000" stroke-width="{SLASH_GAP}" '
                'stroke-linecap="round"/></mask>')
        overlay = (f'<path d="M170 150 L870 850" stroke="{color}" stroke-width="{SLASH_W}" '
                   'stroke-linecap="round"/>')
        body = f'<g mask="url(#cut)">{body}</g>'

    elif state == "error":
        # 右下角实心圆，感叹号是挖空的；圆外再挖一圈缝，不压住波纹
        c, r = BADGE_C, BADGE_R
        bar_w, bar_h = int(r * 0.30), int(r * 0.62)
        mask = (f'<mask id="cut"><rect width="1024" height="1024" fill="#fff"/>'
                f'<circle cx="{c}" cy="{c}" r="{int(r * 1.34)}" fill="#000"/>'
                f'<circle cx="{c}" cy="{c}" r="{r}" fill="#fff"/>'
                f'<rect x="{c - bar_w // 2}" y="{c - int(r * 0.46)}" width="{bar_w}" '
                f'height="{bar_h}" rx="{bar_w // 2}" fill="#000"/>'
                f'<circle cx="{c}" cy="{c + int(r * 0.44)}" r="{int(r * 0.16)}" fill="#000"/></mask>')
        overlay = f'<circle cx="{c}" cy="{c}" r="{r}" fill="{color}" mask="url(#cut)"/>'
        body = f'<g mask="url(#cut)">{body}</g>'

    return f"<defs>{mask}</defs>{body}{overlay}"


def mono_master(inner):
    """单色图标母版：按原始 1024 画布渲一次，裁到墨迹外框后再渲一次（一次一状态）。"""
    raw = _shot(inner, (0, 0, 1024, 1024), 1024, str(pathlib.Path(tempfile.mkdtemp()) / "m.png"))
    x, y, side, _ = square_box(raw, pad_ratio=0.04)
    return _shot(inner, (x, y, side, side), 1024, str(pathlib.Path(tempfile.mkdtemp()) / "mc.png"))


# ─────────────────────────── 磁贴 ───────────────────────────

def tile(img, w, h, content=0.70):
    """把图标居中放进 w×h 的透明画布，长边占 content，四周留白。"""
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    side = int(round(min(w, h) * content))
    ic = scale(img, side)
    out.alpha_composite(ic, ((w - side) // 2, (h - side) // 2))
    return out


def main():
    ASSETS.mkdir(exist_ok=True)
    TRAY.mkdir(exist_ok=True)

    print("渲染母版…")
    big = master(read_svg(ASSETS / "icon-main.svg"))
    small = master(read_svg(ASSETS / "icon-small.svg"))

    # app.ico
    entries = [(s, scale(small, s)) for s in ICO_SMALL] + [(s, scale(big, s)) for s in ICO_LARGE]
    write_ico(ASSETS / "app.ico", entries)
    print("app.ico", (ASSETS / "app.ico").stat().st_size, "bytes", [s for s, _ in entries])

    # 单独 PNG
    for name, img, s in (("app_16.png", small, 16), ("app_32.png", small, 32),
                         ("app_64.png", big, 64), ("app_256.png", big, 256)):
        scale(img, s).save(ASSETS / name)
    print("app_16/32/64/256.png")

    # MSIX 磁贴
    tile(scale(big, 256), 44, 44).save(ASSETS / "Square44x44.png")
    tile(scale(big, 256), 44, 44).save(ASSETS / "Square44x44.targetsize-44_altform-unplated.png")
    tile(big, 150, 150).save(ASSETS / "Square150x150.png")
    tile(big, 310, 150).save(ASSETS / "Wide310x150.png")
    tile(big, 256, 256).save(ASSETS / "StoreLogo-256.png")
    print("tiles 44/150/310x150/256")

    # 托盘
    for state in ("connected", "disconnected", "error"):
        for theme, color in (("dark", INK_LIGHT_TASKBAR), ("light", INK_DARK_TASKBAR)):
            m = mono_master(tray_svg(color, state))
            write_ico(TRAY / f"tray_{state}_{theme}.ico", [(s, scale(m, s)) for s in (16, 20, 24)])
    print("tray 6 个 ico（16/20/24）")


if __name__ == "__main__":
    main()
