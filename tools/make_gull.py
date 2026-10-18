"""Generates the herring gull shape, texture and animations for Flocks.

Run from the repo root:
  python tools/make_gull.py            shape + animations; texture only if it does not exist yet
  python tools/make_gull.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/gull.json, textures/entity/bird/gull.png and tools/gull_uv_guide.png.

Built from make_greylag.py's pieces (elements, UV packing, painting, animations) with gull proportions: a sleek white
body, a rounded white head, a long stout yellow bill with the red spot near its tip, a short square white tail, pink
legs, and long, narrow, pointed wings: silver-grey above with a white trailing edge, the outer primaries black with
white "mirrors" near the tip. The element names match the greylag's, so every greylag animation (flight, swim, walk,
takeoff, landing, death) works unchanged.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac, plumage, feathered, rough, lift, grain, clamp01, round_eye

HERE = g.HERE
ROOT = g.ROOT
TEX = "gull"
g.TEX = TEX                    # El.json names faces' texture "#" + make_greylag.TEX

# ---------------------------------------------------------------- palette (herring gull, adult)
WHITE, WHITE_SH = (240, 240, 236), (214, 216, 214)
MANTLE, MANTLE_DK, MANTLE_LT = (166, 176, 186), (142, 152, 162), (190, 198, 206)
BLACK, BLACK_LT = (30, 30, 34), (52, 52, 56)
BILL, BILL_DK, GONYS = (234, 198, 72), (204, 168, 56), (204, 52, 40)
LEGS = (228, 172, 160)
EYE = (34, 30, 26)


# ---------------------------------------------------------------- materials (see make_greylag for the convention)
# Painted with make_greylag's texture toolkit (plumage, feathered, rough...): soft, low-contrast feathering in vanilla's
# manner. A gull is mostly clean white and silver-grey, so its feathering is kept faint.
def white(x, y, z, s=1.4):
    return grain(plumage(x, y, z, WHITE, mix(WHITE, WHITE_SH, 0.45), (250, 250, 248), s), x, y, z, 0.02)


def mantle(x, y, z, a, b):
    return feathered(plumage(x, y, z, MANTLE, MANTLE_DK, MANTLE_LT, 1.4), a, b, MANTLE_LT, 1.4, 1.2, 0.5, shadow=0.88)


def m_body(x, y, z, face, fu, fv):
    if face == "up": return mantle(x, y, z, x, z)
    if face == "down": return mul(white(x, y, z), 0.92)
    if face in ("east", "west"): return white(x, y, z)
    c = mix(white(x, y, z), mantle(x, y, z, x, y), rough((y - 5.2) / 0.4, x, y, z, 0.5))   # grey back over white flanks
    return lift(c, y, 2.2, 6.0)


def m_breast(x, y, z, face, fu, fv):
    return mul(white(x, y, z), 0.92) if face == "down" else white(x, y, z)


def m_rump(x, y, z, face, fu, fv):
    return white(x, y, z)


def m_neck(x, y, z, face, fu, fv):
    return white(x, y, z, 1.0)


EYE_C = (-2.75, 6.25)          # on a pixel corner, so the pupil is a clean 2x2
IRIS = (232, 210, 110)         # a herring gull's pale yellow eye
EYE_RING = (214, 140, 96)      # and its thin orange-red orbital ring


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south"):
        e = round_eye(x - EYE_C[0], y - EYE_C[1], 0.45, IRIS, EYE_RING, 0.1, pupil=0.2)
        if e: return e
        if abs(x - EYE_C[0]) < 0.6 and 0.0 < y - EYE_C[1] < 0.5: return mul(white(x, y, z, 1.0), 0.9)   # a slight brow
    return white(x, y, z, 1.0)


def m_bill(x, y, z, face, fu, fv):
    # yellow, a little paler at the base; the red spot on the lower mandible near the tip
    if x < -5.4 and x > -6.4 and y < 5.25 and face in ("north", "south", "down"): return grain(GONYS, x, y, z, 0.03)
    c = mix(BILL, mix(BILL, (240, 226, 150), 0.5), clamp01((x + 4.8) / 0.8))
    if x < -6.3: c = mul(c, 0.9)                                                  # the hooked tip
    if face == "up": c = mul(c, 1.06)
    elif face == "down": c = BILL_DK
    elif -5.1 < x < -4.6 and 5.3 < y < 5.45: c = mul(c, 0.55)                     # nostril
    return grain(c, x, y, z, 0.03)


def m_feet(x, y, z, face, fu, fv):
    c = mul(LEGS, 0.85) if face == "down" else LEGS
    return grain(mul(c, 0.92) if frac(x * 2.5) < 0.2 else c, x, y, z, 0.04)


def m_edge(x, y, z, face, fu, fv):
    return plumage(x, y, z, MANTLE_DK, mul(MANTLE_DK, 0.9), MANTLE, 1.0)


def m_tail(x, y, z, face, fu, fv):
    """Short, square, all-white tail with slightly rounded outer corners."""
    end = 15.0 - x
    edge = abs(z - 8.0) / 1.7
    if end < 0.5 * edge ** 4 - 0.05: return None
    c = white(x, y, z)
    return mul(c, 0.9) if frac((z - 6.3) / 0.85) < 0.12 else c


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: silver-grey coverts and secondaries, a broad white trailing edge, white tertial crescent at the root."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.3)
    if c > 0.96 and (lz < 0.12 or lz > 0.88): return None             # notches between the secondary tips
    grey = mantle(x, y, z, x, abs(z - 8.0))
    sec = plumage(x, y, z, MANTLE, MANTLE_DK, MANTLE_LT, 1.0, (2.6, 1, 0.5))
    if min(lz, 1 - lz) < 0.1: sec = mul(sec, 0.88)
    grey = mix(grey, sec, rough((c - 0.45) / 0.1, x, y, z, 0.5))
    grey = mix(grey, plumage(x, y, z, MANTLE_DK, mul(MANTLE_DK, 0.9), MANTLE, 1.0), clamp01((0.12 - c) / 0.06))
    w = rough((c - 0.78) / 0.06, x, y, z, 0.4)                        # the white trailing edge
    if s < 0.12: w = max(w, rough((c - 0.58) / 0.06, x, y, z, 0.4))   # tertial crescent
    return mix(grey, white(x, y, z), w)


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: long, narrow and pointed. Grey at the base; the outer primaries black, with white mirrors near the
    tip and small white tips; a white trailing edge on the inner primaries."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.12 * s ** 2
    trail = 1.0 - 0.82 * s ** 1.2
    if s > 0.99 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)                                    # 0..1 across what is left of the chord
    lf = frac(k * 5)
    grey = mantle(x, y, z, x, abs(z - 8.0)) if k < 0.3 else plumage(x, y, z, MANTLE, MANTLE_DK, MANTLE_LT, 1.0, (0.6, 1, 2.6))
    if k >= 0.3 and lf < 0.12: grey = mul(grey, 0.88)
    if k > 0.82 and s < 0.5: grey = mix(grey, white(x, y, z), 0.9)     # the trailing edge carries on a little
    black = plumage(x, y, z, BLACK, mul(BLACK, 0.8), BLACK_LT, 1.0, (0.6, 1, 2.6))
    if lf < 0.14: black = mul(black, 0.75)
    elif 0.45 < lf < 0.55: black = mul(black, 1.4)                     # shafts
    if s > 0.965: black = white(x, y, z)                               # white tips
    elif 0.84 < s < 0.93 and 0.25 < k < 0.75: black = white(x, y, z)  # the mirror on the outermost primaries
    t = rough((s - (0.62 - 0.25 * (1 - k))) / 0.05, x, y, z, 0.3)      # the black wedge reaches further in at the front
    return mix(grey, black, t)


def m_web(x, y, z, face, fu, fv):
    return grain(mul(LEGS, 0.9 if face == "up" else 0.78), x, y, z, 0.05)


def m_claw(x, y, z, face, fu, fv):
    return grain((70, 62, 58), x, y, z, 0.05)


SCAP_X = (3.4, 8.6)


def m_scapulars(x, y, z, face, fu, fv):
    """Silver-grey scapulars over the wing root, with the white "scapular crescent" along their tips."""
    end = SCAP_X[1] - x
    lz = frac(abs(z - 8.0) / 1.1)
    if end < 0.9 * (2 * lz - 1) ** 2: return None
    c = mantle(x, y, z, x, abs(z - 8.0))
    return mix(c, white(x, y, z), rough((0.9 - end) / 0.4, x, y, z, 0.4))


# ---------------------------------------------------------------- geometry (same conventions as the greylag)
ARM_X = (3.2, 8.4)             # narrow chord: a gull's wing is long and slender
ARM_ROOT, ARM_LEN = 2.3, 8.5
HAND_X = (3.4, 8.0)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 10.5
WING_Y = 5.5
mz = g.mz


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 2.2, WING_Y, z_root if L else mz(z_root)])
    under = El("Arm" + side + "u", arm.frm, arm.to, m_arm, faces=("up",), uv_from=arm,
               origin=[ARM_X[0], WING_Y, arm.origin[2]], rot=(0, 0, 5))
    edge_a = El("EdgeArm" + side, [ARM_X[0] - 0.2, WING_Y - 0.2, a0], [ARM_X[0] + 0.4, WING_Y + 0.2, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 1.8, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.5)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.1, WING_Y - 0.15, e0], [HAND_X[0] + 0.4, WING_Y + 0.15, e1], m_edge)
    return arm.add(under, edge_a, hand.add(edge_h))


def shape():
    body = El("Body", [2.0, 2.2, 5.7], [11.0, 6.0, 10.3], m_body)
    breast = El("Breast", [0.8, 2.7, 6.1], [2.0, 5.8, 9.9], m_breast)
    belly = El("Belly", [3.0, 1.75, 6.3], [10.0, 2.2, 9.7], m_breast)       # rounds the underside (0.22.0)
    rump = El("Rump", [11.0, 3.2, 6.4], [12.6, 5.6, 9.6], m_rump)
    neck = El("Neck", [-1.2, 4.4, 7.0], [1.2, 6.2, 9.0], m_neck, origin=[1.2, 5.3, 8], rot=(0, 0, -4))
    neck_base = El("NeckBase", [0.2, 4.0, 6.6], [1.6, 6.3, 9.4], m_neck)   # the neck thickens into the breast
    head = El("Head", [-4.0, 4.6, 6.8], [-1.2, 7.0, 9.2], m_head, origin=[-1.2, 5.7, 8], rot=(0, 0, 6), res=2)
    bill = El("Bill", [-6.8, 4.9, 7.45], [-4.0, 5.65, 8.55], m_bill, res=2)
    # the gull's bill deepens toward the tip: a gonys angle on the lower mandible
    gonys = El("BillGonys", [-6.5, 4.6, 7.55], [-5.4, 4.9, 8.45], m_bill, res=2)
    tail = g.split_tail(El("Tail", [11.6, 5.0, 6.3], [15.0, 5.0, 9.7], m_tail, faces=("up",), origin=[11.6, 5.0, 8], rot=(0, 0, 4)), m_tail, fan=5)
    legs = []
    for side, zc in (("L", 8.7), ("R", 7.3)):
        leg = El("Leg" + side, [8.6, 2.4, zc - 0.25], [11.2, 2.8, zc + 0.25], m_feet, origin=[8.6, 2.6, zc])
        legs.append(leg.add(g.webbed_foot(side, 10.6, 2.6, zc, m_feet, m_web, m_claw, length=1.6, centre=0.8)))
    scaps = [g.scapulars(s, SCAP_X[0], SCAP_X[1], 6.12, 8 + ARM_ROOT, 1.6, 0.6, m_scapulars) for s in "LR"]
    body.add(breast, belly, rump, neck.add(neck_base, head.add(bill.add(gonys))), tail, *legs, *scaps, wing("L"), wing("R"))
    return body


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
        "animations": g.complete_axes(g.animations()),
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
    g.uv_guide(root, img).save(os.path.join(HERE, "gull_uv_guide.png"))
    zs = [v for e in root.walk() for v in (e.frm[2], e.to[2])]
    print("wingspan", round((max(zs) - min(zs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
