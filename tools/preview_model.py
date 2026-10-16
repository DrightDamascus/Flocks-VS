"""Renders any make_<bird>.py model with its texture in chosen poses, without starting the game.

  python tools/preview_model.py make_owl out.png idle-stand:0 idle-stand:60 fly-flap:8 ...

Rows are animation:frame (or "rest" for the base pose); columns are side, front 3/4, front and above views, on a
ground plane under the lowest point. Builds the model from the generator module (geometry, UV layout, animations)
and reads its texture PNG, so hand edits show. Unlike preview_crane.py it also applies keyframe offsets (the ground
stance lifts the body with offsetY). Orthographic software renderer: z-buffer, alpha cut-out, double-sided faces.
"""
import importlib, math, os, sys
import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

CELL = 300
SKY = (138, 168, 222)
GROUND = (96, 120, 70)


def rot(axis, deg):
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    if axis == 0: return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])
    if axis == 1: return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


CHANNELS = ("rotationX", "rotationY", "rotationZ", "offsetX", "offsetY", "offsetZ")


def pose(anim, frame):
    """Linear keyframe sampling per element (the game bakes a spline; close enough for a preview)."""
    if anim is None: return {}
    n, keys, out = anim["quantityframes"], anim["keyframes"], {}
    for name in {k for kf in keys for k in kf["elements"]}:
        ks = [(kf["frame"], kf["elements"][name]) for kf in keys if name in kf["elements"]]
        a, b = ks[-1], ks[0]
        for i, k in enumerate(ks):
            if k[0] <= frame: a, b = k, ks[(i + 1) % len(ks)]
        span = (b[0] - a[0]) % n or n
        t = ((frame - a[0]) % n) / span
        out[name] = [a[1].get(r, 0) + (b[1].get(r, 0) - a[1].get(r, 0)) * t for r in CHANNELS]
    return out


def quads(mg, root, p):
    out = []

    def walk(el, parent_tf):
        r = list(el.rot); off = np.zeros(3)
        if el.name in p:
            r = [a + b for a, b in zip(r, p[el.name][:3])]
            off = np.array(p[el.name][3:])
        R = rot(0, r[0]) @ rot(1, r[1]) @ rot(2, r[2])
        o = np.array(el.origin, float)
        tf = lambda v, R=R, o=o, off=off: parent_tf(o + off + R @ (np.asarray(v, float) - o))
        src = el.uv_from or el
        for f in el.faces:
            x0, y0, pw, ph = src.px[f]
            w, h = src.face_size(f)
            corners, uvs = [], []
            for fu, fv in ((0, 0), (1, 0), (1, 1), (0, 1)):
                corners.append(tf(el.point(f, fu, fv)))
                uvs.append((x0 + fu * w * mg.g.PX if hasattr(mg, "g") else x0 + fu * w * mg.PX, y0 + fv * h * (mg.g.PX if hasattr(mg, "g") else mg.PX)))
            out.append((np.array(corners), np.array(uvs), f))
        for c in el.children: walk(c, tf)

    walk(root, lambda v: v)
    return out


def render(qs, tex, view, centre, zoom):
    yaw, pitch = view
    V = rot(0, pitch) @ rot(1, -yaw)
    img = np.zeros((CELL, CELL, 3)); img[:] = SKY
    zb = np.full((CELL, CELL), -1e9)
    scale = CELL / zoom
    th, tw = tex.shape[:2]
    for corners, uvs, f in qs:
        P = (V @ (corners - centre).T).T
        n = np.cross(P[1] - P[0], P[2] - P[0]); ln = np.linalg.norm(n)
        light = 0.72 + 0.28 * abs(n[1] / ln) if ln > 1e-9 else 1.0
        sx = CELL / 2 + P[:, 0] * scale; sy = CELL / 2 - P[:, 1] * scale
        for tri in ((0, 1, 2), (0, 2, 3)):
            xs, ys, zs, uv = sx[list(tri)], sy[list(tri)], P[list(tri), 2], uvs[list(tri)]
            d = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
            if abs(d) < 1e-9: continue
            x_lo, x_hi = max(0, int(xs.min())), min(CELL - 1, int(xs.max()) + 1)
            y_lo, y_hi = max(0, int(ys.min())), min(CELL - 1, int(ys.max()) + 1)
            if x_lo > x_hi or y_lo > y_hi: continue
            gx, gy = np.meshgrid(np.arange(x_lo, x_hi + 1) + 0.5, np.arange(y_lo, y_hi + 1) + 0.5)
            a = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / d
            b = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / d
            c = 1 - a - b
            m = (a >= -1e-6) & (b >= -1e-6) & (c >= -1e-6)
            if not m.any(): continue
            z = a * zs[0] + b * zs[1] + c * zs[2]
            u = np.clip((a * uv[0, 0] + b * uv[1, 0] + c * uv[2, 0]).astype(int), 0, tw - 1)
            v = np.clip((a * uv[0, 1] + b * uv[1, 1] + c * uv[2, 1]).astype(int), 0, th - 1)
            texel = tex[v, u]
            m &= texel[..., 3] > 12
            sub = zb[y_lo:y_hi + 1, x_lo:x_hi + 1]
            m &= z > sub
            sub[m] = z[m]
            img[y_lo:y_hi + 1, x_lo:x_hi + 1][m] = texel[..., :3][m] * light
    return Image.fromarray(img.clip(0, 255).astype(np.uint8))


def with_plane(qs, tex, y, centre, half):
    th, tw = tex.shape[:2]
    strip = np.zeros((2, tw, 4)); strip[..., :3] = GROUND; strip[..., 3] = 255
    tex = np.concatenate([tex, strip])
    cx, cz = centre[0], centre[2]
    corners = np.array([[cx - half, y, cz - half], [cx + half, y, cz - half], [cx + half, y, cz + half], [cx - half, y, cz + half]])
    uvs = np.array([[0, th], [tw - 1, th], [tw - 1, th + 1], [0, th + 1]], float)
    return qs + [(corners, uvs, "up")], tex


def main():
    mod, out, rows = sys.argv[1], sys.argv[2], sys.argv[3:]
    mg = importlib.import_module(mod)
    root = mg.shape(); (mg.g.pack if hasattr(mg, "g") else mg.pack)(root)
    sub = getattr(mg, "SUBDIR", "bird")
    tex = np.array(Image.open(os.path.join(mg.ROOT, "textures", "entity", sub, mg.TEX + ".png")).convert("RGBA"), float)
    anims = {a["code"]: a for a in mg.animations()}
    views = [("side", (0, 6)), ("3/4 front", (-40, 18)), ("front", (-90, 6)), ("above", (0, 80))]
    zoom = float(os.environ.get("ZOOM", "34"))
    sheet = Image.new("RGB", (CELL * len(views), CELL * len(rows)), SKY)
    dr = ImageDraw.Draw(sheet)
    for ri, row in enumerate(rows):
        name, _, fr = row.partition(":")
        qs = quads(mg, root, pose(anims.get(name), float(fr or 0)))
        pts = np.concatenate([c for c, _, _ in qs])
        lo = pts[:, 1].min()
        centre = np.array([(pts[:, 0].min() + pts[:, 0].max()) / 2, (lo + pts[:, 1].max()) / 2, 8.0])
        qs2, tex2 = with_plane(qs, tex, lo - 0.02, centre, zoom)
        for ci, (vlabel, view) in enumerate(views):
            sheet.paste(render(qs2, tex2, view, centre, zoom), (ci * CELL, ri * CELL))
            dr.text((ci * CELL + 4, ri * CELL + 4), f"{row} - {vlabel}", fill=(20, 20, 40))
    sheet.save(out)
    print("wrote", out)


if __name__ == "__main__":
    main()
