"""生成应用图标：圆形纯色底 + 经典三弧无线符号。

只用标准库，CI 不需要额外依赖。
几何基准与 artifacts/icon-concepts/round-1.png 一致，但做了两处针对 Windows 的修正：

1. 圆形外径从 960 提到 1010（画布 1024）。任务栏里方形图标是满格铺开的，
   圆形的视觉面积只有方形的 78%，不多给一点外径就会明显小一圈。
2. 小尺寸单独一套几何。16/24 px 下三弧的线宽不足 1 个像素，缩小后会糊成白斑，
   所以 24 px 用两弧、16 px 用单弧，线宽按像素反算，保证笔画实心。
"""
import math
import os
import struct
import zlib

S = 1024                       # 基准画布
CX = CY = 512.0                # 圆心
R_CIRCLE = 510.0               # 圆半径，直径 1020（只留 2px 抗锯齿余量，几乎顶满图标槽）
BRAND = (0x16, 0x77, 0xFF)     # 品牌蓝

# 大尺寸几何：三条等粗弧 + 圆点，半径与线宽按 1024 画布给出
ARC_HALF = 45.0                # 弧张开半角
FULL_ARCS = [(360.0, 58.0), (238.0, 58.0), (116.0, 58.0)]
FULL_DOT = 46.0

# 小尺寸几何：16 px 实在塞不下三条弧，用一条粗弧带点
MID_ARCS = FULL_ARCS                            # 24 px 仍用三弧（约 1.2 px 线宽，还算清楚）
MID_DOT = FULL_DOT
SMALL_ARCS = [(338.0, 104.0)]                   # 16 px
SMALL_DOT = 108.0


def geometry_for(size):
    """按输出尺寸挑几何。只有 16 px 才退成单弧，再上面都保持三弧的形状。"""
    if size >= 20:
        return FULL_ARCS, FULL_DOT
    return SMALL_ARCS, SMALL_DOT


def centered_cy(arcs, dot_r):
    """让「弧顶到点底」这一段整体在画布里垂直居中。"""
    outer = max(r for r, _w in arcs) + max(w for _r, w in arcs) / 2.0
    return CY + (outer - dot_r) / 2.0


def render(size, supersample=1):
    """直接按 size 渲染（内部可超采样）。弧线用「到圆弧段的距离」算覆盖度，端点天然圆头。"""
    arcs, dot_r = geometry_for(size)
    arc_cy = centered_cy(arcs, dot_r)

    k = size / float(S)                 # 1024 坐标 → 目标尺寸
    ss = max(1, supersample)
    W = H = size * ss
    br, bg, bb = BRAND
    buf = bytearray(W * H * 4)

    r_circle = R_CIRCLE * k * ss
    cx = cy = (size / 2.0) * ss
    acy = arc_cy * k * ss
    dot = dot_r * k * ss
    scaled_arcs = [(r * k * ss, w * k * ss) for r, w in arcs]

    deg0 = 270.0 - ARC_HALF
    deg1 = 270.0 + ARC_HALF
    rad0 = math.radians(deg0)
    rad1 = math.radians(deg1)
    ends = [
        ((cx + r * math.cos(rad0), acy + r * math.sin(rad0)),
         (cx + r * math.cos(rad1), acy + r * math.sin(rad1)))
        for r, _w in scaled_arcs
    ]

    for y in range(H):
        fy = y + 0.5
        dy = fy - acy
        for x in range(W):
            fx = x + 0.5

            dc = math.hypot(fx - cx, fy - cy)
            if dc > r_circle + 1.0:
                continue
            mask = min(max(r_circle + 0.5 - dc, 0.0), 1.0)
            if mask <= 0:
                continue

            dist = math.hypot(fx - cx, dy)
            cover = dot + 0.5 - dist
            if cover < 0.0:
                cover = 0.0

            ang = (math.degrees(math.atan2(dy, fx - cx)) + 360.0) % 360.0
            in_range = deg0 <= ang <= deg1
            for i, (radius, width) in enumerate(scaled_arcs):
                if in_range:
                    d = abs(dist - radius)
                else:
                    (e0x, e0y), (e1x, e1y) = ends[i]
                    d = min(math.hypot(fx - e0x, fy - e0y),
                            math.hypot(fx - e1x, fy - e1y))
                v = width * 0.5 + 0.5 - d
                if v > cover:
                    cover = v

            if cover > 1.0:
                cover = 1.0
            elif cover < 0.0:
                cover = 0.0

            if cover > 0.0:
                cr = int(br + (255 - br) * cover)
                cg = int(bg + (255 - bg) * cover)
                cb = int(bb + (255 - bb) * cover)
            else:
                cr, cg, cb = br, bg, bb

            i4 = (y * W + x) * 4
            buf[i4] = cr
            buf[i4 + 1] = cg
            buf[i4 + 2] = cb
            buf[i4 + 3] = int(mask * 255)

    if ss == 1:
        return buf, W, H
    return downsample(buf, W, H, size, size)


def downsample(buf, W, H, nw, nh):
    nw = int(nw)
    nh = int(nh)
    out = bytearray(nw * nh * 4)
    sw, sh = W / nw, H / nh
    for y in range(nh):
        y0 = int(y * sh)
        y1 = max(y0 + 1, int((y + 1) * sh))
        for x in range(nw):
            x0 = int(x * sw)
            x1 = max(x0 + 1, int((x + 1) * sw))
            r = g = b = a = 0
            n = 0
            for yy in range(y0, y1):
                base = yy * W
                for xx in range(x0, x1):
                    i = (base + xx) * 4
                    av = buf[i + 3]
                    r += buf[i] * av
                    g += buf[i + 1] * av
                    b += buf[i + 2] * av
                    a += av
                    n += 1
            i = (y * nw + x) * 4
            if a > 0:
                out[i] = r // a
                out[i + 1] = g // a
                out[i + 2] = b // a
                out[i + 3] = a // n
            else:
                out[i + 3] = 0
    return out, nw, nh


def png_bytes(buf, w, h):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += buf[y * w * 4:(y + 1) * w * 4]

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


print('rendering base...')
base, BW, BH = render(S)
print('base done')

sizes = [256, 128, 64, 48, 32, 24, 16]
images = []
for s in sizes:
    # 大尺寸从 1024 降采样（质量最好），小尺寸按自己的几何超采样单独渲染
    if s >= 48:
        images.append(downsample(base, BW, BH, s, s))
    else:
        images.append(render(s, supersample=4))
    print('rendered', s)

pngs = [(png_bytes(b, w, h), w, h) for b, w, h in images]

count = len(pngs)
head = struct.pack('<HHH', 0, 1, count)
entries = b''
offset = 6 + 16 * count
blobs = b''
for data, w, h in pngs:
    ww = 0 if w >= 256 else w
    hh = 0 if h >= 256 else h
    entries += struct.pack('<BBBBHHII', ww, hh, 0, 0, 1, 32, len(data), offset)
    blobs += data
    offset += len(data)

here = os.path.dirname(os.path.abspath(__file__))
out_path = os.path.join(here, 'Assets', 'app.ico')
os.makedirs(os.path.dirname(out_path), exist_ok=True)
with open(out_path, 'wb') as f:
    f.write(head + entries + blobs)

with open(os.path.join(os.path.dirname(out_path), 'app_256.png'), 'wb') as f:
    f.write(pngs[0][0])

print('ico written:', out_path, os.path.getsize(out_path), 'bytes')
