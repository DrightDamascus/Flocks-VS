"""Generates the mallard shape, its drake and hen textures, and its animations for Flocks.

Run from the repo root:
  python tools/make_mallard.py            shape + animations; textures only if they don't exist yet
  python tools/make_mallard.py --texture  also (re)paint both textures, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/mallard.json, textures/entity/bird/mallard-drake.png and
mallard-hen.png (one UV layout, two paint jobs: the entity picks one at random), and tools/mallard_uv_guide.png.

Built from make_greylag.py's pieces (elements, UV packing, painting, animations) with duck proportions: a plump body,
short neck, round head, broad flat bill, short pointed tail and shorter, pointed wings. The element names match the
greylag's, so every greylag animation (flight, swim, walk, takeoff, landing, death) works unchanged. The drake also
has the black curled feathers over its tail (TailCurl, not animated).
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac, plumage, feathered, rough, lift, grain, clamp01, round_eye, cloud

HERE = g.HERE
ROOT = g.ROOT
TEX = "mallard"
g.TEX = TEX                    # El.json names faces' texture "#" + make_greylag.TEX

# ---------------------------------------------------------------- palettes
DRAKE, HEN = "drake", "hen"
SEX = DRAKE                    # which paint job paint() does; main() paints both

# drake
GREEN, GREEN_LT, GREEN_DK = (34, 86, 50), (52, 122, 72), (22, 58, 36)
COLLAR = (236, 236, 230)
CHESTNUT, CHESTNUT_DK = (112, 60, 44), (86, 44, 32)
VERM, VERM_DK = (184, 184, 178), (150, 150, 146)      # vermiculated grey flanks
BACK_D, BACK_D_DK = (128, 118, 104), (100, 92, 82)
BELLY_D = (204, 204, 198)
STERN = (28, 28, 32)                                  # black under and over the tail
TAIL_D = (222, 222, 216)
BILL_D, NAIL = (204, 186, 74), (60, 58, 44)
# hen
BUFF, BROWN, BROWN_DK = (190, 154, 110), (124, 92, 62), (78, 58, 40)
HEAD_H, STRIPE = (168, 136, 100), (72, 54, 38)
BILL_H, SADDLE = (214, 130, 60), (70, 56, 40)
# both
SPEC, SPEC_LT = (54, 70, 168), (84, 104, 200)         # the blue speculum
BAR = (240, 240, 236)                                 # white bars either side of it
WING_G, WING_G_DK, PRIM = (138, 130, 120), (112, 106, 98), (104, 98, 92)
FEET = (230, 130, 50)
EYE = (24, 22, 20)


def drake(): return SEX == DRAKE


# ---------------------------------------------------------------- materials (see make_greylag for the convention)
# Painted with make_greylag's texture toolkit (plumage, feathered, rough...): soft feathering in vanilla's manner.
def hen_feathers(x, y, z, a, b, size=1.0):
    """The hen's (and the drake's back's) pattern: brown feathers with dark centres and broad buff fringes."""
    base = plumage(x, y, z, BROWN, BROWN_DK, mix(BROWN, BUFF, 0.4), 1.0)
    return feathered(base, a, b, BUFF, size * 1.3, size * 1.15, 0.6, shadow=0.8)


def vermiculated(x, y, z):
    """The drake's grey flanks: fine, wavy dark lines on pale grey."""
    c = plumage(x, y, z, VERM, VERM_DK, mix(VERM, BELLY_D, 0.5), 1.4)
    wave = frac((y + 0.18 * math.sin(x * 2.2 + z)) * 2.6)
    return mul(c, 0.86) if wave < 0.22 else c


def m_body(x, y, z, face, fu, fv):
    if not drake():
        if face == "down": return plumage(x, y, z, BUFF, mix(BUFF, BROWN, 0.35), mix(BUFF, (230, 210, 180), 0.5), 1.2)
        return lift(hen_feathers(x, y, z, x, z if face in ("up", "down") else y), y, 2.0, 6.0)
    stern = rough((x - 10.0) / 0.4, x, y, z, 0.4) if face != "up" else 0.0
    if face == "up": c = feathered(plumage(x, y, z, BACK_D, BACK_D_DK, mix(BACK_D, BELLY_D, 0.3)), x, z, mix(BACK_D, BELLY_D, 0.4), 1.0, 0.9, 0.5, shadow=0.8)
    elif face == "down": c = plumage(x, y, z, BELLY_D, VERM, (226, 226, 220), 1.4)
    elif face == "west": c = plumage(x, y, z, CHESTNUT, CHESTNUT_DK, mix(CHESTNUT, BELLY_D, 0.2), 1.0)
    elif face == "east": c = grain(STERN, x, y, z)
    else:
        back = feathered(plumage(x, y, z, BACK_D, BACK_D_DK, mix(BACK_D, BELLY_D, 0.3)), x, y, mix(BACK_D, BELLY_D, 0.4), 1.0, 0.9, 0.5, shadow=0.8)
        c = lift(mix(vermiculated(x, y, z), back, rough((y - 5.2) / 0.4, x, y, z, 0.5)), y, 2.0, 6.0)
    return mix(c, plumage(x, y, z, STERN, (16, 16, 20), (44, 44, 50), 1.0), stern)


def m_breast(x, y, z, face, fu, fv):
    if not drake(): return hen_feathers(x, y, z, -y, z + x, 0.8)
    c = feathered(plumage(x, y, z, CHESTNUT, CHESTNUT_DK, mix(CHESTNUT, (150, 90, 66), 0.6), 1.0), -y, z + x, mix(CHESTNUT, (160, 100, 76), 0.5), 0.7, 0.7, 0.4)
    return mul(c, 0.85) if face == "down" else c


def m_belly(x, y, z, face, fu, fv):
    if not drake(): return plumage(x, y, z, BUFF, mix(BUFF, BROWN, 0.35), mix(BUFF, (230, 210, 180), 0.5), 1.2)
    return plumage(x, y, z, BELLY_D, VERM, (226, 226, 220), 1.4)


def m_rump(x, y, z, face, fu, fv):
    if not drake(): return hen_feathers(x, y, z, x, z if face == "up" else y, 0.8)
    return plumage(x, y, z, STERN, (16, 16, 20), (44, 44, 50), 1.0)


def m_neck(x, y, z, face, fu, fv):
    if not drake(): return plumage(x, y, z, HEAD_H, mix(HEAD_H, BROWN, 0.4), BUFF, 0.8, (0.8, 2.0, 0.8))
    collar = clamp01(1 - abs(x - 0.65) / 0.3)                          # the white ring
    if x > 0.95: c = plumage(x, y, z, CHESTNUT, CHESTNUT_DK, mix(CHESTNUT, BELLY_D, 0.2), 0.8)
    else: c = green(x, y, z, face)
    return mix(c, grain(COLLAR, x, y, z, 0.03), collar)


def green(x, y, z, face):
    """The drake's head: bottle green with a glossy sheen that catches the light high on the head."""
    c = plumage(x, y, z, GREEN, GREEN_DK, GREEN_LT, 0.9)
    sheen = clamp01((y - 5.6) / 1.2) * (0.8 if face != "up" else 0.5)
    return mix(c, mix(GREEN_LT, (90, 170, 120), 0.4), 0.55 * sheen * cloud(x, y, z, 0.7))


EYE_C = (-2.75, 6.35)


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south"):
        e = round_eye(x - EYE_C[0], y - EYE_C[1], 0.3, (34, 26, 20))
        if e: return e
    if not drake():
        c = plumage(x, y, z, HEAD_H, mix(HEAD_H, BROWN, 0.3), mix(HEAD_H, BUFF, 0.6), 0.7)
        if face == "up": return plumage(x, y, z, BROWN, BROWN_DK, HEAD_H, 0.7)                  # dark crown
        if face in ("north", "south"):
            stripe = clamp01(1 - abs(y - EYE_C[1] - 0.02) / 0.16) * clamp01((x + 1.0) / -0.6)   # dark eye stripe
            return mix(c, STRIPE, 0.85 * stripe)
        return c
    return green(x, y, z, face)


def m_bill(x, y, z, face, fu, fv):
    if x < -6.0 or face == "west": return grain(NAIL, x, y, z, 0.04)
    if drake(): c = BILL_D
    else:
        c = BILL_H
        if face == "up": c = mix(c, SADDLE, rough(1 - abs(x + 5.05) / 0.7, x, y, z, 0.6))      # hen's dark saddle
    if face == "up": c = mul(c, 1.06)
    elif face == "down": c = mul(c, 0.84)
    elif -4.85 < x < -4.5 and 5.4 < y < 5.55: c = mul(c, 0.55)                                 # nostril
    return grain(c, x, y, z, 0.03)


def m_bill_base(x, y, z, face, fu, fv):
    """Where the bill rises into the forehead: bill colour all round (no nail)."""
    if face == "west": face = "up"
    return m_bill(-4.4, y, z, face, fu, fv) if face != "up" else m_bill(x, y, z, face, fu, fv)


def m_feet(x, y, z, face, fu, fv):
    c = mul(FEET, 0.84) if face == "down" else FEET
    return grain(mul(c, 0.9) if frac(x * 2.5) < 0.2 else c, x, y, z, 0.04)


def m_web(x, y, z, face, fu, fv):
    return grain(mul(FEET, 0.9 if face == "up" else 0.76), x, y, z, 0.05)


def m_claw(x, y, z, face, fu, fv):
    return grain((60, 50, 44), x, y, z, 0.05)


def m_edge(x, y, z, face, fu, fv):
    return plumage(x, y, z, WING_G_DK if drake() else BROWN_DK, mul(WING_G_DK, 0.85), WING_G, 1.0)


def m_tail(x, y, z, face, fu, fv):
    """Short pointed tail: whitish outer feathers on the drake, brown with buff edges on the hen."""
    end = 14.6 - x
    edge = abs(z - 8.0) / 1.6                # 0 centre .. 1 outer edge
    if end < 1.6 * edge ** 1.5 - 0.1: return None                   # pointed shape
    lz = frac((z - 6.4) / 0.8)
    if drake():
        c = mix(plumage(x, y, z, TAIL_D, VERM, (236, 236, 230), 1.0), vermiculated(x, y, z), rough(0.6 - edge, x, y, z, 0.4))
    else:
        c = plumage(x, y, z, BROWN, BROWN_DK, mix(BROWN, BUFF, 0.4), 1.0, (2.0, 1, 0.6))
        if min(lz, 1 - lz) < 0.18: c = mix(c, BUFF, 0.7)
    return mul(c, 0.86) if min(lz, 1 - lz) < 0.06 else c


def m_curl(x, y, z, face, fu, fv):
    return plumage(x, y, z, STERN, (16, 16, 20), (50, 50, 60), 0.6) if drake() else plumage(x, y, z, BROWN, BROWN_DK, BUFF, 0.6)


SCAP_X = (3.2, 8.4)


def m_scapulars(x, y, z, face, fu, fv):
    end = SCAP_X[1] - x
    lz = frac(abs(z - 8.0) / 1.0)
    if end < 0.8 * (2 * lz - 1) ** 2: return None
    if not drake(): return hen_feathers(x, y, z, x, abs(z - 8.0))
    c = feathered(plumage(x, y, z, BACK_D, BACK_D_DK, mix(BACK_D, BELLY_D, 0.3)), x, abs(z - 8.0), mix(BACK_D, BELLY_D, 0.5), 1.0, 0.9, 0.5, shadow=0.8)
    return mix(c, mix(BACK_D, BELLY_D, 0.35), 0.4 * rough((x - 6.5) / 1.0, x, y, z, 0.4))     # paler grey scapulars


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: grey-brown coverts, a white bar, the blue speculum, another white bar, then the tertials."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.2)
    if c > 0.95 and (lz < 0.15 or lz > 0.85): return None
    if s < 0.18:                                                       # tertials
        t = plumage(x, y, z, BROWN if not drake() else BACK_D, BROWN_DK if not drake() else BACK_D_DK, BUFF if not drake() else BELLY_D, 1.0, (2.6, 1, 0.5))
        return mul(t, 0.82) if min(lz, 1 - lz) < 0.12 else t
    coverts = plumage(x, y, z, WING_G, WING_G_DK, mix(WING_G, BAR, 0.25), 1.2)
    coverts = feathered(coverts, x, abs(z - 8.0), mix(WING_G, BAR, 0.35), 0.8, 0.8, 0.45 if not drake() else 0.3, shadow=0.84)
    spec = mix(SPEC, SPEC_LT, clamp01(cloud(x, y, z, 0.6) * 1.4 - 0.2))   # iridescent
    if min(lz, 1 - lz) < 0.1: spec = mul(spec, 0.8)
    bar = grain(BAR, x, y, z, 0.03)
    col = coverts
    col = mix(col, bar, rough((c - 0.49) / 0.04, x, y, z, 0.2))
    col = mix(col, spec, clamp01((c - 0.56) / 0.03))
    return mix(col, bar, clamp01((c - 0.89) / 0.03))


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: grey-brown primaries tapering to a sharper point than the goose's."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.15 * s ** 2
    trail = 1.0 - 0.75 * s ** 1.3
    if s > 0.985 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)
    coverts = feathered(plumage(x, y, z, WING_G, WING_G_DK, mix(WING_G, BAR, 0.25), 1.2), x, abs(z - 8.0), mix(WING_G, BAR, 0.3), 0.8, 0.8, 0.3, shadow=0.86)
    prim = plumage(x, y, z, PRIM, mul(PRIM, 0.8), WING_G, 1.0, (0.6, 1, 2.6))
    lf = frac((k - 0.3) / 0.7 * 5.0)
    if lf < 0.14: prim = mul(prim, 0.8)
    elif 0.46 < lf < 0.56: prim = mul(prim, 1.12)
    return mix(prim, coverts, rough((0.3 - k) / 0.08, x, y, z, 0.5))


# ---------------------------------------------------------------- geometry (same conventions as the greylag)
ARM_X = (3.0, 8.8)
ARM_ROOT, ARM_LEN = 2.5, 6.0
HAND_X = (3.4, 8.4)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 7.0
WING_Y = 5.4
mz = g.mz


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 2.5, WING_Y, z_root if L else mz(z_root)])
    under = El("Arm" + side + "u", arm.frm, arm.to, m_arm, faces=("up",), uv_from=arm,
               origin=[ARM_X[0], WING_Y, arm.origin[2]], rot=(0, 0, 5))
    edge_a = El("EdgeArm" + side, [ARM_X[0] - 0.2, WING_Y - 0.2, a0], [ARM_X[0] + 0.4, WING_Y + 0.2, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 2.0, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.55)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.1, WING_Y - 0.15, e0], [HAND_X[0] + 0.4, WING_Y + 0.15, e1], m_edge)
    return arm.add(under, edge_a, hand.add(edge_h))


def shape():
    body = El("Body", [2.0, 2.0, 5.5], [11.0, 6.0, 10.5], m_body)
    breast = El("Breast", [0.8, 2.6, 6.0], [2.0, 5.8, 10.0], m_breast)
    belly = El("Belly", [3.0, 1.55, 6.1], [10.0, 2.0, 9.9], m_belly)        # rounds the underside (0.22.0)
    rump = El("Rump", [11.0, 3.0, 6.3], [12.4, 5.6, 9.7], m_rump)
    neck = El("Neck", [-1.2, 4.6, 7.0], [1.2, 6.4, 9.0], m_neck, origin=[1.2, 5.5, 8], rot=(0, 0, -4))
    neck_base = El("NeckBase", [0.4, 4.2, 6.6], [1.6, 6.4, 9.4], m_neck)   # the neck thickens into the breast
    head = El("Head", [-4.0, 4.6, 6.8], [-1.2, 7.0, 9.2], m_head, origin=[-1.2, 5.7, 8], rot=(0, 0, 6), res=2)
    bill = El("Bill", [-6.4, 5.0, 7.15], [-4.0, 5.7, 8.85], m_bill, res=2)
    # the bill's raised base where it meets the forehead: a duck's profile slopes into its bill
    bill_base = El("BillBase", [-4.6, 5.0, 7.3], [-3.9, 6.1, 8.7], m_bill_base, res=2)
    tail = g.split_tail(El("Tail", [12.0, 5.0, 6.4], [14.6, 5.0, 9.6], m_tail, faces=("up",), origin=[12.0, 5.0, 8], rot=(0, 0, 6)), m_tail, fan=7)
    curl = El("TailCurl", [11.5, 5.6, 7.6], [12.3, 6.6, 8.4], m_curl, origin=[11.5, 5.6, 8], rot=(0, 0, -20))
    legs = []
    for side, zc in (("L", 8.7), ("R", 7.3)):
        leg = El("Leg" + side, [8.8, 2.4, zc - 0.25], [11.4, 2.8, zc + 0.25], m_feet, origin=[8.8, 2.6, zc])
        legs.append(leg.add(g.webbed_foot(side, 10.8, 2.6, zc, m_feet, m_web, m_claw, length=1.5, centre=0.8)))
    scaps = [g.scapulars(s, SCAP_X[0], SCAP_X[1], 6.12, 8 + ARM_ROOT, 1.6, 0.6, m_scapulars) for s in "LR"]
    body.add(breast, belly, rump, neck.add(neck_base, head.add(bill, bill_base)), tail, curl, *legs, *scaps, wing("L"), wing("R"))
    return body


def paint_as(sex, root, height):
    global SEX
    SEX = sex
    return g.paint(root, height)


def main():
    repaint = "--texture" in sys.argv
    shapes = os.path.join(ROOT, "shapes", "entity", "bird")
    textures = os.path.join(ROOT, "textures", "entity", "bird")
    os.makedirs(shapes, exist_ok=True); os.makedirs(textures, exist_ok=True)
    root = shape()
    height = g.pack(root)
    data = {
        "textureWidth": g.SHEET_W // g.PX, "textureHeight": height // g.PX,
        "textures": {TEX: "flocks:entity/bird/" + TEX + "-drake"},
        "elements": [root.json()],
        "animations": g.complete_axes(g.animations()),
    }
    with open(os.path.join(shapes, TEX + ".json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1)
    print("wrote", os.path.normpath(os.path.join(shapes, TEX + ".json")))
    for sex in (DRAKE, HEN):
        path = os.path.join(textures, f"{TEX}-{sex}.png")
        img = paint_as(sex, root, height)
        if repaint or not os.path.exists(path):
            img.save(path); print("painted", os.path.normpath(path), img.size)
        else:
            print("kept existing texture", os.path.normpath(path), "(pass --texture to repaint)")
        if sex == DRAKE: g.uv_guide(root, img).save(os.path.join(HERE, "mallard_uv_guide.png"))
    zs = [v for e in root.walk() for v in (e.frm[2], e.to[2])]
    print("wingspan", round((max(zs) - min(zs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
