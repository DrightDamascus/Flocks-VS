"""Generates the amphithere shape (VS cuboid JSON), procedural texture and fly animations.

  python tools/make_amphithere.py

Reuses the El/pack/paint machinery from make_greylag.py. Writes into Flocks/assets/flocks/ (overwrites the texture).
Conventions as the hawk/goose: head to -X, wings along +-Z, up +Y, 16 units = 1 block.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as cl   # shared El/pack/paint helpers
from make_greylag import El, mul, mix, frac

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "Flocks", "assets", "flocks")
TEX = "amphithere"
cl.TEX, cl.SHEET_W = TEX, 128
cl.ROOT = OUT

EMERALD = (22, 150, 92); EMERALD_DK = (12, 98, 64); TEAL = (24, 150, 160); TEAL_LT = (80, 200, 196)
GOLD = (236, 186, 52); GOLD_DK = (190, 132, 30); SCARLET = (214, 40, 36); SCARLET_DK = (150, 24, 30)
BELLY = (242, 214, 120); EYE = (20, 16, 10); EYE_RING = (250, 230, 90); CREAM = (250, 240, 200)
CY, CZ = 5.0, 8.0

def _desat(c, k=0.5, dark=0.78):
    g = sum(c) / 3
    return tuple(int((g + (v - g) * k) * dark) for v in c)
EMERALD, EMERALD_DK, TEAL, TEAL_LT = map(_desat, (EMERALD, EMERALD_DK, TEAL, TEAL_LT))
GOLD, GOLD_DK, SCARLET, SCARLET_DK = map(_desat, (GOLD, GOLD_DK, SCARLET, SCARLET_DK))
BELLY, CREAM = _desat(BELLY, 0.45, 0.7), _desat(CREAM, 0.4, 0.75)
MUD, MUD_DK, SCAR, BONE, BONE_DK, SOOT = (92, 74, 52), (58, 46, 34), (168, 140, 124), (176, 164, 136), (112, 102, 84), (34, 32, 28)

def _n(x, y, z, s=1.0):
    """cheap value noise from model position"""
    return frac(math.sin(x * 12.99 * s + y * 78.23 * s + z * 37.72 * s) * 43758.55)

def _smooth(x, y, z, sc):
    xs, ys, zs = x / sc, y / sc, z / sc
    return (math.sin(xs * 1.7 + zs * 2.3) + math.sin(ys * 2.9 - xs * 1.3) + math.sin(zs * 3.1 + ys * 0.7)) / 6 + 0.5

def grime(mat):
    """Weathering pass: mottling, mud streaks running down, darker undersides, worn spots."""
    def m(x, y, z, face, fu, fv):
        c = mat(x, y, z, face, fu, fv)
        if c is None: return None
        k = 0.8 + 0.35 * _smooth(x, y, z, 1.6)                     # large mottling
        if face == "down": k *= 0.72
        elif face not in ("up",): k *= 0.82 + 0.18 * (1 - fv)          # sides darken toward the bottom
        c = mul(c, k)
        if _smooth(x * 3.0, 0, z * 3.0, 1.0) > 0.78 and fv > 0.35 and face not in ("up",):
            c = mix(c, MUD, 0.55)                                       # vertical mud streaks
        if face == "down" and _smooth(x, y, z, 0.9) > 0.6: c = mix(c, MUD_DK, 0.6)
        r = _n(x, y, z)
        if r > 0.93: c = mix(c, MUD_DK, 0.7)                           # dirt flecks
        elif r < 0.05: c = mix(c, BONE, 0.35)                           # worn, bleached spots
        return c
    return m


def scales(x, y, z):
    row = math.floor(x / 1.0); lz = frac((z + y + row * 0.5) / 1.0)
    if frac(x) > 0.75: return EMERALD_DK
    return TEAL if lz < 0.3 else EMERALD

def m_seg(x, y, z, face, fu, fv):
    if -3 < x < 12 and face in ("north", "south", "up"):           # old claw scars: three diagonal rakes
        for o in (0.0, 0.9, 1.8):
            if abs(frac((x * 0.55 + y * 0.8 + o + (0.4 if face == "south" else 0)) / 5.0) - 0.5) < 0.035 and _smooth(x, y, z, 2) > 0.45:
                return SCAR
    if face == "up":
        if abs(z - CZ) < 0.45: return SCARLET if frac(x / 2) < 0.6 else GOLD   # dorsal stripe
        return scales(x, y, z)
    if face == "down":
        return BELLY if frac(x / 0.75) > 0.2 else GOLD_DK                      # belly plates
    if face in ("east", "west"): return mix(EMERALD, BELLY, 0.4)
    if y < CY - 0.7: return BELLY
    if y < CY - 0.3: return GOLD
    return scales(x, y, z)

def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south") and -12.4 < x < -11.0 and 5.3 < y < 6.3:
        return EYE if -12.0 < x < -11.4 and 5.5 < y < 6.1 else (200, 120, 30)
    if face == "down": return BELLY
    if face == "west": return GOLD
    if face == "up" and abs(z - CZ) < 0.4: return SCARLET
    return scales(x, y, z)

def m_beak(x, y, z, face, fu, fv):
    if face == "down": return mul(GOLD_DK, 0.85)
    if face in ("north", "south") and fv > 0.75: return GOLD_DK      # cutting edge line
    return mix(GOLD, CREAM, 0.25) if fu < 0.5 else GOLD

def m_hook(x, y, z, face, fu, fv):
    return mul(GOLD_DK, 0.8) if face == "down" else mix(GOLD_DK, CREAM, 0.2)

def feather(x0, w, length, axis_c, axis_s, colours, notch=True, seed=0.0):
    """Material for a single long feather plane: tapered, rounded tip, shaft down the middle, cut-out edges."""
    base, mid, tip = colours
    def m(x, y, z, face, fu, fv):
        c, s = axis_c(x, y, z), axis_s(x, y, z)        # c 0..1 across, s 0 base..1 tip
        half = 0.5 * (1 - 0.15 * s) * (1 - max(0, s - 0.7) / 0.3 * 0.9)
        d = abs(c - 0.5)
        if d > half: return None
        fray = 0.62 + 0.3 * frac(math.sin(seed * 7.1 + 1.3) * 999)            # where this feather starts to fray
        if s > fray and _n(round(c * 6), round(s * 14), seed) > 1.15 - (s - fray) * 2.2: return None   # ragged barb gaps
        if s > 0.9 - 0.25 * frac(seed * 3.7) and d > 0.12: return None           # tattered, uneven tip
        if notch and c > 0.5 and 0.55 < s < 0.62: return None      # emargination notch on the trailing vane
        if d < 0.07: return CREAM if s < 0.6 else GOLD_DK          # shaft
        col = base if s < 0.45 else (mid if s < 0.7 else tip)
        if s > 0.8: col = mix(col, SOOT, min(1, (s - 0.8) * 4))                   # dark, worn tips
        return mul(col, 0.85) if frac(s * 9) < 0.15 else col     # barb lines
    return m

def m_tailfin(x, y, z, face, fu, fv):
    u = (x - FIN_X0) / FIN_LEN; dz = (z - CZ) / FIN_HALF
    w = abs(dz)
    if w > 0.22 + 0.78 * u ** 0.7: return None                 # fan widens backward
    ang = math.atan2(dz * FIN_HALF, max(0.01, x - FIN_X0 + 2))  # radial feathers from a point just ahead of the fin
    lf = frac(ang / 0.16 + 0.5)
    r = math.hypot(x - FIN_X0 + 2, z - CZ) / (FIN_LEN + 2)
    if r > 0.88 + 0.08 * math.cos(math.pi * (2 * lf - 1)) - (0.06 if (lf < 0.12 or lf > 0.88) else 0): return None
    if r > 0.86 and (lf < 0.12 or lf > 0.88): return None
    if r > 0.62 + 0.3 * _n(round(ang / 0.16), 0, 5) and _n(round(x * 2), 2, round(z * 2)) > 0.35: return None  # ragged
    if lf < 0.06 or lf > 0.94: return mul(EMERALD_DK, 0.9)
    if abs(lf - 0.5) < 0.05: return CREAM if r < 0.6 else GOLD_DK
    if r > 0.8: return mix(SCARLET_DK, SOOT, 0.5)
    if r > 0.7: return SCARLET
    if r > 0.55: return GOLD
    return TEAL_LT if lf > 0.5 else TEAL

def m_edge(x, y, z, face, fu, fv): return GOLD_DK

BODY_HW = 3.4
ARM_X = (-2.5, 10.5); ARM_ROOT, ARM_LEN = BODY_HW - 0.3, 15.0
HAND_X = (-2.0, 8.5); HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 8.0
WING_Y = 7.4
N_PRIM, PRIM_LEN, PRIM_W, PRIM_STEP = 7, 14.0, 2.0, 11.0     # primaries: equal spacing, equal angle step
FIN_X0, FIN_LEN, FIN_HALF = 44.0, 14.0, 9.0

def m_arm(x, y, z, face, fu, fv):
    c, s = cl.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8) / 1.4)
    if c > 0.94 and (lz < 0.2 or lz > 0.8): return None
    if c > 0.8 + 0.18 * _n(round(abs(z - 8) * 1.5), 0, 3): return None          # torn trailing edge
    if 0.55 < c < 0.75 and _n(round(x * 2), 1, round(z * 2)) > 0.985: return None  # holes
    if c < 0.08: return BONE_DK
    if c < 0.45: return EMERALD_DK if frac((c - 0.08) / 0.1) > 0.7 else EMERALD
    if c > 0.86: return SCARLET
    return TEAL if 0.12 < lz < 0.88 else mul(TEAL, 0.75)

def m_hand(x, y, z, face, fu, fv):
    c, s = cl.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lz = frac(abs(z - 8) / 1.2)
    if c > 0.9 and (lz < 0.25 or lz > 0.75): return None       # scalloped covert edge over the primaries
    if c < 0.08: return GOLD
    return EMERALD_DK if frac(c / 0.14) > 0.75 else (EMERALD if s < 0.5 else TEAL)

def mz(z): return 16 - z

def primaries(side, z_base):
    L = side == "L"; out = []
    x_lo, x_hi = HAND_X[0] + 1.0, HAND_X[1] - 0.6
    for i in range(N_PRIM):
        px = x_lo + i * (x_hi - x_lo) / (N_PRIM - 1)
        ang = i * PRIM_STEP                                       # leading feather points straight out, rest fan back
        zb = z_base - 1.0
        z0, z1 = (zb, zb + PRIM_LEN) if L else (mz(zb + PRIM_LEN), mz(zb))
        x0 = px - PRIM_W / 2
        ac = (lambda x0: lambda x, y, z: (x - x0) / PRIM_W)(x0)
        as_ = (lambda zb: lambda x, y, z: (abs(z - 8) - zb + 8) / PRIM_LEN)(zb)
        mat = grime(feather(x0, PRIM_W, PRIM_LEN, ac, as_, (TEAL, GOLD, SCARLET_DK), seed=i + (0.5 if L else 0)))
        out.append(El(f"Prim{side}{i}", [x0, WING_Y + 0.01 * i, z0], [x0 + PRIM_W, WING_Y + 0.01 * i, z1], mat,
                      faces=("up",), origin=[px, WING_Y, zb if L else mz(zb)], rot=(0, ang if L else -ang, 0)))
    return out

def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], grime(m_arm), faces=("up",),
             origin=[ARM_X[0] + 3, WING_Y, z_root if L else mz(z_root)])
    edge = El("EdgeArm" + side, [ARM_X[0] - 0.4, WING_Y - 0.45, a0], [ARM_X[0] + 0.9, WING_Y + 0.45, a1], grime(m_spine))
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], grime(m_hand), faces=("up",),
              origin=[HAND_X[0] + 2, WING_Y, z_mid if L else mz(z_mid)])
    return arm.add(edge, hand.add(*primaries(side, z_tip)))

# torso fattest at Body, tapering into neck and tail; rest pose has a gentle S-curve in yaw
SEGS = [  # (name, x0, x1, half-width, half-height, rest yaw)
    ("Tail1", 8, 14, 2.8, 2.5, 6), ("Tail2", 14, 20, 2.3, 2.05, 8), ("Tail3", 20, 26, 1.85, 1.65, -3),
    ("Tail4", 26, 32, 1.45, 1.3, -10), ("Tail5", 32, 38, 1.1, 1.0, -7), ("Tail6", 38, 44, 0.8, 0.75, 6),
]

def m_spine(x, y, z, face, fu, fv):
    if face == "up" or fv < 0.3: return BONE if _n(x, y, z) > 0.2 else BONE_DK
    return mix(BONE_DK, SOOT, 0.3)

def m_claw(x, y, z, face, fu, fv): return mix(SOOT, BONE_DK, 0.4 if fv < 0.5 else 0.1)

def ridge(name, x0, x1, hw, hh, i):
    """bony dorsal plates along a segment, thinning and tilting back"""
    out = []
    n = max(1, int((x1 - x0) / 2))
    for k in range(n):
        xa = x0 + (k + 0.25) * (x1 - x0) / n; h = 0.5 + hh * 0.55; w = max(0.3, hw * 0.28)
        out.append(El(f"{name}Spike{k}", [xa, CY + hh - 0.2, CZ - w / 2], [xa + 1.2, CY + hh + h, CZ + w / 2],
                      grime(m_spine), origin=[xa, CY + hh, CZ], rot=(0, 0, -28)))
    return out

def box(name, x0, x1, hw, hh, mat, yaw=0, pivot=None):
    mat = grime(mat)
    return El(name, [x0, CY - hh, CZ - hw], [x1, CY + hh, CZ + hw], mat,
              origin=[pivot if pivot is not None else x0, CY, CZ], rot=(0, yaw, 0))

def plume(name, x0, length, w, pivot, rot, colours, horizontal=False):
    if horizontal:   # lies flat, spans +-z
        el = El(name, [x0, pivot[1], CZ - w / 2], [x0 + length, pivot[1], CZ + w / 2], None, faces=("up",), origin=pivot, rot=rot)
        el.mat = grime(feather(0, w, length, lambda x, y, z: (z - CZ + w / 2) / w, lambda x, y, z: (x - x0) / length, colours, False, seed=hash(name) % 97))
    else:
        el = El(name, [x0, pivot[1] - w / 2, CZ], [x0 + length, pivot[1] + w / 2, CZ], None, faces=("north",), origin=pivot, rot=rot)
        el.mat = grime(feather(0, w, length, lambda x, y, z: (y - pivot[1] + w / 2) / w, lambda x, y, z: (x - x0) / length, colours, False, seed=hash(name) % 97))
    return el

def shape():
    body = box("Body", -1, 8, BODY_HW, 3.0, m_seg, pivot=3)
    body.add(*ridge("Body", -1, 8, BODY_HW, 3.0, 0))
    chest = box("Chest", -4, -1, 2.6, 2.4, m_seg, pivot=-1)
    neck = box("Neck", -8, -4, 1.9, 1.8, m_seg, yaw=-10, pivot=-4)
    neck.add(*ridge("Neck", -8, -4, 1.9, 1.8, 0))
    head = El("Head", [-13.5, 3.2, 5.6], [-8, 7.0, 10.4], grime(m_head), origin=[-8, 5, 8], rot=(0, 14, -6))
    brow = El("Brow", [-12.6, 6.2, 5.4], [-9.4, 7.4, 10.6], grime(m_spine), origin=[-9.4, 6.8, 8], rot=(0, 0, 10))
    beak = El("Beak", [-17.6, 3.9, 6.9], [-13.5, 6.3, 9.1], grime(m_beak), origin=[-13.5, 5.8, 8], rot=(0, 0, 26))
    hook = El("BeakHook", [-18.6, 2.4, 7.3], [-17.2, 4.6, 8.7], grime(m_hook), origin=[-17.6, 4.6, 8], rot=(0, 0, 18))
    jaw = El("Jaw", [-16.2, 3.2, 7.2], [-13.5, 4.0, 8.8], grime(m_beak), origin=[-13.5, 3.8, 8], rot=(0, 0, 14))
    beak.add(hook); head.add(jaw, brow)
    crest = [plume(f"Plume{i}", -10.0, 12.0 - abs(i - 2) * 1.2, 2.0, [-10.0, 7.2, CZ], (0, 0, 8 + i * 12),
                   (TEAL, GOLD, SCARLET_DK)) for i in range(5)]
    cheeks = [plume(f"Cheek{s}{i}", -9.5, 8.5, 1.6, [-9.5, 5.4, CZ + (2.3 if s == "L" else -2.3)],
                    (0, (1 if s == "L" else -1) * (-25 - i * 15), 8), (TEAL, GOLD, SCARLET), horizontal=True)
              for s in "LR" for i in range(2)]
    head.add(beak, *crest, *cheeks); neck.add(head); chest.add(neck)
    prev = body
    for name, x0, x1, hw, hh, yaw in SEGS:
        s = box(name, x0, x1, hw, hh, m_seg, yaw=yaw); s.add(*ridge(name, x0, x1, hw, hh, 0)); prev.add(s); prev = s
    talons = []
    for side, zc in (("L", CZ + 1.6), ("R", CZ - 1.6)):
        for k in range(3):
            t = El(f"Talon{side}{k}", [2.5 + k * 1.1, CY - 4.3, zc - 0.3], [3.1 + k * 1.1, CY - 2.8, zc + 0.3], m_claw,
                   origin=[2.8 + k * 1.1, CY - 2.8, zc], rot=(0, 0, -30))
            talons.append(t)
    body.add(*talons)
    fin = El("TailFin", [FIN_X0, CY + 0.05, CZ - FIN_HALF], [FIN_X0 + FIN_LEN, CY + 0.05, CZ + FIN_HALF], grime(m_tailfin),
             faces=("up",), origin=[FIN_X0, CY, CZ])
    prev.add(fin)
    body.add(chest, wing("L"), wing("R"))
    return body

def key(frame):
    p = frame / 48 * 2 * math.pi
    arm = 6 + 30 * math.sin(p); hand = 16 * math.sin(p - 1.1)   # hand lags the arm
    els = {"ArmL": {"rotationX": round(-arm, 2)}, "ArmR": {"rotationX": round(arm, 2)},
           "WingL": {"rotationX": round(-hand, 2)}, "WingR": {"rotationX": round(hand, 2)},
           "Body": {"offsetY": round(-0.15 * math.sin(p), 3)}}
    for i, s in enumerate(SEGS):   # travelling wave down the tail
        els[s[0]] = {"rotationY": round(s[5] + 10 * math.sin(p - i * 0.9), 2),
                     "rotationZ": round(4 * math.sin(p - i * 0.9 + 1.2), 2)}
    els["TailFin"] = {"rotationZ": round(6 * math.sin(p - 6 * 0.9 + 1.2), 2)}
    els["Neck"] = {"rotationY": round(-10 + 4 * math.sin(p + 1.5), 2)}
    return {"frame": frame, "elements": els}

def animations():
    return [{"name": "fly-flap", "code": "fly-flap", "quantityframes": 48, "onAnimationEnd": "Repeat",
             "keyframes": [key(f) for f in range(0, 48, 6)]}]

def main():
    shapes = os.path.join(OUT, "shapes", "entity"); textures = os.path.join(OUT, "textures", "entity")
    os.makedirs(shapes, exist_ok=True); os.makedirs(textures, exist_ok=True)
    root = shape(); height = cl.pack(root)
    data = {"textureWidth": cl.SHEET_W // cl.PX, "textureHeight": height // cl.PX,
            "textures": {TEX: "flocks:entity/" + TEX},
            "elements": [root.json()], "animations": cl.complete_axes(animations())}
    with open(os.path.join(shapes, TEX + ".json"), "w", newline="\n") as f: json.dump(data, f, indent=1)
    img = cl.paint(root, height); img.save(os.path.join(textures, TEX + ".png"))
    cl.uv_guide(root, img).save(os.path.join(HERE, "amphithere_uv_guide.png"))
    print("elements", sum(1 for _ in root.walk()), "texture", img.size)

if __name__ == "__main__":
    main()
