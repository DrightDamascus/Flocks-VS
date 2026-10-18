"""Generates the golden eagle shape, texture and animations for Flocks.

Run from the repo root:
  python tools/make_eagle.py            shape + animations; texture only if it does not exist yet
  python tools/make_eagle.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/eagle.json, textures/entity/bird/eagle.png and tools/eagle_uv_guide.png.

Built from make_greylag.py's pieces (elements, UV packing, painting, keyframe helpers). Its own geometry: a robust body,
big head on a short neck, hooked bill, long square tail, feathered legs with talons, and long broad wings with seven
separated primaries ("fingers") at each tip. Its own animations, for flocks.raptor: soaring (wings flat in a slight
dihedral), powered flight, the stoop (wings swept back and half folded, nose down), takeoff, landing with the talons
thrust forward, perched upright scanning, mantling over a kill, a walk, and death.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac

HERE = g.HERE
ROOT = g.ROOT
TEX = "eagle"
g.TEX = TEX

# ---------------------------------------------------------------- palette (golden eagle, adult)
BROWN_DK, BROWN, BROWN_LT = (50, 37, 27), (78, 58, 41), (112, 86, 60)
TAWNY = (152, 120, 82)                      # the pale bar across the upper wing coverts
GOLD, GOLD_DK, GOLD_LT = (190, 148, 78), (148, 110, 58), (214, 178, 108)
TROUSERS, TROUSERS_LT = (120, 92, 64), (160, 128, 90)
TAIL_G, TAIL_B = (98, 86, 72), (48, 38, 30)
CERE, BILL, BILL_TIP = (222, 192, 76), (92, 90, 92), (36, 34, 36)
FEET, CLAW = (218, 186, 70), (30, 28, 28)
EYE, PUPIL = (132, 82, 34), (20, 16, 14)
PRIM, PRIM_LT = (40, 33, 28), (72, 64, 56)
SECOND = (62, 54, 46)


# the texture toolkit (noise, plumage, feathered, rough, lift, grain) lives in make_greylag.py, shared by every bird
from make_greylag import _h, vnoise, cloud, grain, clamp01, plumage, feathered, rough, lift


# ---------------------------------------------------------------- materials
def m_body(x, y, z, face, fu, fv):
    if face == "up": return feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), x, z, TAWNY, 1.4, 1.1, 0.4)
    if face in ("down", "east", "west"):
        return feathered(plumage(x, y, z, BROWN_DK, mul(BROWN_DK, 0.8), BROWN, 1.2), x, z + y, BROWN, 1.1, 0.9, 0.35)
    return lift(feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), x, y, BROWN_LT, 1.3, 1.0, 0.4), y, 2.2, 6.8)


def m_breast(x, y, z, face, fu, fv):
    if face == "up": return plumage(x, y, z, GOLD_DK, BROWN, GOLD, 1.0)
    # dark chest, streaked downward
    return feathered(plumage(x, y, z, BROWN_DK, mul(BROWN_DK, 0.8), BROWN, 1.2, (0.8, 2.2, 0.8)), -y, z + x, BROWN, 0.9, 0.8, 0.4)


def m_belly(x, y, z, face, fu, fv):
    return feathered(plumage(x, y, z, mul(BROWN_DK, 0.92), mul(BROWN_DK, 0.75), BROWN_DK, 1.2), x, z + y, BROWN, 0.9, 0.8, 0.3)


def m_rump(x, y, z, face, fu, fv):
    if face == "up": return feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), x, z, TAWNY, 1.2, 1.0, 0.4)
    return lift(feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), x, y, BROWN_LT, 1.2, 1.0, 0.4), y, 3.4, 6.4)


def m_neck(x, y, z, face, fu, fv):
    # the golden hackles on the back and sides of the neck, fading raggedly to brown on the throat
    if face in ("down", "west"): return plumage(x, y, z, BROWN_DK, mul(BROWN_DK, 0.8), BROWN)
    t = 1.0 if face == "up" else rough((y - 4.6) / 1.8, x, y, z, 0.7)
    gold = plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.2, (2.4, 0.6, 0.6))
    c = mix(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), gold, t)
    return feathered(c, x, z if face == "up" else y, GOLD_LT, 1.1, 0.55, 0.35)        # narrow, pointed hackles


EYE_C = (-2.95, 5.95)          # eye centre on the head's sides (x, y)
EYE_GOLD = (222, 160, 46)      # artistic liberty (Dright): an adult's eye is brown; a bright gold eye reads fierce
SHINE = (236, 212, 172)        # the eye's catch-light: a pale tan (Dright: light brown, not white), bright enough to show
BROW_TILT = 12                 # the brow slopes down toward the bill: the scowl


def eye(x, y):
    """A fierce eye: narrow, its top cut by the brow sloping down toward the bill, a gold iris shaded under the lid
    and a small pupil with a single pixel of shine. No ring; a round, ringed eye looked creepy (Dright). None outside it."""
    dx, dy = x - EYE_C[0], y - EYE_C[1]
    top = 0.32 + 0.28 * dx                                             # the lid line, lower toward the front
    if (dx / 0.7) ** 2 + (min(dy, 0) / 0.44) ** 2 > 1 or dy > top: return None
    if dy > top - 0.1: return mix(PUPIL, BROWN_DK, 0.3)               # the heavy upper lid
    if abs(dx + 0.075) < 0.1 and abs(dy - 0.075) < 0.1: return SHINE  # one pixel of shine on the pupil's upper front
    if (dx + 0.04) ** 2 + (dy - 0.02) ** 2 < 0.21 ** 2: return PUPIL
    return mix(EYE_GOLD, mul(EYE_GOLD, 0.55), clamp01((dy - top + 0.35) / 0.3))   # darker under the lid


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south"):
        e = eye(x, y)
        if e: return e
        if abs(y - 5.15 + 0.25 * (x + 3.9)) < 0.13 and x < -3.0: return grain(PUPIL, x, y, z)   # gape line, rising back
        t = rough(clamp01((x + 3.2) / 2.2) * clamp01((y - 5.0) / 1.6), x, y, z, 0.6)
        c = mix(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT, 1.0), plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0), t)
        c = feathered(c, x, y, mix(c, GOLD_LT, 0.4), 0.45, 0.35, 0.3)
        # the brow's shadow over and behind the eye
        sh = clamp01(1 - abs(y - (6.35 + 0.22 * (x - EYE_C[0]))) / 0.3) * clamp01(1 - abs(x - EYE_C[0]) / 1.1)
        return mul(c, 1 - 0.3 * sh)
    if face == "up":
        t = rough((x + 3.6) / 2.4, x, y, z, 0.6)                          # crown darker gold, nape bright gold
        c = mix(plumage(x, y, z, GOLD_DK, BROWN, GOLD, 1.0), plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0), t)
        return feathered(c, x, z, GOLD_LT, 0.5, 0.4, 0.3)
    if face == "east": return feathered(plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0), -y, z, GOLD_LT, 0.6, 0.45, 0.3)
    return plumage(x, y, z, BROWN_DK, mul(BROWN_DK, 0.8), BROWN, 1.0)


def m_brow(x, y, z, face, fu, fv):
    if face == "up": return feathered(plumage(x, y, z, GOLD_DK, BROWN, GOLD, 1.0), x, z, GOLD, 0.5, 0.4, 0.3)
    if face == "down": return grain(mul(BROWN_DK, 0.6), x, y, z)
    c = feathered(plumage(x, y, z, BROWN_DK, mul(BROWN_DK, 0.8), BROWN, 0.8), x, y, BROWN, 0.45, 0.35, 0.3)
    return mul(c, 0.75 + 0.25 * clamp01((y - 6.45) / 0.5))                  # darker along the overhanging edge


def bill_colour(x, y, z, face):
    """Yellow cere at the base, horn-grey bill darkening to a black tip, a paler ridge along the top."""
    c = mix(BILL, BILL_TIP, clamp01((-4.6 - x) / 1.1))
    c = mix(c, CERE, clamp01((x + 4.85) / 0.25))
    if face == "up": c = mul(c, 1.18)                                         # the culmen catches the light
    elif face == "down": c = mul(c, 0.72)
    elif face in ("north", "south"):
        c = mul(c, 0.85 + 0.2 * clamp01((y - 5.0) / 1.4))                    # rounder: lit on top, shaded below
        if -4.5 < x < -4.15 and 5.7 < y < 6.0: return mix(CERE, PUPIL, 0.8)   # the nostril, in the cere
    return grain(c, x, y, z, 0.03)


def m_bill(x, y, z, face, fu, fv):
    return bill_colour(x, y, z, face)


def m_jaw(x, y, z, face, fu, fv):
    return bill_colour(x, y, z, face if face != "up" else "south")


def m_hook(x, y, z, face, fu, fv):
    c = mix(mix(BILL, BILL_TIP, 0.6), BILL_TIP, clamp01((5.6 - y) / 0.8))
    if face == "up": c = mul(c, 1.15)
    return grain(c, x, y, z, 0.03)


def m_legs(x, y, z, face, fu, fv):
    # shaggy feathered "trousers", a little paler than the body, down to the toes
    c = plumage(x, y, z, TROUSERS, mul(TROUSERS, 0.75), TROUSERS_LT, 0.9, (2.0, 0.8, 0.8))
    c = feathered(c, x, z + y, TROUSERS_LT, 0.8, 0.6, 0.45)
    return mul(c, 0.82) if face == "down" else c


def m_edge(x, y, z, face, fu, fv):
    return plumage(x, y, z, BROWN_DK, mul(BROWN_DK, 0.8), BROWN, 1.2)


def m_tail(x, y, z, face, fu, fv):
    """Long, broad, square tail: grey-brown with soft, wavy darker bands and a broad dark tip; feather tips cut out."""
    end = 16.5 - x
    lz = frac((z - 5.8) / 1.1)
    if end < 0.3 * (2 * lz - 1) ** 2: return None                      # each feather's tip slightly rounded
    if end < 0.3 * abs(z - 8.0) / 2.2 - 0.05: return None              # very slightly rounded corners
    phase = x / 1.7 + (cloud(x, 0, z, 1.2) - 0.5) * 0.6
    band = (0.5 + 0.5 * math.cos(2 * math.pi * phase)) ** 3
    c = mix(TAIL_G, TAIL_B, 0.55 * band)
    c = mix(c, TAIL_B, rough((1.9 - end) / 0.8, x, 0, z, 0.4))         # the dark terminal band
    c = mul(c, 1 - 0.14 * (1 - min(lz, 1 - lz) * 6) if min(lz, 1 - lz) < 1 / 6 else 1)   # faint gaps between feathers
    return grain(c, x, y, z)


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: mottled brown coverts with a ragged pale tawny bar, then long dark secondaries (streaked along their
    length) with a slightly bulging (S-curved) trailing edge, cut into feather tips."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.2)
    trail = 0.9 + 0.1 * math.sin(math.pi * min(1.0, s * 1.15))         # secondaries bulge mid-wing
    if c > trail - 0.07 * (2 * lz - 1) ** 2: return None               # rounded secondary tips
    coverts = plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT, 1.3)
    bar = rough(1 - abs(c - 0.27) / 0.15, x, y, z, 0.9, 0.8)
    coverts = mix(coverts, plumage(x, y, z, TAWNY, BROWN_LT, mix(TAWNY, GOLD_LT, 0.4), 1.0), 0.85 * bar)
    coverts = feathered(coverts, x, abs(z - 8.0), mix(TAWNY, GOLD_LT, 0.3), 1.0, 1.0, 0.5)   # rows of pale-tipped coverts
    coverts = mix(coverts, grain(BROWN_DK, x, y, z), clamp01((0.12 - c) / 0.08))
    sec = plumage(x, y, z, SECOND, PRIM, mix(SECOND, TAIL_G, 0.5), 1.0, (2.6, 1, 0.5))
    edge = min(lz, 1 - lz)
    if edge < 0.12: sec = mul(sec, 0.78)                               # the line between secondaries
    elif abs(lz - 0.5) < 0.06 and c < 0.85: sec = mul(sec, 0.88)      # shaft
    sec = mul(sec, 1 - 0.25 * clamp01((c - (trail - 0.2)) / 0.12))     # darker tips
    return mix(coverts, sec, rough((c - 0.5) / 0.12, x, y, z, 0.6))


FINGERS = 7


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: coverts, then long dark primaries; past 60% of the hand the outer seven separate into fingers."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.08 * s ** 2
    trail = 1.0 - 0.3 * s ** 2
    if s > 0.995 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)
    f = frac(k * FINGERS)
    if s > 0.6:
        gap = 0.08 + 0.55 * (s - 0.6) / 0.4          # the gaps between fingers widen toward the tip
        if f < gap / 2 or f > 1 - gap / 2: return None
    prim = plumage(x, y, z, PRIM, mul(PRIM, 0.8), PRIM_LT, 1.0, (0.6, 1, 2.6))   # streaked along the feathers
    prim = mix(prim, PRIM_LT, 0.35 * clamp01((0.55 - s) / 0.4))      # paler, greyer feather bases
    if f < 0.1 or f > 0.93: prim = mul(prim, 0.72)                    # the line between primaries
    elif abs(f - 0.45) < 0.07: prim = mul(prim, 1.15)                 # pale shaft
    coverts = feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT, 1.3), x, abs(z - 8.0), TAWNY, 1.0, 1.0, 0.45)
    return mix(prim, coverts, rough((0.6 - s) / 0.15, x, y, z, 0.5) * rough((0.32 - k) / 0.1, x, y, z, 0.5))


def m_toe(x, y, z, face, fu, fv):
    # yellow toes with faint scale rings across them
    c = mul(FEET, 0.82) if face == "down" else FEET
    return grain(mul(c, 0.88) if frac(x * 2.5) < 0.2 else c, x, y, z, 0.05)


def m_claw(x, y, z, face, fu, fv):
    return grain(CLAW, x, y, z, 0.08)


def m_plume(x, y, z, face, fu, fv):
    """Shaggy feathers hanging off the back of the trousers: ragged pointed tips cut out along the lower edge."""
    depth = y - PLUME_Y[0]                                            # 0 at the trouser, growing out from it
    tip = PLUME_Y[1] - PLUME_Y[0]
    lx = frac(x / 0.7)
    if depth > tip * (1 - 0.6 * abs(2 * lx - 1)) - 0.25 * cloud(x, 0, z, 0.5): return None
    return feathered(plumage(x, y, z, TROUSERS, mul(TROUSERS, 0.75), TROUSERS_LT, 0.9, (0.8, 2.0, 0.8)), y, x, TROUSERS_LT, 0.8, 0.7, 0.4)


def m_hackles(x, y, z, face, fu, fv):
    """The golden nape: long pointed hackles trailing back over the neck."""
    end = HACKLE_X[1] - x
    lz = frac((z - 8.0) / 0.8 + 0.5)
    if end < 1.4 * abs(2 * lz - 1) + 0.6 * abs(z - 8.0) / 1.2: return None
    return feathered(plumage(x, y, z, GOLD, GOLD_DK, GOLD_LT, 1.0, (2.4, 0.6, 0.6)), x, z, GOLD_LT, 1.2, 0.6, 0.35)


def m_scapulars(x, y, z, face, fu, fv):
    """Scapulars over the wing root: mottled brown with paler tips, pointed tips cut out at the rear."""
    end = SCAP_X[1] - x
    lz = frac(abs(z - 8.0) / 0.9)
    if end < 1.0 * abs(2 * lz - 1): return None
    c = feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT, 1.0, (2.0, 1, 0.7)), x, abs(z - 8.0), TAWNY, 1.3, 0.9, 0.45)
    return mix(c, TAWNY, 0.35 * rough((1.2 - end) / 0.6, x, y, z, 0.6))


# ---------------------------------------------------------------- geometry
ARM_X = (2.4, 11.0)
ARM_ROOT, ARM_LEN = 2.8, 9.6
HAND_X = (3.0, 10.6)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 10.0
WING_Y = 6.1
mz = g.mz
HIP = (8.0, 3.4)
LEG = 3.2
BODY_FROM, BODY_TO = (2.0, 2.2, 5.3), (10.5, 6.8, 10.7)
BODY_ORIGIN = ((BODY_FROM[0] + BODY_TO[0]) / 2, (BODY_FROM[1] + BODY_TO[1]) / 2)
PLUME_Y = (4.2, 5.4)          # the trouser plume, out from the back of the leg (rest pose: the leg lies along +x)
HACKLE_X = (-1.8, 1.4)
SCAP_X = (3.2, 9.0)
SCAP_Y = BODY_TO[1] + 0.15     # just over the back


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 3, WING_Y, z_root if L else mz(z_root)])
    under = El("Arm" + side + "u", arm.frm, arm.to, m_arm, faces=("up",), uv_from=arm,
               origin=[ARM_X[0], WING_Y, arm.origin[2]], rot=(0, 0, 5))
    # a chunky leading edge (the "shoulder" and forearm), so the wing has some body from the side and front
    edge_a = El("EdgeArm" + side, [ARM_X[0] - 0.3, WING_Y - 0.4, a0], [ARM_X[0] + 1.0, WING_Y + 0.4, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 2.5, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.45)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.2, WING_Y - 0.3, e0], [HAND_X[0] + 0.7, WING_Y + 0.3, e1], m_edge)
    # scapulars: a feathered plane lying over the wing root, so the wing grows out of the back instead of a seam
    s0, s1 = zr(z_root - 1.2, z_root + 3.6)
    scap = El("Scapulars" + side, [SCAP_X[0], SCAP_Y, s0], [SCAP_X[1], SCAP_Y, s1], m_scapulars, faces=("up",),
              origin=[SCAP_X[0], SCAP_Y, z_root if L else mz(z_root)], rot=(0, 0, -3))
    return arm.add(under, edge_a, scap, hand.add(edge_h))


def foot(side, zc):
    """A foot built like vanilla's hawk's: a pad, three splayed front toes and a hind toe, each with its own talon.
    Rest pose (leg along +x): the toes point +x and the talons curl toward +y; standing, the foot is turned 180
    degrees, so the toes point forward and the talons curl down into the ground."""
    pad = El("Foot" + side, [11.0, 2.75, zc - 0.7], [12.0, 3.35, zc + 0.7], m_toe)
    kids = []
    for i, (dz, yaw, length) in enumerate(((-0.45, 24, 1.6), (0.0, 0, 1.8), (0.45, -24, 1.6))):
        z = zc + dz
        toe = El("Toe%s%d" % (side, i + 1), [11.8, 2.8, z - 0.25], [11.8 + length, 3.3, z + 0.25], m_toe,
                 origin=[11.8, 3.05, z], rot=(0, yaw, 0))
        tx = 11.8 + length
        toe.add(El("Claw%s%d" % (side, i + 1), [tx - 0.1, 2.85, z - 0.15], [tx + 0.6, 3.25, z + 0.15], m_claw,
                   origin=[tx - 0.1, 3.05, z], rot=(0, 0, 50)))
        kids.append(toe)
    hind = El("Toe%sBack" % side, [10.0, 2.8, zc - 0.25], [11.2, 3.3, zc + 0.25], m_toe, origin=[11.2, 3.05, zc])
    hind.add(El("Claw%sBack" % side, [9.4, 2.85, zc - 0.15], [10.1, 3.25, zc + 0.15], m_claw,
                origin=[10.1, 3.05, zc], rot=(0, 0, -50)))
    return pad.add(*kids, hind)


def shape():
    body = El("Body", BODY_FROM, BODY_TO, m_body)
    breast = El("Breast", [0.5, 2.7, 5.8], [2.0, 6.5, 10.2], m_breast)
    belly = El("Belly", [3.0, 1.6, 6.0], [9.0, 2.2, 10.0], m_belly)
    rump = El("Rump", [10.5, 3.6, 6.2], [12.0, 6.4, 9.8], m_rump)
    neck = El("Neck", [-0.9, 4.3, 6.3], [1.2, 6.9, 9.7], m_neck, origin=[1.2, 5.6, 8])
    head = El("Head", [-3.9, 4.4, 6.4], [-0.9, 7.4, 9.6], m_head, origin=[-0.9, 5.8, 8], res=2)
    # the heavy brow ridge over the eyes gives the eagle its scowl; it juts out a little past the head's sides
    brow = El("Brow", [-4.0, 6.5, 6.25], [-2.0, 7.2, 9.75], m_brow, res=2, origin=[-2.0, 7.0, 8], rot=(0, 0, BROW_TILT))
    bill = El("Bill", [-5.3, 5.0, 7.3], [-3.9, 6.4, 8.7], m_bill, res=2)
    jaw = El("Jaw", [-5.0, 4.6, 7.5], [-3.9, 5.0, 8.5], m_jaw, res=2)
    hook = El("BillHook", [-5.8, 4.5, 7.6], [-5.3, 5.9, 8.4], m_hook, res=2)   # the tip curling down past the jaw
    # the golden hackles trail back from the nape over the neck, like the hawk's head plumes
    hackles = El("Hackles", [HACKLE_X[0], 7.3, 6.8], [HACKLE_X[1], 7.3, 9.2], m_hackles, faces=("up",),
                 origin=[HACKLE_X[0], 7.3, 8], rot=(0, 0, -24))
    # the tail in three planes like the hawk's, the outer ones fanned a little under the middle one
    tail = El("Tail", [10.5, 5.4, 6.6], [16.5, 5.4, 9.4], m_tail, faces=("up",), origin=[10.5, 5.4, 8], rot=(0, 0, 2))
    tail_l = El("TailL", [10.5, 5.3, 8.2], [16.3, 5.3, 10.4], m_tail, faces=("up",), origin=[10.5, 5.3, 8.6], rot=(0, -6, 0))
    tail_r = El("TailR", [10.5, 5.3, 5.6], [16.3, 5.3, 7.8], m_tail, faces=("up",), origin=[10.5, 5.3, 7.4], rot=(0, 6, 0))
    tail.add(tail_l, tail_r)
    legs = []
    for side, zc in (("L", 9.1), ("R", 6.9)):
        leg = El("Leg" + side, [HIP[0], 2.6, zc - 0.8], [HIP[0] + LEG, 4.2, zc + 0.8], m_legs, origin=[HIP[0], HIP[1], zc])
        plume = El("LegPlume" + side, [HIP[0] + 0.4, PLUME_Y[0], zc], [HIP[0] + LEG + 0.4, PLUME_Y[1], zc], m_plume,
                   faces=("north",))
        legs.append(leg.add(plume, foot(side, zc)))
    body.add(breast, belly, rump, neck.add(head.add(brow, bill.add(hook), jaw, hackles)), tail, *legs, wing("L"), wing("R"))
    return body

# ---------------------------------------------------------------- animation
def stance(body, swing_l=0.0, swing_r=0.0):
    """Ground legs for a body tilt: legs straight down plus swing, talons flat and forward, body lifted so the feet
    rest at y = 0 (as for the heron)."""
    t = math.radians(body)
    hip_y = BODY_ORIGIN[1] + (HIP[0] - BODY_ORIGIN[0]) * math.sin(t) + (HIP[1] - BODY_ORIGIN[1]) * math.cos(t)
    lowest = min(math.sin(math.radians(-90 + sw)) for sw in (swing_l, swing_r))
    lift = round(-(hip_y + LEG * lowest - 0.5), 2)
    return {"Body": {"rotationZ": body, "offsetY": lift},
            "LegL": {"rotationZ": -90 + swing_l - body}, "LegR": {"rotationZ": -90 + swing_r - body},
            "FootL": {"rotationZ": -90 - swing_l}, "FootR": {"rotationZ": -90 - swing_r}}


# Folded wings, as the greylag's but drawn in against the flanks: the eagle's body is wider than the arm's root, so
# the vanilla-style fold left the leading edges standing out like a cape. Tucked in and down, the edge lies along
# the flank and the long primaries cross over the tail.
FOLDED = {"ArmL": {"rotationX": -6, "rotationY": 95, "offsetZ": -1.4, "offsetY": -0.3},
          "ArmR": {"rotationX": 6, "rotationY": -95, "offsetZ": 1.4, "offsetY": -0.3},
          "WingL": {"rotationY": 172}, "WingR": {"rotationY": -172}}


def ground(frame, body, head=(0, 0, 0), neck=(0, 0, 0), wings=None, swing=(0.0, 0.0)):
    els = {k: dict(v) for k, v in FOLDED.items()}
    if wings: els.update(wings)
    els.update(stance(body, *swing))
    els["Neck"] = {"rotationX": neck[0], "rotationY": neck[1], "rotationZ": neck[2]}
    els["Head"] = {"rotationX": head[0], "rotationY": head[1], "rotationZ": head[2]}
    return {"frame": frame, "elements": els}


TUCKED = {"LegL": {"rotationZ": 6}, "LegR": {"rotationZ": 6}, "FootL": {"rotationZ": 20}, "FootR": {"rotationZ": 20}}


def air(frame, arm, hand, extra=None):
    k = g.key(frame, arm, hand)
    k["elements"].update(TUCKED)
    if extra: k["elements"].update(extra)
    return k


def animations():
    # powered flight: deep, strong beats
    flap = [air(0, 0, -16, {"Body": {"rotationZ": 1}}), air(8, 42, 10, {"Body": {"rotationZ": 0}}),
            air(16, 0, 22, {"Body": {"rotationZ": -1}}), air(24, -38, -10, {"Body": {"rotationZ": 0}})]   # deeper in 0.21.2
    # soaring: wings held flat in a slight dihedral, the hand barely rocking
    soar = [air(0, 7, 3), air(20, 5, 5), air(40, 7, 3)]
    # the stoop: wings swept back and half folded, nose down, talons tucked
    def stoop_key(frame, sweep):
        k = air(frame, 10, -20, {"Body": {"rotationZ": 22}})
        e = k["elements"]
        e["ArmL"]["rotationY"] = sweep; e["ArmR"]["rotationY"] = -sweep
        e["WingL"]["rotationY"] = sweep * 1.6; e["WingR"]["rotationY"] = -sweep * 1.6
        return k
    stoop = [stoop_key(0, 48), stoop_key(10, 52)]
    # takeoff: big strokes, legs dangling
    dangle = {"LegL": {"rotationZ": -60}, "LegR": {"rotationZ": -70}, "FootL": {"rotationZ": -30}, "FootR": {"rotationZ": -30}}
    def takeoff(frame, arm, hand):
        k = g.key(frame, arm, hand); k["elements"].update(dangle); k["elements"]["Body"] = {"rotationZ": -18}
        return k
    # landing: wings up and cupped, talons thrust forward
    def flare(frame, arm, hand):
        k = g.key(frame, arm, hand)
        k["elements"].update({"Body": {"rotationZ": -28}, "LegL": {"rotationZ": -130}, "LegR": {"rotationZ": -130},
                              "FootL": {"rotationZ": -40}, "FootR": {"rotationZ": -40}})
        k["elements"]["ArmL"]["rotationY"] = -14; k["elements"]["ArmR"]["rotationY"] = 14
        return k
    # perched upright, the head turning to scan
    # upright, about 52 deg from horizontal; neck and head tipped back so the head stays level
    up = dict(neck=(0, 0, 22))
    perch = [ground(0, -52, head=(0, 0, 30), **up), ground(20, -52, head=(0, 30, 30), **up), ground(45, -52, head=(0, 30, 30), **up),
             ground(65, -52, head=(0, -30, 30), **up), ground(90, -52, head=(0, -30, 30), **up), ground(110, -52, head=(0, 0, 30), **up)]
    # mantling over a kill: wings half open and drooped over it, head down to feed
    mantle = {"ArmL": {"rotationX": 22, "rotationY": 40}, "ArmR": {"rotationX": -22, "rotationY": -40},
              "WingL": {"rotationX": 10, "rotationY": 70}, "WingR": {"rotationX": -10, "rotationY": -70}}
    eat = [ground(0, 8, head=(0, 0, 30), neck=(0, 0, 25), wings=mantle), ground(8, 10, head=(0, 0, 50), neck=(0, 0, 35), wings=mantle),
           ground(16, 8, head=(0, 0, 30), neck=(0, 0, 25), wings=mantle)]
    walk = [ground(0, -35, head=(0, 0, 20), neck=(0, 0, 15), swing=(18, -12)), ground(8, -35, head=(0, 0, 20), neck=(0, 0, 15)),
            ground(16, -35, head=(0, 0, 20), neck=(0, 0, 15), swing=(-12, 18)), ground(24, -35, head=(0, 0, 20), neck=(0, 0, 15))]
    def dead(frame, **parts):
        k = g.ground_key(frame, **parts)
        k["elements"].update({n: dict(v) for n, v in FOLDED.items()})
        return k
    death = [dead(0, Body=(0, 0, 0)), dead(6, Body=(35, 0, 0), Neck=(0, 0, 10), Head=(0, 0, 10)),
             dead(14, Body=(82, 0, 0), Neck=(0, 0, 20), Head=(0, 0, 15), LegL=(0, 0, -30), LegR=(0, 0, -15)),
             dead(23, Body=(82, 0, 0), Neck=(0, 0, 20), Head=(0, 0, 15), LegL=(0, 0, -30), LegR=(0, 0, -15))]
    return [
        {"name": "fly-flap", "code": "fly-flap", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": flap},
        {"name": "fly-soar", "code": "fly-soar", "quantityframes": 60, "onAnimationEnd": "Repeat", "keyframes": soar},
        {"name": "stoop", "code": "stoop", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": stoop},
        {"name": "takeoff", "code": "takeoff", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [
            takeoff(0, 0, -22), takeoff(5, 55, 10), takeoff(10, 0, 26), takeoff(15, -45, -10)]},
        {"name": "land", "code": "land", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [flare(0, 40, -16), flare(10, 32, -10)]},
        {"name": "idle-stand", "code": "idle-stand", "quantityframes": 130, "onAnimationEnd": "Repeat", "keyframes": perch},
        {"name": "eat", "code": "eat", "quantityframes": 24, "onAnimationEnd": "Repeat", "keyframes": eat},
        {"name": "walk", "code": "walk", "quantityframes": 32, "onAnimationEnd": "Repeat", "keyframes": walk},
        {"name": "death", "code": "death", "quantityframes": 24, "onActivityStopped": "PlayTillEnd", "onAnimationEnd": "Hold", "keyframes": death},
    ]


def main():
    repaint = "--texture" in sys.argv
    shapes = os.path.join(ROOT, "shapes", "entity", "bird")
    textures = os.path.join(ROOT, "textures", "entity", "bird")
    os.makedirs(shapes, exist_ok=True); os.makedirs(textures, exist_ok=True)
    root = shape()
    height = g.pack(root)
    data = {
        "textureWidth": g.SHEET_W // g.PX, "textureHeight": height // g.PX,
        "textures": {TEX: "flocks:entity/bird/" + TEX},
        "elements": [root.json()],
        "animations": g.complete_axes(animations()),
    }
    with open(os.path.join(shapes, TEX + ".json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1)
    print("wrote", os.path.normpath(os.path.join(shapes, TEX + ".json")))
    path = os.path.join(textures, TEX + ".png")
    img = g.paint(root, height)
    if repaint or not os.path.exists(path):
        img.save(path); print("painted", os.path.normpath(path), img.size)
    else:
        print("kept existing texture", os.path.normpath(path), "(pass --texture to repaint)")
    g.uv_guide(root, img).save(os.path.join(HERE, "eagle_uv_guide.png"))
    zs = [v for e in root.walk() for v in (e.frm[2], e.to[2])]
    print("wingspan", round((max(zs) - min(zs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
