# -*- coding: utf-8 -*-
"""
ROSHIQ: генератор собственной нарисованной карты для истории (без графики Valve).
Всё процедурное: рельеф с уступами и скальными склонами, земля с фактурой, река, дороги, тропы,
базы с постройками, лес как «ковёр» крон со светом и тенью.
Геометрия совпадает с MiniMap.cs: доли 0…1, (0,0) — левый нижний угол (Radiant), (1,1) — правый верхний (Dire).

Запуск:  python tools/gen-minimap.py  src/Roshiq.App/Assets/Map/minimap.jpg
Нужны: numpy, pillow, scipy.
"""
import sys, math
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

OUT = sys.argv[1] if len(sys.argv) > 1 else "minimap.jpg"
N = 1024          # итоговый размер
R = 2048          # размер отрисовки (суперсэмплинг ×2)
F = 512           # размер расчётных полей
rng = np.random.default_rng(1729)


# ================================================================ утилиты
def grid(n):
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
    return (xs + 0.5) / n, 1 - (ys + 0.5) / n


X, Y = grid(F)


def up(a, n=R):
    return np.array(Image.fromarray(a.astype(np.float32), "F").resize((n, n), Image.BILINEAR))


def noise(octaves=5, base=4, seed=0, persist=0.5, n=F):
    r = np.random.default_rng(seed)
    acc = np.zeros((n, n), np.float32)
    amp, tot = 1.0, 0.0
    for o in range(octaves):
        k = base * 2 ** o
        g = r.random((k + 1, k + 1)).astype(np.float32)
        acc += amp * np.array(Image.fromarray(g, "F").resize((n, n), Image.BICUBIC))
        tot += amp
        amp *= persist
    acc /= tot
    return (acc - acc.min()) / (acc.max() - acc.min() + 1e-6)


def lerp(a, b, t):
    return a + (b - a) * t


def sm(m, s):
    return np.clip(ndimage.gaussian_filter(m.astype(np.float32), s), 0, 1)


def mirror(p):
    return (1 - p[0], 1 - p[1])


def bezier(p0, p1, p2, p3, n=60):
    t = np.linspace(0, 1, n)[:, None]
    return (1 - t) ** 3 * p0 + 3 * (1 - t) ** 2 * t * p1 + 3 * (1 - t) * t ** 2 * p2 + t ** 3 * p3


def poly_dist(pts, Xg=X, Yg=Y):
    pts = np.asarray(pts, np.float32)
    d = np.full(Xg.shape, 9.0, np.float32)
    for a, b in zip(pts[:-1], pts[1:]):
        ab = b - a
        L = float(ab @ ab) + 1e-9
        t = np.clip(((Xg - a[0]) * ab[0] + (Yg - a[1]) * ab[1]) / L, 0, 1)
        d = np.minimum(d, np.hypot(Xg - (a[0] + t * ab[0]), Yg - (a[1] + t * ab[1])))
    return d


def densify(pts, per=100):
    out = []
    for a, b in zip(pts[:-1], pts[1:]):
        k = int(math.hypot(b[0] - a[0], b[1] - a[1]) * per) + 2
        for t in np.linspace(0, 1, k)[:-1]:
            out.append((lerp(a[0], b[0], t), lerp(a[1], b[1], t)))
    out.append(pts[-1])
    return np.array(out, np.float32)


def meander(pts, amp, freq, seed):
    pts = np.asarray(pts, np.float32).copy()
    ph = np.random.default_rng(seed).random(3) * 6.28
    out, s = [pts[0]], 0.0
    for i in range(1, len(pts) - 1):
        tan = pts[i + 1] - pts[i - 1]
        nrm = np.array([-tan[1], tan[0]]) / (np.linalg.norm(tan) + 1e-9)
        s += np.linalg.norm(pts[i] - pts[i - 1])
        off = amp * (math.sin(s * freq + ph[0]) * 0.6 + math.sin(s * freq * 2.3 + ph[1]) * 0.3 + math.sin(s * freq * 5.1 + ph[2]) * 0.1)
        out.append(pts[i] + nrm * off)
    out.append(pts[-1])
    return np.array(out)


# ================================================================ геометрия (поля F)
side = X + Y - 1
corner_r = np.hypot(X, Y)
corner_d = np.hypot(1 - X, 1 - Y)

river_pts = np.vstack([
    bezier(np.array([-0.02, 1.02]), np.array([0.30, 0.78]), np.array([0.45, 0.58]), np.array([0.50, 0.50])),
    bezier(np.array([0.50, 0.50]), np.array([0.55, 0.42]), np.array([0.70, 0.22]), np.array([1.02, -0.02]))[1:],
])
river_pts = meander(river_pts, 0.016, 24, 3)
d_river = poly_dist(river_pts)
river_w = 0.028 + 0.012 * noise(4, 6, 11)

lanes = [
    [(0.08, 0.10), (0.08, 0.92), (0.90, 0.92)],
    [(0.10, 0.08), (0.92, 0.08), (0.92, 0.90)],
    [(0.12, 0.12), (0.88, 0.88)],
]
lane_dist = np.full((F, F), 9.0, np.float32)
for i, ln in enumerate(lanes):
    lane_dist = np.minimum(lane_dist, poly_dist(meander(densify(ln, 80), 0.005, 38, 10 + i)))

trail_src = [
    [(0.08, 0.40), (0.18, 0.42), (0.27, 0.36), (0.36, 0.24), (0.46, 0.17), (0.58, 0.16), (0.70, 0.17), (0.80, 0.08)],
    [(0.08, 0.55), (0.20, 0.50), (0.26, 0.58), (0.30, 0.71)],
    [(0.28, 0.28), (0.36, 0.30), (0.44, 0.31), (0.50, 0.38), (0.58, 0.42)],
    [(0.46, 0.17), (0.48, 0.08)],
    [(0.27, 0.36), (0.30, 0.44), (0.40, 0.46)],
    [(0.62, 0.24), (0.70, 0.29), (0.72, 0.36)],
    [(0.20, 0.50), (0.14, 0.60), (0.08, 0.66)],
    [(0.36, 0.24), (0.30, 0.16), (0.22, 0.08)],
]
trail_dist = np.full((F, F), 9.0, np.float32)
for k, tr in enumerate(trail_src):
    for j, pts in enumerate((tr, [mirror(p) for p in tr])):
        trail_dist = np.minimum(trail_dist, poly_dist(meander(densify(pts, 120), 0.007, 55, 100 + k * 2 + j)))

camps = [(0.27, 0.36), (0.36, 0.24), (0.46, 0.17), (0.58, 0.16), (0.70, 0.17), (0.20, 0.50), (0.26, 0.58), (0.44, 0.31)]
spots = []
for c in camps:
    spots += [(c, 0.030), (mirror(c), 0.030)]
spots += [((0.36, 0.64), 0.042), ((0.64, 0.36), 0.042), ((0.30, 0.71), 0.028), ((0.70, 0.29), 0.028),
          ((0.30, 0.44), 0.024), ((0.70, 0.56), 0.024), ((0.62, 0.24), 0.034), ((0.38, 0.76), 0.034),
          ((0.80, 0.24), 0.034), ((0.20, 0.76), 0.034)]
clear_d = np.full((F, F), 9.0, np.float32)
for (cx, cy), r in spots:
    clear_d = np.minimum(clear_d, np.hypot(X - cx, Y - cy) / r)
clear_d = clear_d + 0.25 * (noise(4, 16, 13) - 0.5)


def rbox(Xg, Yg, cx, cy, half, rad):
    qx, qy = np.abs(Xg - cx) - half + rad, np.abs(Yg - cy) - half + rad
    return np.hypot(np.maximum(qx, 0), np.maximum(qy, 0)) + np.minimum(np.maximum(qx, qy), 0) - rad


base_r = rbox(X, Y, 0.10, 0.10, 0.115, 0.05)
base_d = rbox(X, Y, 0.90, 0.90, 0.115, 0.05)

towers = [(0.08, 0.62), (0.08, 0.44), (0.08, 0.26), (0.36, 0.36), (0.28, 0.28), (0.21, 0.21), (0.80, 0.08), (0.52, 0.08), (0.30, 0.08)]
all_towers = towers + [mirror(t) for t in towers]

# ================================================================ рельеф: уровни с уступами
lvl_n = noise(5, 6, 5)
high = (np.minimum(corner_r, corner_d) + 0.05 * (lvl_n - 0.5)) < 0.34          # возвышенность у баз
mid_ground = np.abs(side) > 0.10 + 0.05 * (noise(4, 8, 8) - 0.5)                 # всё, что не низина у реки
elev = 1.0 * mid_ground + 1.0 * high
elev = elev + 0.5 * ((base_r < 0) | (base_d < 0))
water_f = d_river < river_w
elev = np.where(water_f, -0.6, elev).astype(np.float32)
# пандусы: на дорогах уступы сглажены сильнее
e_sharp = ndimage.gaussian_filter(elev, 0.9)
e_soft = ndimage.gaussian_filter(elev, 7)
ramp = sm(lane_dist < 0.03, 2)
elev_f = lerp(e_sharp, e_soft, ramp) + 0.25 * noise(6, 5, 9)

E = up(elev_f)
gy, gx = np.gradient(E)
gmag = np.hypot(gx, gy)
light = np.clip(1.0 + (-gx + gy) * 75, 0.55, 1.35)           # свет слева сверху
cliff = np.clip(gmag * 220 - 0.3, 0, 1) * 0.85                        # скальные склоны

# ================================================================ поля в полном разрешении
XR, YR = grid(R)
T = up(np.clip((side + 0.05 + 0.05 * (noise(4, 5, 7) - 0.5)) / 0.10, 0, 1))[..., None]   # 0 — Radiant, 1 — Dire
riverD = up(d_river)
riverW = up(river_w)
laneD = up(lane_dist)
trailD = up(trail_dist)
clearD = up(clear_d)
baseR = up(base_r)
baseD = up(base_d)
HIGH = up(high.astype(np.float32))

nA = up(noise(6, 6, 31))[..., None]
nB = up(noise(4, 14, 32))[..., None]
fine = noise(4, 64, 33, 0.55, n=R)[..., None]
fine2 = noise(3, 160, 34, 0.6, n=R)[..., None]

# ---------------------------------------------------------------- земля
grass = lerp(np.array([74, 116, 42], np.float32), np.array([134, 168, 66], np.float32), nA)
grass = lerp(grass, np.array([164, 170, 82], np.float32), np.clip(nB - 0.62, 0, 1) * 1.6)     # выгоревшие пятна
grass = grass * (0.88 + 0.24 * fine)
ash = lerp(np.array([50, 54, 62], np.float32), np.array([96, 96, 102], np.float32), nA)
ash = lerp(ash, np.array([90, 72, 76], np.float32), np.clip(nB - 0.55, 0, 1) * 1.6)
ash = ash * (0.86 + 0.28 * fine)
ground = lerp(grass, ash, T)
ground = ground * (1 + 0.07 * HIGH[..., None])

# скальные склоны
rock = lerp(np.array([118, 104, 84], np.float32), np.array([70, 68, 72], np.float32), T) * (0.75 + 0.5 * fine2)
ground = lerp(ground, rock, cliff[..., None])

# поляны (вытоптанная трава)
clr = np.clip((1.0 - clearD) * 3, 0, 1)[..., None]
ground = lerp(ground, ground * 0.9 + np.array([18, 14, 4], np.float32), clr * 0.6)

# тропы
dirt = lerp(np.array([168, 140, 94], np.float32), np.array([106, 98, 94], np.float32), T) * (0.84 + 0.3 * fine)
tr = np.clip((0.0085 - trailD) / 0.003, 0, 1)[..., None]
ground = lerp(ground, dirt * 0.95, tr * 0.85)

# дороги: тёмная обочина, утоптанная середина, колеи
lw = 0.017 + 0.004 * up(noise(3, 8, 21))
road = np.clip((lw - laneD) / 0.003, 0, 1)[..., None]
shoulder = np.clip((lw + 0.008 - laneD) / 0.004, 0, 1)[..., None] - road
ground = ground * (1 - 0.22 * np.clip(shoulder, 0, 1))
ruts = (np.abs(laneD - 0.0065) < 0.0012)[..., None] * 0.12
ground = lerp(ground, dirt * (1 - ruts), road)
# брусчатка у баз
pave = ((laneD < 0.014) & (np.minimum(np.hypot(XR, YR), np.hypot(1 - XR, 1 - YR)) < 0.36))
stone = lerp(np.array([184, 176, 156], np.float32), np.array([78, 74, 80], np.float32), T) * (0.9 + 0.2 * fine)
cell = 0.0055
bx = (XR / cell + 0.5 * (np.floor(YR / cell) % 2)) % 1
by = (YR / cell) % 1
mortar = ((bx < 0.08) | (by < 0.1))[..., None]
cob = stone * (1 - 0.25 * mortar) * (0.92 + 0.16 * fine2)
ground = lerp(ground, cob, sm(pave, 1.2)[..., None] * 0.9)

# камешки на дорогах, цветы на стороне Radiant, угольки на Dire
speck = rng.random((R, R)).astype(np.float32)
pebble = ((speck > 0.9965) & (road[..., 0] > 0.5))[..., None]
ground = np.where(pebble, ground * 0.7, ground)
flower = ((speck < 0.0012) & (T[..., 0] < 0.3) & (road[..., 0] < 0.1) & (cliff < 0.2))[..., None]
fcol = np.where((rng.random((R, R)) > 0.5)[..., None], np.array([238, 226, 120], np.float32), np.array([236, 236, 236], np.float32))
ground = np.where(flower, fcol, ground)
ember = ((speck > 0.9992) & (T[..., 0] > 0.8))[..., None]
ground = np.where(ember, np.array([230, 110, 50], np.float32), ground)

# ---------------------------------------------------------------- базы
for bd, cx, cy, fx, fy in ((baseR, 0.075, 0.075, 0.03, 0.03), (baseD, 0.925, 0.925, 0.97, 0.97)):
    inside = np.clip(-bd / 0.003, 0, 1)[..., None]
    ground = lerp(ground, cob, inside)
    wall = ((bd < 0.0) & (bd > -0.007))[..., None]
    ground = np.where(wall, ground * 0.5, ground)
    wall_top = ((bd < -0.0015) & (bd > -0.0045))[..., None]
    ground = np.where(wall_top, ground * 1.25 + 10, ground)
    d0 = np.hypot(XR - cx, YR - cy)
    ground = lerp(ground, ground * 1.08 + 10, (d0 < 0.040)[..., None].astype(np.float32))
    ground = np.where((np.abs(d0 - 0.043) < 0.003)[..., None], ground * 0.7, ground)
    # фонтан в углу
    df = np.hypot(XR - fx, YR - fy)
    ground = np.where((df < 0.026)[..., None], stone * 0.8, ground)
    ground = np.where((df < 0.020)[..., None], lerp(np.array([80, 170, 200], np.float32), np.array([200, 240, 250], np.float32), np.clip(1 - df / 0.02, 0, 1)[..., None] ** 2), ground)
# трещины с жаром в базе Dire
crack = up(noise(5, 12, 61))
cracks = (np.abs(crack - 0.5) < 0.006) & (baseD < -0.012)
glow = ndimage.gaussian_filter(cracks.astype(np.float32), 5)[..., None]
ground = ground + glow * np.array([260, 80, 0], np.float32)
ground = np.where(cracks[..., None], np.array([255, 150, 60], np.float32), ground)

# площадки под вышками
for tx, ty in all_towers:
    d = np.hypot(XR - tx, YR - ty)
    ground = lerp(ground, cob * 0.95, np.clip((0.016 - d) / 0.002, 0, 1)[..., None])
    ground = np.where((np.abs(d - 0.0165) < 0.0012)[..., None], ground * 0.6, ground)

# логово Рошана: каменная чаша
for rx, ry in ((0.36, 0.64), (0.64, 0.36)):
    d = np.hypot(XR - rx, YR - ry)
    ground = lerp(ground, stone * 0.55, np.clip((0.030 - d) / 0.003, 0, 1)[..., None])
    ground = lerp(ground, ground * 0.45, np.clip((0.022 - d) / 0.006, 0, 1)[..., None])
    ground = np.where((np.abs(d - 0.030) < 0.0026)[..., None], stone * 1.05, ground)
    ground = np.where((np.abs(d - 0.0335) < 0.0012)[..., None], ground * 0.45, ground)

# ---------------------------------------------------------------- река
bank = np.clip((riverW + 0.016 - riverD) / 0.006, 0, 1)[..., None]
sand = lerp(np.array([170, 156, 108], np.float32), np.array([92, 92, 90], np.float32), T) * (0.9 + 0.2 * fine)
ground = lerp(ground, sand, bank * 0.85 * (1 - cliff[..., None] * 0.7))
depth = np.clip(1 - riverD / riverW, 0, 1)[..., None]
wn = noise(5, 24, 71, n=R)[..., None]
water = lerp(np.array([78, 168, 178], np.float32), np.array([18, 58, 104], np.float32), depth ** 0.55) + (wn - 0.5) * 22
caust = (np.abs(noise(3, 40, 73, n=R) - 0.5) < 0.02)[..., None] * (1 - depth) * 40
ripple = (np.abs(noise(4, 30, 74, n=R) - 0.5) < 0.012)[..., None] * 18
water = water + caust + ripple
wmask = np.clip((riverW - riverD) / 0.0025, 0, 1)[..., None]
ground = lerp(ground, water, wmask)
foam = np.clip(1 - np.abs(riverD - riverW) / 0.0016, 0, 1)[..., None]
ground = lerp(ground, np.array([214, 236, 236], np.float32), foam * 0.35)
# камни в реке
for _ in range(18):
    i = int(rng.random() * (len(river_pts) - 1))
    px, py = river_pts[i] + rng.normal(0, 0.012, 2)
    rr = 0.003 + rng.random() * 0.003
    x0, x1 = max(0, int((px - rr) * R) - 1), min(R, int((px + rr) * R) + 2)
    y0, y1 = max(0, int((1 - py - rr) * R) - 1), min(R, int((1 - py + rr) * R) + 2)
    if x1 <= x0 or y1 <= y0:
        continue
    d = np.hypot(XR[y0:y1, x0:x1] - px, YR[y0:y1, x0:x1] - py)
    m = (d < rr)[..., None]
    c = lerp(np.array([60, 70, 62], np.float32), np.array([132, 136, 118], np.float32), np.clip(1 - d / rr, 0, 1)[..., None] ** 0.7)
    ground[y0:y1, x0:x1] = np.where(m, c, ground[y0:y1, x0:x1])

# ---------------------------------------------------------------- свет рельефа
ground = ground * light[..., None]

# ================================================================ лес: «ковёр» крон
edge = np.minimum.reduce([X, Y, 1 - X, 1 - Y])
free = ~((d_river < river_w + 0.018) | (lane_dist < 0.030) | (trail_dist < 0.014) | (clear_d < 1.1) | (base_r < 0.006) | (base_d < 0.006))
for tx, ty in all_towers:
    free &= np.hypot(X - tx, Y - ty) > 0.03
fn = noise(5, 7, 81)
forest = free & ((fn > 0.30) | (edge < 0.05))
forest = ndimage.binary_opening(forest, iterations=2)
forest_s = ndimage.gaussian_filter(forest.astype(np.float32), 1.0)
cliffF = np.array(Image.fromarray(cliff.astype(np.float32), "F").resize((F, F), Image.BILINEAR))

H = np.zeros((R, R), np.float32)          # высота крон
TINT = np.zeros((R, R), np.float32)       # оттенок кроны
trees = []
cs = 0.0062
n = int(1 / cs)
for i in range(n):
    for j in range(n):
        for _ in range(2):
            x, y = (i + rng.random()) * cs, (j + rng.random()) * cs
            fx, fy = min(F - 1, int(x * F)), min(F - 1, int((1 - y) * F))
            if forest_s[fy, fx] > 0.5 and cliffF[fy, fx] < 0.6 and rng.random() < 0.85:
                trees.append((x, y))
# одиночные деревья у кромок полян и вдоль троп
for _ in range(900):
    x, y = rng.random(), rng.random()
    fx, fy = min(F - 1, int(x * F)), min(F - 1, int((1 - y) * F))
    if free[fy, fx] and not forest[fy, fx] and 0.05 < forest_s[fy, fx] < 0.5:
        trees.append((x, y))

kcache = {}
for x, y in trees:
    r = int(round((3.2 + rng.random() ** 1.6 * 5.5) * R / N))
    if r not in kcache:
        yy, xx = np.mgrid[-r:r + 1, -r:r + 1].astype(np.float32)
        dd = np.hypot(xx, yy) / r
        k = np.sqrt(np.clip(1 - dd ** 2, 0, 1)) * r
        ang = np.arctan2(yy, xx)
        k = np.where(dd < 0.92 + 0.08 * np.sin(ang * 5), k, 0)        # неровный край кроны
        kcache[r] = k.astype(np.float32)
    k = kcache[r] * (0.85 + rng.random() * 0.3)
    px, py = int(x * R), int((1 - y) * R)
    y0, y1, x0, x1 = py - r, py + r + 1, px - r, px + r + 1
    ky0, kx0 = max(0, -y0), max(0, -x0)
    y0c, x0c, y1c, x1c = max(0, y0), max(0, x0), min(R, y1), min(R, x1)
    if y1c <= y0c or x1c <= x0c:
        continue
    kk = k[ky0:ky0 + (y1c - y0c), kx0:kx0 + (x1c - x0c)]
    sub = H[y0c:y1c, x0c:x1c]
    win = kk > sub
    sub[win] = kk[win]
    TINT[y0c:y1c, x0c:x1c][win] = rng.random()

# мелкая фактура листвы
H = H + (H > 0) * (noise(3, 220, 84, 0.6, n=R) - 0.5) * 2.2
cover = np.clip(H / 1.2, 0, 1)
cy_, cx_ = np.gradient(ndimage.gaussian_filter(H, 0.6))
nlight = np.clip(0.62 + (-cx_ + cy_) * 0.32, 0.0, 1.2)
hnorm = np.clip(H / (8.5 * R / N), 0, 1)
tint = TINT[..., None]
rad_lo = lerp(np.array([14, 44, 18], np.float32), np.array([30, 62, 20], np.float32), tint)
rad_hi = lerp(np.array([96, 150, 52], np.float32), np.array([150, 182, 70], np.float32), tint)
dire_lo = lerp(np.array([14, 18, 24], np.float32), np.array([24, 18, 28], np.float32), tint)
dire_hi = lerp(np.array([84, 98, 104], np.float32), np.array([104, 96, 110], np.float32), tint)
lo, hi = lerp(rad_lo, dire_lo, T), lerp(rad_hi, dire_hi, T)
canopy = lerp(lo, hi, np.clip(nlight[..., None] * (0.25 + 0.75 * hnorm[..., None] ** 0.6), 0, 1.15))
autumn = (TINT > 0.985)[..., None] & (T < 0.5)                   # редкие желтоватые кроны
canopy = np.where(autumn, canopy * np.array([1.22, 1.02, 0.7], np.float32), canopy)

# тень леса на землю + затенение у кромки
shadowm = ndimage.shift(ndimage.gaussian_filter(cover, 6), (9, 7), order=1)
ao = ndimage.gaussian_filter(cover, 14)
ground = ground * (1 - 0.55 * shadowm[..., None]) * (1 - 0.25 * ao[..., None])
img = lerp(ground, canopy, cover[..., None])

# ================================================================ постройки в базах (простые объёмные блоки)
im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
dr = ImageDraw.Draw(im, "RGBA")


def block(cx, cy, w, h, top, side_c):
    px, py = cx * R, (1 - cy) * R
    w, h = w * R, h * R
    o = 0.004 * R
    dr.rectangle([px - w / 2 + o, py - h / 2 + o, px + w / 2 + o, py + h / 2 + o], fill=(0, 0, 0, 90))
    dr.rectangle([px - w / 2, py - h / 2, px + w / 2, py + h / 2], fill=side_c)
    dr.rectangle([px - w / 2, py - h / 2, px + w / 2 - o * 0.5, py + h / 2 - o * 0.5], fill=top)


for sgn, top, side_c in ((0, (206, 198, 172, 255), (120, 110, 90, 255)), (1, (98, 90, 96, 255), (40, 36, 42, 255))):
    def m(p):
        return p if sgn == 0 else mirror(p)
    # казармы у выходов линий
    for (a, b) in [((0.055, 0.205), (0.105, 0.205)), ((0.205, 0.055), (0.205, 0.105)), ((0.165, 0.185), (0.185, 0.165))]:
        for p in (a, b):
            q = m(p)
            block(q[0], q[1], 0.020, 0.020, top, side_c)
    q = m((0.075, 0.075))
    block(q[0], q[1], 0.040, 0.040, top, side_c)
    px, py = q[0] * R, (1 - q[1]) * R
    c = (120, 230, 120, 255) if sgn == 0 else (255, 110, 70, 255)
    dr.ellipse([px - 0.009 * R, py - 0.009 * R, px + 0.009 * R, py + 0.009 * R], fill=c)

# ================================================================ финал
im = im.resize((N, N), Image.LANCZOS)
a = np.array(im).astype(np.float32)
yy, xx = np.mgrid[0:N, 0:N]
rr = np.hypot((xx - N / 2) / (N / 2), (yy - N / 2) / (N / 2))
a = a * np.clip(1.05 - 0.2 * rr ** 2.6, 0.62, 1.0)[..., None]
g = ((xx + yy) / (2 * N))[..., None]                              # тёплый свет сверху-слева, прохладнее снизу-справа
a = a * lerp(np.array([1.06, 1.03, 0.96]), np.array([0.94, 0.97, 1.05]), g)
a = np.clip((a - 128) * 1.07 + 128, 0, 255)
Image.fromarray(a.astype(np.uint8)).save(OUT, quality=91, optimize=True, progressive=True)
print("saved", OUT, len(trees), "trees")
