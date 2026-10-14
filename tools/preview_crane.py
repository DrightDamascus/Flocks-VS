"""Renders the crane model with its texture from a few angles, without starting the game.

  python tools/preview_crane.py [out.png]

Builds the model straight from make_crane.py (geometry, UV layout, animations) and reads the texture PNG, so
hand-painted texture edits show up. Small orthographic software renderer: z-buffer, alpha cut-out, double-sided
faces, simple face shading. Rows: flap frames 0/8/16/24 and the glide; columns: below, 3/4 below, side, above.
Also writes crane_preview_top/side/34.png, larger single views of mid-downstroke.
"""
import math, os, sys
import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_crane as mg

CELL = 260
SKY = (138, 168, 222)


def rot(axis, deg):
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    if axis == 0: return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])
    if axis == 1: return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


def pose(anim, frame):
    """Linear keyframe sampling per element (the game bakes a spline; close enough for a preview)."""
    n, keys, out = anim["quantityframes"], anim["keyframes"], {}
    for name in {k for kf in keys for k in kf["elements"]}:
        ks = [(kf["frame"], kf["elements"][name]) for kf in keys if name in kf["elements"]]
        a, b = ks[-1], ks[0]
        for i, k in enumerate(ks):
            if k[0] <= frame: a, b = k, ks[(i + 1) % len(ks)]
        span = (b[0] - a[0]) % n or n
        t = ((frame - a[0]) % n) / span
        out[name] = [a[1].get(r, 0) + (b[1].get(r, 0) - a[1].get(r, 0)) * t for r in ("rotationX", "rotationY", "rotationZ")]
    return out


def quads(root, p):
    """(corners 4x3 world, uv corners 4x2 in texture px, face normal-ish) for every face, posed."""
    out = []

    def walk(el, parent_tf):
        r = list(el.rot)
        if el.name in p: r = [a + b for a, b in zip(r, p[el.name])]
        R = rot(0, r[0]) @ rot(1, r[1]) @ rot(2, r[2])
        o = np.array(el.origin, float)
        tf = lambda v, R=R, o=o: parent_tf(o + R @ (np.asarray(v, float) - o))
        src = el.uv_from or el
        for f in el.faces:
            x0, y0, pw, ph = src.px[f]
            w, h = src.face_size(f)
            corners, uvs = [], []
            for fu, fv in ((0, 0), (1, 0), (1, 1), (0, 1)):
                corners.append(tf(el.point(f, fu, fv)))
                uvs.append((x0 + fu * w * mg.PX, y0 + fv * h * mg.PX))
            out.append((np.array(corners), np.array(uvs), f))
        for c in el.children: walk(c, tf)

    walk(root, lambda v: v)
    return out


def render(qs, tex, view):
    """Orthographic render. view: (yaw deg, pitch deg) of the camera; pitch -90 looks straight up from below."""
    yaw, pitch = view
    V = rot(0, pitch) @ rot(1, -yaw)   # larger view z = nearer the camera
    img = np.zeros((CELL, CELL, 3)); img[:] = SKY
    zb = np.full((CELL, CELL), -1e9)
    scale, centre = CELL / 60.0, np.array([4.5, 4.5, 8.0])
    th, tw = tex.shape[:2]
    for corners, uvs, f in qs:
        P = (V @ (corners - centre).T).T
        n = np.cross(P[1] - P[0], P[2] - P[0]); ln = np.linalg.norm(n)
        light = 0.75 + 0.25 * abs(n[1] / ln) if ln > 1e-9 else 1.0
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


def main():
    global CELL
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "crane_preview.png")
    root = mg.shape(); mg.pack(root)
    tex = np.array(Image.open(os.path.join(mg.ROOT, "textures", "entity", "bird", mg.TEX + ".png")).convert("RGBA"), float)
    anims = {a["code"]: a for a in mg.animations()}
    rows = [("flap 0", anims["fly-flap"], 0), ("flap 8", anims["fly-flap"], 8), ("flap 16", anims["fly-flap"], 16),
            ("flap 24", anims["fly-flap"], 24), ("glide", anims["fly-soar"], 0)]
    views = [("below", (0, -90)), ("3/4 below", (35, -40)), ("side", (0, 0)), ("above", (0, 90))]
    sheet = Image.new("RGB", (CELL * len(views), CELL * len(rows)), SKY)
    dr = ImageDraw.Draw(sheet)
    for ri, (label, anim, fr) in enumerate(rows):
        qs = quads(root, pose(anim, fr))
        for ci, (vlabel, view) in enumerate(views):
            sheet.paste(render(qs, tex, view), (ci * CELL, ri * CELL))
            dr.text((ci * CELL + 4, ri * CELL + 4), f"{label} - {vlabel}", fill=(20, 20, 40))
    sheet.save(out)
    print("wrote", out)
    # single larger views of the downstroke pose: top, side and 3/4
    CELL = 520
    here = os.path.dirname(os.path.abspath(out))
    qs = quads(root, pose(anims["fly-flap"], 16))
    for name, view in (("top", (0, 90)), ("side", (0, 0)), ("34", (35, 30))):
        path = os.path.join(here, f"crane_preview_{name}.png")
        render(qs, tex, view).save(path)
        print("wrote", path)


if __name__ == "__main__":
    main()
