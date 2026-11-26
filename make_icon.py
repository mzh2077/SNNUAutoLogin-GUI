"""生成应用图标：圆形纯色底 + 经典三弧无线符号。

只用标准库，CI 不需要额外依赖。
设计参数与 artifacts/icon-concepts/round-1.png 一致。
"""
import math
import os
import struct
import zlib

S = 1024                       # 超采样画布
CX = CY = 512.0                # 圆心（也是画布中心）
R_CIRCLE = 480.0               # 圆形半径，直径 960
BRAND = (0x16, 0x77, 0xFF)     # 品牌蓝

ARC_CY = 674.25                # 三条弧与圆点共用的圆心（偏下，给上方留白）
ARC_HALF = 45.0                # 弧张开半角
ARCS = [(340.0, 49.0), (229.0, 49.0), (118.0, 49.0)]   # (半径, 线宽)
DOT_R = 40.0                   # 底部圆点半径

DEG0 = 270.0 - ARC_HALF
DEG1 = 270.0 + ARC_HALF
_RAD0 = math.radians(DEG0)
_RAD1 = math.radians(DEG1)
_ENDS = [
    ((CX + r * math.cos(_RAD0), ARC_CY + r * math.sin(_RAD0)),
     (CX + r * math.cos(_RAD1), ARC_CY + r * math.sin(_RAD1)))
    for r, _w in ARCS
]


def render(size):
    W = H = size
    br, bg, bb = BRAND
    buf = bytearray(W * H * 4)

    for y in range(H):
        fy = y + 0.5
        for x in range(W):
            fx = x + 0.5

            dc = math.hypot(fx - CX, fy - CY)
            if dc > R_CIRCLE + 1.0:
                continue
            mask = min(max(R_CIRCLE + 0.5 - dc, 0.0), 1.0)
            if mask <= 0:
                continue

            # 前景覆盖度：弧线用“到圆弧段的距离”，端点自然成为圆头
            dx = fx - CX
            dy = fy - ARC_CY
            dist = math.hypot(dx, dy)

            cover = DOT_R + 0.5 - dist
            if cover < 0.0:
                cover = 0.0

            ang = (math.degrees(math.atan2(dy, dx)) + 360.0) % 360.0
            in_range = DEG0 <= ang <= DEG1
            for i, (radius, width) in enumerate(ARCS):
                if in_range:
                    d = abs(dist - radius)
                else:
                    (e0x, e0y), (e1x, e1y) = _ENDS[i]
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
    return buf, W, H


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
    if s == S:
        images.append((base, S, S))
    else:
        images.append(downsample(base, BW, BH, s, s))
    print('scaled', s)

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
