import math, struct, zlib, os

S = 1024  # 超采样画布

def lerp(a, b, t):
    return a + (b - a) * t

# ── 圆角矩形 + 渐变 + wifi ──
def render(size):
    W = H = size
    r = size * 0.5                    # 圆角半径
    cxp, cyp = W * 0.5, H * 0.70       # wifi 原点
    arcs = [(0.365, 0.044), (0.245, 0.044), (0.125, 0.044)]
    dot_r = 0.035

    top = (0x3A, 0x93, 0xF2)
    bot = (0x0A, 0x5B, 0xC2)

    buf = bytearray(W * H * 4)

    for y in range(H):
        fy = y + 0.5
        for x in range(W):
            fx = x + 0.5

            # --- 圆角矩形遮罩（1px 抗锯齿）---
            qx = abs(fx - W / 2) - (W / 2 - r)
            qy = abs(fy - H / 2) - (H / 2 - r)
            ox, oy = max(qx, 0.0), max(qy, 0.0)
            d_rect = math.hypot(ox, oy) + min(max(qx, qy), 0.0) - r
            mask = min(max(0.5 - d_rect, 0.0), 1.0)
            if mask <= 0:
                continue

            # --- 渐变背景 ---
            t = (fx / W) * 0.45 + (fy / H) * 0.55
            cr = int(lerp(top[0], bot[0], t))
            cg = int(lerp(top[1], bot[1], t))
            cb = int(lerp(top[2], bot[2], t))

            # --- wifi 图形 ---
            dx = fx - cxp
            dy = cyp - fy          # 向上为正
            dist = math.hypot(dx, dy)
            ang = math.degrees(math.atan2(dy, dx)) if dist > 0.0001 else 0.0

            white = 0.0

            # 底部圆点
            if dist < dot_r * size:
                white = max(white, min(max(0.5 - (dist - dot_r * size), 0.0), 1.0))

            # 三圈圆弧：角度 45°~135°
            if 30.0 <= ang <= 150.0:
                # 角度边缘羽化
                a_fade = min((ang - 30.0) / 6.0, (150.0 - ang) / 6.0, 1.0)
                a_fade = max(a_fade, 0.0)
                for R, lw in arcs:
                    rr = R * size
                    half = lw * size / 2.0
                    dd = abs(dist - rr)
                    core = min(max(half - dd + 0.5, 0.0), 1.0)
                    if core > 0:
                        white = max(white, core * a_fade)

            if white > 0:
                cr = int(lerp(cr, 255, white))
                cg = int(lerp(cg, 255, white))
                cb = int(lerp(cb, 255, white))

            i = (y * W + x) * 4
            buf[i] = cr
            buf[i + 1] = cg
            buf[i + 2] = cb
            buf[i + 3] = int(mask * 255)
    return buf, W, H

def downsample(buf, W, H, nw, nh):
    nw = int(nw); nh = int(nh)
    out = bytearray(nw * nh * 4)
    sw, sh = W / nw, H / nh
    for y in range(nh):
        y0 = int(y * sh); y1 = max(y0 + 1, int((y + 1) * sh))
        for x in range(nw):
            x0 = int(x * sw); x1 = max(x0 + 1, int((x + 1) * sw))
            r = g = b = a = 0; n = 0
            for yy in range(y0, y1):
                base = yy * W
                for xx in range(x0, x1):
                    i = (base + xx) * 4
                    av = buf[i + 3]
                    r += buf[i] * av; g += buf[i + 1] * av; b += buf[i + 2] * av
                    a += av; n += 1
            i = (y * nw + x) * 4
            if a > 0:
                out[i] = r // a; out[i + 1] = g // a; out[i + 2] = b // a
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

# ICO
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

out_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Assets', 'app.ico')
os.makedirs(os.path.dirname(out_path), exist_ok=True)
with open(out_path, 'wb') as f:
    f.write(head + entries + blobs)

# 同时导出 256 预览图
with open(os.path.join(os.path.dirname(out_path), 'app_256.png'), 'wb') as f:
    f.write(pngs[0][0])

print('ico written:', out_path, os.path.getsize(out_path), 'bytes')

