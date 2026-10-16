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
from make_greylag import El, mul, mix, frac

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


def scallop(x, z, base, fringe, dark, size=1.2):
    """Hen feathering and the drake's back: offset rows of feathers with pale fringes and dark centres."""
    row = math.floor(x / size)
    lx, lz = frac(x / size), frac((z + row * size / 2) / size)
    if lx > 0.66: return fringe if 0.15 < lz < 0.85 else base
    if lx < 0.32 and 0.3 < lz < 0.7: return dark
    return base


# ---------------------------------------------------------------- materials (see make_greylag for the convention)
def m_body(x, y, z, face, fu, fv):
    if not drake():
        if face == "down": return BUFF if frac(x * 0.8 + z * 0.4) > 0.2 else mix(BUFF, BROWN, 0.4)
        return scallop(x, z if face in ("up", "down") else y + 3, BROWN, BUFF, BROWN_DK)
    if x > 10.0 and face != "up": return STERN                      # black stern
    if face == "up": return scallop(x, z, BACK_D, mix(BACK_D, BELLY_D, 0.4), BACK_D_DK, 1.0)
    if face == "down": return BELLY_D
    if face == "west": return CHESTNUT
    if face == "east": return STERN
    if y > 5.3: return scallop(x, y + 4, BACK_D, mix(BACK_D, BELLY_D, 0.4), BACK_D_DK, 1.0)
    return VERM_DK if frac((y + x * 0.15) * 2.6) < 0.18 else VERM  # fine wavy barring


def m_breast(x, y, z, face, fu, fv):
    if not drake(): return scallop(x + 3, y if face not in ("up", "down") else z, BROWN, BUFF, BROWN_DK, 0.9)
    return CHESTNUT_DK if face == "down" else CHESTNUT


def m_rump(x, y, z, face, fu, fv):
    if not drake(): return scallop(x, z if face == "up" else y, BROWN, BUFF, BROWN_DK, 0.9)
    return STERN


def m_neck(x, y, z, face, fu, fv):
    if not drake(): return scallop(x, y if face not in ("up", "down") else z, HEAD_H, BUFF, BROWN, 0.7)
    if 0.35 < x < 0.95: return COLLAR                             # the white ring
    if x > 0.95: return CHESTNUT
    return GREEN_LT if frac(y * 1.3) > 0.5 else GREEN


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south") and -3.05 < x < -2.45 and 6.0 < y < 6.6: return EYE
    if not drake():
        if face in ("north", "south") and 5.9 < y < 6.25 and x < -1.6: return STRIPE   # dark eye stripe
        if face == "up": return BROWN                                                     # dark crown
        return HEAD_H
    if face == "up": return GREEN_DK
    return GREEN_LT if y > 6.3 else GREEN


def m_bill(x, y, z, face, fu, fv):
    if x < -6.0 or face == "west": return NAIL
    if drake(): return mul(BILL_D, 0.88) if face == "down" else BILL_D
    if face == "up" and -5.7 < x < -4.4: return SADDLE                                  # hen's dark saddle
    return mul(BILL_H, 0.88) if face == "down" else BILL_H


def m_feet(x, y, z, face, fu, fv):
    return mul(FEET, 0.85) if face == "down" else FEET


def m_edge(x, y, z, face, fu, fv):
    return WING_G_DK if drake() else BROWN_DK


def m_tail(x, y, z, face, fu, fv):
    """Short pointed tail: whitish outer feathers on the drake, brown with buff edges on the hen."""
    end = 14.6 - x
    edge = abs(z - 8.0) / 1.6                # 0 centre .. 1 outer edge
    if end < 1.6 * edge ** 1.5 - 0.1: return None                   # pointed shape
    lz = frac((z - 6.4) / 0.8)
    if drake(): return TAIL_D if edge > 0.35 else mix(TAIL_D, VERM_DK, 0.5)
    return BUFF if lz < 0.2 or lz > 0.8 else BROWN


def m_curl(x, y, z, face, fu, fv):
    return STERN if drake() else BROWN


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: grey-brown coverts, a white bar, the blue speculum, another white bar, then the tertials."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.2)
    if c > 0.95 and (lz < 0.15 or lz > 0.85): return None
    if s < 0.18: return (BROWN if not drake() else BACK_D) if 0.15 < lz < 0.85 else (BROWN_DK if not drake() else BACK_D_DK)
    if c < 0.5: return scallop(c * 6, abs(z - 8.0), WING_G, mix(WING_G, BAR, 0.3), WING_G_DK, 0.8) if not drake() else WING_G
    if c < 0.56 or c > 0.9: return BAR
    return SPEC_LT if frac(abs(z - 8.0) * 0.8) > 0.5 else SPEC


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: grey-brown primaries tapering to a sharper point than the goose's."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.15 * s ** 2
    trail = 1.0 - 0.75 * s ** 1.3
    if s > 0.985 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)
    if k < 0.3: return WING_G
    lf = frac((k - 0.3) / 0.7 * 5.0)
    if lf < 0.14: return mul(PRIM, 0.8)
    return PRIM


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
    rump = El("Rump", [11.0, 3.0, 6.3], [12.4, 5.6, 9.7], m_rump)
    neck = El("Neck", [-1.2, 4.6, 7.0], [1.2, 6.4, 9.0], m_neck, origin=[1.2, 5.5, 8], rot=(0, 0, -4))
    head = El("Head", [-4.0, 4.6, 6.8], [-1.2, 7.0, 9.2], m_head, origin=[-1.2, 5.7, 8], rot=(0, 0, 6))
    bill = El("Bill", [-6.4, 5.0, 7.15], [-4.0, 5.7, 8.85], m_bill)
    tail = El("Tail", [12.0, 5.0, 6.4], [14.6, 5.0, 9.6], m_tail, faces=("up",), origin=[12.0, 5.0, 8], rot=(0, 0, 6))
    curl = El("TailCurl", [11.5, 5.6, 7.6], [12.3, 6.6, 8.4], m_curl, origin=[11.5, 5.6, 8], rot=(0, 0, -20))
    feet_l = El("FootL", [10.8, 2.4, 8.2], [12.4, 2.8, 9.2], m_feet)
    feet_r = El("FootR", [10.8, 2.4, 6.8], [12.4, 2.8, 7.8], m_feet)
    leg_l = El("LegL", [8.8, 2.4, 8.45], [10.8, 2.8, 8.95], m_feet, origin=[8.8, 2.6, 8.7], uv_from=feet_l)
    leg_r = El("LegR", [8.8, 2.4, 7.05], [10.8, 2.8, 7.55], m_feet, origin=[8.8, 2.6, 7.3], uv_from=feet_r)
    leg_l.add(feet_l); leg_r.add(feet_r)
    body.add(breast, rump, neck.add(head.add(bill)), tail, curl, leg_l, leg_r, wing("L"), wing("R"))
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
