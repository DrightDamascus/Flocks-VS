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
from make_greylag import El, mul, mix, frac

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
def m_body(x, y, z, face, fu, fv):
    if face == "up": return MANTLE if 0.15 < frac(x / 1.5) else MANTLE_DK          # faint feather rows on the back
    if face == "down": return WHITE_SH
    if face in ("east", "west"): return WHITE
    return MANTLE if y > 5.3 else WHITE                                             # grey back over white flanks


def m_breast(x, y, z, face, fu, fv):
    return WHITE if face != "down" else WHITE_SH


def m_rump(x, y, z, face, fu, fv):
    return WHITE


def m_neck(x, y, z, face, fu, fv):
    return WHITE


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south") and -3.15 < x < -2.55 and 6.05 < y < 6.65: return EYE
    return WHITE


def m_bill(x, y, z, face, fu, fv):
    # the red spot on the lower mandible, near the tip
    if x < -5.4 and x > -6.4 and y < 5.25 and face in ("north", "south", "down"): return GONYS
    return BILL_DK if face == "down" else BILL


def m_feet(x, y, z, face, fu, fv):
    return mul(LEGS, 0.85) if face == "down" else LEGS


def m_edge(x, y, z, face, fu, fv):
    return MANTLE_DK


def m_tail(x, y, z, face, fu, fv):
    """Short, square, all-white tail with slightly rounded outer corners."""
    end = 15.0 - x
    edge = abs(z - 8.0) / 1.7
    if end < 0.5 * edge ** 4 - 0.05: return None
    return WHITE if frac((z - 6.3) / 0.85) > 0.12 else WHITE_SH


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: silver-grey coverts and secondaries, a broad white trailing edge, white tertial crescent at the root."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.3)
    if c > 0.96 and (lz < 0.12 or lz > 0.88): return None             # notches between the secondary tips
    if c > 0.8: return WHITE                                          # the white trailing edge
    if s < 0.12 and c > 0.6: return WHITE                             # tertial crescent
    if c < 0.08: return MANTLE_DK
    if c > 0.45: return MANTLE if 0.1 < lz < 0.9 else MANTLE_DK       # secondaries
    return MANTLE_LT if frac(c * 6) > 0.8 else MANTLE                 # rows of coverts


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: long, narrow and pointed. Grey at the base; the outer primaries black, with white mirrors near the
    tip and small white tips; a white trailing edge on the inner primaries."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.12 * s ** 2
    trail = 1.0 - 0.82 * s ** 1.2
    if s > 0.99 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)                                    # 0..1 across what is left of the chord
    black = s > 0.62 - 0.25 * (1 - k)                                  # the black wedge reaches further in at the front
    if not black:
        if k > 0.82 and s < 0.5: return WHITE                          # the trailing edge carries on a little
        return MANTLE if frac(k * 5) > 0.12 else MANTLE_DK
    if s > 0.965: return WHITE                                         # white tips
    if 0.84 < s < 0.93 and 0.25 < k < 0.75: return WHITE               # the mirror on the outermost primaries
    return BLACK if frac(k * 5) > 0.14 else BLACK_LT


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
    rump = El("Rump", [11.0, 3.2, 6.4], [12.6, 5.6, 9.6], m_rump)
    neck = El("Neck", [-1.2, 4.4, 7.0], [1.2, 6.2, 9.0], m_neck, origin=[1.2, 5.3, 8], rot=(0, 0, -4))
    head = El("Head", [-4.0, 4.6, 6.8], [-1.2, 7.0, 9.2], m_head, origin=[-1.2, 5.7, 8], rot=(0, 0, 6))
    bill = El("Bill", [-6.8, 4.9, 7.45], [-4.0, 5.65, 8.55], m_bill)
    tail = El("Tail", [11.6, 5.0, 6.3], [15.0, 5.0, 9.7], m_tail, faces=("up",), origin=[11.6, 5.0, 8], rot=(0, 0, 4))
    feet_l = El("FootL", [10.6, 2.4, 8.2], [12.2, 2.8, 9.2], m_feet)
    feet_r = El("FootR", [10.6, 2.4, 6.8], [12.2, 2.8, 7.8], m_feet)
    leg_l = El("LegL", [8.6, 2.4, 8.45], [10.6, 2.8, 8.95], m_feet, origin=[8.6, 2.6, 8.7], uv_from=feet_l)
    leg_r = El("LegR", [8.6, 2.4, 7.05], [10.6, 2.8, 7.55], m_feet, origin=[8.6, 2.6, 7.3], uv_from=feet_r)
    leg_l.add(feet_l); leg_r.add(feet_r)
    body.add(breast, rump, neck.add(head.add(bill)), tail, leg_l, leg_r, wing("L"), wing("R"))
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
