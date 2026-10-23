"""Generates the amphithere shape (VS cuboid JSON), texture and fly animation.

  python tools/make_amphithere.py            shape + animation; texture only if it does not exist yet
  python tools/make_amphithere.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/amphithere.json, textures/entity/amphithere.png and
tools/amphithere_uv_guide.png. Built from make_greylag.py's pieces (elements, UV packing, painting, the 0.22.0 texture
toolkit). Conventions as the hawk/goose: head to -X, wings along +-Z, up +Y, 16 units = 1 block.
Render it with: python tools/preview_model.py make_amphithere out.png rest fly-flap:12 fly-flap:36 (ZOOM=95).
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g   # shared El/pack/paint helpers and the texture toolkit
from make_greylag import El, mul, mix, frac
from make_greylag import cloud, grain, clamp01, plumage, feathered, rough, lift, SHINE, PUPIL

HERE = g.HERE
ROOT = g.ROOT
SUBDIR = ""                # not a bird: shapes/entity/amphithere.json
TEX = "amphithere"
g.TEX, g.SHEET_W = TEX, 128

# ---------------------------------------------------------------- palette (muted, as vanilla's animals)
def _desat(c, k=0.5, dark=0.78):
    m = sum(c) / 3
    return tuple(int((m + (v - m) * k) * dark) for v in c)

EMERALD, EMERALD_DK, TEAL, TEAL_LT = map(_desat, ((22, 150, 92), (12, 98, 64), (24, 150, 160), (80, 200, 196)))
GOLD, GOLD_DK, SCARLET, SCARLET_DK = map(_desat, ((236, 186, 52), (190, 132, 30), (214, 40, 36), (150, 24, 30)))
GOLD_LT = mix(GOLD, (240, 226, 180), 0.35)
BELLY, CREAM = _desat((242, 214, 120), 0.45, 0.7), _desat((250, 240, 200), 0.4, 0.75)
BONE, BONE_DK, SOOT = (176, 164, 136), (112, 102, 84), (34, 32, 28)
HORN, HORN_TIP = (150, 128, 82), (52, 44, 34)
EYE_AMBER = (214, 150, 40)
CY, CZ = 5.0, 8.0


# ---------------------------------------------------------------- materials
# The 0.22.0 manner: soft value-noise clouds, rows of overlapping scales and feathers (feathered), ragged blends
# (rough), baked light (lift) and grain; no regular grids or hard stripes. The scales point back along +x.
def scaled(c, a, b, light, sa=0.85, sb=0.8, fringe=0.3):
    return feathered(c, a, b, light, sa, sb, fringe, seed=3, shadow=0.8)


def green(x, y, z):
    return plumage(x, y, z, EMERALD, EMERALD_DK, TEAL, 1.8, (2.4, 0.8, 0.8))


def belly(x, y, z):
    return plumage(x, y, z, BELLY, mul(BELLY, 0.8), CREAM, 1.4)


def m_seg(x, y, z, face, fu, fv):
    if face == "up":
        c = green(x, y, z)
        c = mix(c, plumage(x, y, z, GOLD_DK, SCARLET_DK, GOLD, 1.2), 0.8 * rough(1 - abs(z - CZ) / 1.1, x, y, z, 0.6))   # dorsal band
        return scaled(c, x, z, TEAL_LT)
    if face == "down":   # broad belly scutes, a little irregular
        return feathered(belly(x, y, z), x, z, CREAM, 0.6, 2.4, 0.25, seed=5, shadow=0.84)
    if face in ("east", "west"): return grain(mix(green(x, y, z), BELLY, 0.35), x, y, z)
    t = rough((y - CY + 0.9) / 1.3, x, y, z, 0.7)                   # pale belly up the flank, ragged into the green
    c = mix(belly(x, y, z), green(x, y, z), t)
    return lift(scaled(c, x, y, mix(c, TEAL_LT, 0.5)), y, CY - 3, CY + 3)


EYE_C = (-11.75, 5.45)          # eye centre on the head's sides (x, y)


def eye(x, y):
    """A fierce eye like the eagle's: narrow, its top cut by the brow, an amber iris darker under the lid, a reptile's
    slit pupil and one pixel of shine. None outside it."""
    dx, dy = x - EYE_C[0], y - EYE_C[1]
    top = 0.42 + 0.22 * dx                                             # the lid line, lower toward the front
    if (dx / 0.9) ** 2 + (min(dy, 0) / 0.5) ** 2 > 1 or dy > top: return None
    if dy > top - 0.1: return mix(PUPIL, EMERALD_DK, 0.3)             # the heavy upper lid
    if abs(dx + 0.2) < 0.1 and abs(dy - 0.05) < 0.1: return SHINE
    if abs(dx) < 0.09 + 0.05 * (1 - abs(dy) / 0.5): return PUPIL      # the slit
    return mix(EYE_AMBER, mul(EYE_AMBER, 0.55), clamp01((dy - top + 0.35) / 0.3))


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south"):
        e = eye(x, y)
        if e: return e
        t = rough((y - 4.4) / 1.2, x, y, z, 0.6)                    # gold throat into green crown
        c = mix(plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0), green(x, y, z), t)
        c = scaled(c, x, y, mix(c, TEAL_LT, 0.4), 0.45, 0.4)
        sh = clamp01(1 - abs(y - 6.05) / 0.35) * clamp01(1 - abs(x - EYE_C[0]) / 1.2)   # the brow's shadow
        return mul(c, 1 - 0.3 * sh)
    if face == "up": return scaled(green(x, y, z), x, z, TEAL_LT, 0.5, 0.45)
    if face == "down": return scaled(plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0), x, z, GOLD_LT, 0.45, 0.5)
    return grain(mix(green(x, y, z), GOLD, 0.4), x, y, z)


def m_brow(x, y, z, face, fu, fv):
    if face == "down": return grain(mul(EMERALD_DK, 0.6), x, y, z)
    c = scaled(plumage(x, y, z, EMERALD_DK, mul(EMERALD_DK, 0.8), EMERALD, 0.8), x, z if face == "up" else y, EMERALD, 0.4, 0.35)
    return c if face == "up" else mul(c, 0.75 + 0.25 * clamp01((y - 6.3) / 0.6))


def horn(x, y, z, face):
    """Horn-gold beak darkening from mid-beak to the hooked tip, a paler ridge on top, shaded below."""
    c = mix(HORN, HORN_TIP, clamp01((-15.5 - x) / 2.8))
    c = mix(c, GOLD_DK, clamp01((x + 14.3) / 0.8))                    # gold at the base
    if face == "up": c = mul(c, 1.18)
    elif face == "down": c = mul(c, 0.72)
    elif face in ("north", "south"): c = mul(c, 0.85 + 0.2 * clamp01((y - 4.0) / 2.0))
    return grain(c, x, y, z, 0.03)


def m_beak(x, y, z, face, fu, fv):
    if face in ("north", "south") and y < 4.62 and x > -17.6: return grain(mix(PUPIL, HORN_TIP, 0.3), x, y, z)   # gape
    if face in ("north", "south") and -14.7 < x < -14.2 and 5.5 < y < 5.85: return mix(HORN, PUPIL, 0.8)   # nostril
    return horn(x, y, z, face)


def m_jaw(x, y, z, face, fu, fv): return horn(x, y, z, face if face != "up" else "south")
def m_hook(x, y, z, face, fu, fv): return horn(x, y, z, face)


def m_feather(length, axis_c, axis_s, colours, notch=True, seed=0.0):
    """A single long feather plane: tapered, a rounded tip, shaft down the middle; its colours blend raggedly from
    base to tip, streaked along it, darker at the very tip."""
    base, mid, tip = colours
    def m(x, y, z, face, fu, fv):
        c, s = axis_c(x, y, z), axis_s(x, y, z)                       # c 0..1 across, s 0 base..1 tip
        half = 0.5 * (1 - 0.15 * s) * (1 - max(0, s - 0.7) / 0.3 * 0.9)
        d = abs(c - 0.5)
        if d > half: return None
        if notch and c > 0.5 and 0.55 < s < 0.62 and d > half - 0.12: return None   # emargination on the trailing vane
        st = (0.4 * length, 1, 1)
        col = mix(plumage(x, y, z, base, mul(base, 0.8), mix(base, CREAM, 0.2), 1.0, st),
                  plumage(x, y, z, mid, mul(mid, 0.8), mix(mid, CREAM, 0.2), 1.0, st), rough((s - 0.4) / 0.2, x, y, z + seed, 0.5))
        col = mix(col, plumage(x, y, z, tip, mul(tip, 0.8), tip, 1.0, st), rough((s - 0.68) / 0.15, x, y, z + seed, 0.5))
        col = mul(col, 1 - 0.3 * clamp01((s - 0.86) / 0.14))           # darker tip
        if d < 0.06 and s < 0.85: col = mix(col, CREAM if s < 0.5 else mul(col, 0.8), 0.5)   # shaft
        elif d > half - 0.06: col = mul(col, 0.85)                      # vane edge
        return col
    return m


FIN_X0, FIN_LEN, FIN_HALF = 44.0, 14.0, 9.0


def m_tailfin(x, y, z, face, fu, fv):
    u = (x - FIN_X0) / FIN_LEN; dz = (z - CZ) / FIN_HALF
    if abs(dz) > 0.22 + 0.78 * u ** 0.7: return None                  # the fan widens backward
    ang = math.atan2(dz * FIN_HALF, max(0.01, x - FIN_X0 + 2))        # radial feathers from just ahead of the fin
    lf = frac(ang / 0.16 + 0.5)
    r = math.hypot(x - FIN_X0 + 2, z - CZ) / (FIN_LEN + 2)
    if r > 0.88 + 0.08 * math.cos(math.pi * (2 * lf - 1)) - (0.06 if (lf < 0.12 or lf > 0.88) else 0): return None
    c = plumage(x, y, z, TEAL, mul(TEAL, 0.8), TEAL_LT, 1.0, (1, 1, 1))
    c = mix(c, plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0), rough((r - 0.5) / 0.1, x, y, z, 0.4))
    c = mix(c, plumage(x, y, z, SCARLET, SCARLET_DK, SCARLET, 1.0), rough((r - 0.66) / 0.1, x, y, z, 0.4))
    c = mul(c, 1 - 0.35 * clamp01((r - 0.8) / 0.12))                   # dark tips
    if lf < 0.08 or lf > 0.92: c = mul(c, 0.78)                        # the line between feathers
    elif abs(lf - 0.5) < 0.05 and r < 0.8: c = mix(c, CREAM, 0.3)      # shaft
    return grain(c, x, y, z)


def m_spine(x, y, z, face, fu, fv):
    c = plumage(x, y, z, BONE, BONE_DK, mix(BONE, CREAM, 0.3), 0.8)
    return mul(c, 0.7) if face == "down" else (c if face == "up" else lift(c, y, CY - 1, CY + 6))


def m_claw(x, y, z, face, fu, fv): return grain(mix(SOOT, BONE_DK, 0.4 if fv < 0.5 else 0.1), x, y, z, 0.08)


BODY_HW = 3.4
ARM_X = (-2.5, 10.5); ARM_ROOT, ARM_LEN = BODY_HW - 0.3, 15.0
HAND_X = (-2.0, 8.5); HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 8.0
WING_Y = 7.4
N_PRIM, PRIM_LEN, PRIM_W, PRIM_STEP = 7, 14.0, 2.0, 11.0     # primaries: equal spacing, equal angle step
SCAP_X, SCAP_Y = (-0.5, 7.0), CY + 3.0 + 0.15


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: rows of green coverts with pale fringes, then long teal secondaries going scarlet at the trailing
    edge, cut into rounded feather tips."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8) / 1.4)
    if c > 0.94 - 0.08 * (2 * lz - 1) ** 2: return None                # rounded secondary tips
    coverts = feathered(green(x, y, z), x, abs(z - 8), TEAL_LT, 1.0, 1.0, 0.45)
    coverts = mix(coverts, plumage(x, y, z, BONE_DK, mul(BONE_DK, 0.8), BONE, 1.0), clamp01((0.1 - c) / 0.06))
    sec = plumage(x, y, z, TEAL, mul(TEAL, 0.75), TEAL_LT, 1.0, (2.6, 1, 0.5))
    sec = mix(sec, plumage(x, y, z, SCARLET, SCARLET_DK, SCARLET, 1.0), 0.75 * rough((c - 0.68) / 0.22, x, y, z, 0.8))
    edge = min(lz, 1 - lz)
    if edge < 0.12: sec = mul(sec, 0.78)                               # the line between secondaries
    elif abs(lz - 0.5) < 0.06 and c < 0.85: sec = mul(sec, 0.88)      # shaft
    return mix(coverts, sec, rough((c - 0.48) / 0.12, x, y, z, 0.6))


def m_hand(x, y, z, face, fu, fv):
    """Outer wing coverts over the primaries: gold at the leading edge into green, rounded tips at the rear."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lz = frac(abs(z - 8) / 1.2)
    if c > 0.95 - 0.12 * (2 * lz - 1) ** 2: return None
    col = mix(plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0), green(x, y, z), rough((c - 0.12) / 0.12, x, y, z, 0.5))
    return feathered(col, x, abs(z - 8), TEAL_LT, 0.9, 0.9, 0.4)


def m_edge(x, y, z, face, fu, fv):
    return plumage(x, y, z, GOLD_DK, mul(GOLD_DK, 0.8), GOLD, 1.0) if face != "down" else mul(BONE_DK, 0.8)


def m_scapulars(x, y, z, face, fu, fv):
    """Over the wing root, so the wing grows out of the back: green, pointed tips cut out at the rear."""
    end = SCAP_X[1] - x
    lz = frac(abs(z - 8.0) / 0.9)
    if end < 1.0 * abs(2 * lz - 1): return None
    return feathered(green(x, y, z), x, abs(z - 8.0), TEAL_LT, 1.2, 0.9, 0.45)


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
        mat = m_feather(PRIM_LEN, ac, as_, (TEAL, GOLD, SCARLET_DK), seed=i + (0.5 if L else 0))
        out.append(El(f"Prim{side}{i}", [x0, WING_Y + 0.01 * i, z0], [x0 + PRIM_W, WING_Y + 0.01 * i, z1], mat,
                      faces=("up",), origin=[px, WING_Y, zb if L else mz(zb)], rot=(0, ang if L else -ang, 0)))
    return out


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 3, WING_Y, z_root if L else mz(z_root)])
    edge = El("EdgeArm" + side, [ARM_X[0] - 0.4, WING_Y - 0.45, a0], [ARM_X[0] + 0.9, WING_Y + 0.45, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 2, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.5)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.2, WING_Y - 0.3, e0], [HAND_X[0] + 0.7, WING_Y + 0.3, e1], m_edge)
    return arm.add(edge, hand.add(edge_h, *primaries(side, z_tip)))


# torso fattest at Body, tapering into neck and tail; rest pose has a gentle S-curve in yaw
SEGS = [  # (name, x0, x1, half-width, half-height, rest yaw)
    ("Tail1", 8, 14, 2.8, 2.5, 6), ("Tail2", 14, 20, 2.3, 2.05, 8), ("Tail3", 20, 26, 1.85, 1.65, -3),
    ("Tail4", 26, 32, 1.45, 1.3, -10), ("Tail5", 32, 38, 1.1, 1.0, -7), ("Tail6", 38, 44, 0.8, 0.75, 6),
]


def ridge(name, x0, x1, hw, hh):
    """bony dorsal plates along a segment, thinning and tilting back"""
    out = []
    n = max(1, int((x1 - x0) / 2))
    for k in range(n):
        xa = x0 + (k + 0.25) * (x1 - x0) / n; h = 0.5 + hh * 0.55; w = max(0.3, hw * 0.28)
        out.append(El(f"{name}Spike{k}", [xa, CY + hh - 0.2, CZ - w / 2], [xa + 1.2, CY + hh + h, CZ + w / 2],
                      m_spine, origin=[xa, CY + hh, CZ], rot=(0, 0, -28)))
    return out


def box(name, x0, x1, hw, hh, mat, yaw=0, pivot=None):
    return El(name, [x0, CY - hh, CZ - hw], [x1, CY + hh, CZ + hw], mat,
              origin=[pivot if pivot is not None else x0, CY, CZ], rot=(0, yaw, 0))


def plume(name, x0, length, w, pivot, rot, colours, horizontal=False):
    seed = sum(map(ord, name)) % 97          # stable across runs (hash() is salted per process)
    if horizontal:   # lies flat, spans +-z
        return El(name, [x0, pivot[1], CZ - w / 2], [x0 + length, pivot[1], CZ + w / 2],
                  m_feather(length, lambda x, y, z: (z - CZ + w / 2) / w, lambda x, y, z: (x - x0) / length, colours, False, seed),
                  faces=("up",), origin=pivot, rot=rot)
    return El(name, [x0, pivot[1] - w / 2, CZ], [x0 + length, pivot[1] + w / 2, CZ],
              m_feather(length, lambda x, y, z: (y - pivot[1] + w / 2) / w, lambda x, y, z: (x - x0) / length, colours, False, seed),
              faces=("north",), origin=pivot, rot=rot)


def shape():
    body = box("Body", -1, 8, BODY_HW, 3.0, m_seg, pivot=3)
    body.add(*ridge("Body", -1, 8, BODY_HW, 3.0))
    chest = box("Chest", -4, -1, 2.6, 2.4, m_seg, pivot=-1)
    neck = box("Neck", -8, -4, 1.9, 1.8, m_seg, yaw=-10, pivot=-4)
    neck.add(*ridge("Neck", -8, -4, 1.9, 1.8))
    # head, brow and beak painted at double density, so the eye can be more than a square (as the birds' in 0.22.0)
    head = El("Head", [-13.5, 3.2, 5.6], [-8, 7.0, 10.4], m_head, origin=[-8, 5, 8], rot=(0, 14, -6), res=2)
    brow = El("Brow", [-12.6, 6.2, 5.4], [-9.4, 7.4, 10.6], m_brow, origin=[-9.4, 6.8, 8], rot=(0, 0, 10), res=2)
    # a raptor's bill, as the eagle's but larger: the upper bill, a narrower tip stepping forward, and the hook
    # curling down just past the lower jaw (0.22.0; the old one was a fat block tipped down with the hook hanging off)
    beak = El("Beak", [-16.8, 4.4, 7.0], [-13.5, 6.3, 9.0], m_beak, origin=[-13.5, 5.4, 8], rot=(0, 0, 6), res=2)
    tip = El("BeakTip", [-18.0, 4.7, 7.3], [-16.8, 6.1, 8.7], m_beak, res=2)
    hook = El("BeakHook", [-18.7, 3.7, 7.45], [-17.6, 5.6, 8.55], m_hook, res=2)
    jaw = El("Jaw", [-16.6, 3.8, 7.2], [-13.5, 4.4, 8.8], m_jaw, origin=[-13.5, 4.1, 8], rot=(0, 0, 6), res=2)
    beak.add(tip.add(hook)); head.add(jaw, brow)
    crest = [plume(f"Plume{i}", -10.0, 12.0 - abs(i - 2) * 1.2, 2.0, [-10.0, 7.2, CZ], (0, 0, 8 + i * 12),
                   (TEAL, GOLD, SCARLET_DK)) for i in range(5)]
    cheeks = [plume(f"Cheek{s}{i}", -9.5, 8.5, 1.6, [-9.5, 5.4, CZ + (2.3 if s == "L" else -2.3)],
                    (0, (1 if s == "L" else -1) * (-25 - i * 15), 8), (TEAL, GOLD, SCARLET), horizontal=True)
              for s in "LR" for i in range(2)]
    head.add(beak, *crest, *cheeks); neck.add(head); chest.add(neck)
    prev = body
    for name, x0, x1, hw, hh, yaw in SEGS:
        s = box(name, x0, x1, hw, hh, m_seg, yaw=yaw); s.add(*ridge(name, x0, x1, hw, hh)); prev.add(s); prev = s
    talons = []
    for side, zc in (("L", CZ + 1.6), ("R", CZ - 1.6)):
        for k in range(3):
            talons.append(El(f"Talon{side}{k}", [2.5 + k * 1.1, CY - 4.3, zc - 0.3], [3.1 + k * 1.1, CY - 2.8, zc + 0.3], m_claw,
                             origin=[2.8 + k * 1.1, CY - 2.8, zc], rot=(0, 0, -30)))
    body.add(*talons)
    fin = El("TailFin", [FIN_X0, CY + 0.05, CZ - FIN_HALF], [FIN_X0 + FIN_LEN, CY + 0.05, CZ + FIN_HALF], m_tailfin,
             faces=("up",), origin=[FIN_X0, CY, CZ])
    prev.add(fin)
    # scapulars lie over each wing root on the back (children of the body: they stay put while the wings beat)
    z_root = 8 + ARM_ROOT
    scaps = [El("Scapulars" + s, [SCAP_X[0], SCAP_Y, a], [SCAP_X[1], SCAP_Y, b], m_scapulars, faces=("up",),
                origin=[SCAP_X[0], SCAP_Y, z_root if s == "L" else mz(z_root)], rot=(0, 0, -3))
             for s, (a, b) in (("L", (z_root - 1.2, z_root + 3.0)), ("R", (mz(z_root + 3.0), mz(z_root - 1.2))))]
    body.add(chest, wing("L"), wing("R"), *scaps)
    return body


# ---------------------------------------------------------------- animation
def key(frame):
    p = frame / 48 * 2 * math.pi
    # 0.22.0: the stroke deepened to about 90 degrees, as the birds' in 0.21.2 (was 6 +- 30, a 60 degree arc)
    arm = 6 + 45 * math.sin(p); hand = 22 * math.sin(p - 1.1)   # hand lags the arm
    els = {"ArmL": {"rotationX": round(-arm, 2)}, "ArmR": {"rotationX": round(arm, 2)},
           "WingL": {"rotationX": round(-hand, 2)}, "WingR": {"rotationX": round(hand, 2)},
           "Body": {"offsetY": round(-0.25 * math.sin(p), 3)}}
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
    repaint = "--texture" in sys.argv
    shapes = os.path.join(ROOT, "shapes", "entity"); textures = os.path.join(ROOT, "textures", "entity")
    os.makedirs(shapes, exist_ok=True); os.makedirs(textures, exist_ok=True)
    root = shape(); height = g.pack(root)
    data = {"textureWidth": g.SHEET_W // g.PX, "textureHeight": height // g.PX,
            "textures": {TEX: "flocks:entity/" + TEX},
            "elements": [root.json()], "animations": g.complete_axes(animations())}
    with open(os.path.join(shapes, TEX + ".json"), "w", encoding="utf-8", newline="\n") as f: json.dump(data, f, indent=1)
    print("wrote", os.path.normpath(os.path.join(shapes, TEX + ".json")))
    path = os.path.join(textures, TEX + ".png")
    img = g.paint(root, height)
    if repaint or not os.path.exists(path):
        img.save(path); print("painted", os.path.normpath(path), img.size)
    else:
        print("kept existing texture", os.path.normpath(path), "(pass --texture to repaint)")
    g.uv_guide(root, img).save(os.path.join(HERE, "amphithere_uv_guide.png"))
    print("elements", sum(1 for _ in root.walk()))


if __name__ == "__main__":
    main()
