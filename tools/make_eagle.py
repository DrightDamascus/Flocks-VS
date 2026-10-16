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
BROWN_DK, BROWN, BROWN_LT = (58, 41, 28), (84, 60, 40), (112, 82, 56)
TAWNY = (150, 116, 76)                      # the pale bar across the upper wing coverts
GOLD, GOLD_DK = (200, 154, 74), (164, 120, 54)
TROUSERS = (140, 106, 72)
TAIL_G, TAIL_B = (104, 92, 80), (52, 40, 30)
CERE, BILL, BILL_TIP = (228, 198, 72), (82, 80, 80), (34, 32, 34)
FEET, CLAW = (226, 194, 66), (28, 26, 26)
EYE = (128, 78, 30)
PRIM = (40, 32, 26)


def mottle(x, y, z, a, b, scale=1.3):
    """Feathering: offset rows of darker feather centres on a base colour."""
    row = math.floor(x / scale)
    lx, lz = frac(x / scale), frac((z + y + row * scale / 2) / scale)
    return b if lx < 0.35 and 0.25 < lz < 0.75 else a


# ---------------------------------------------------------------- materials
def m_body(x, y, z, face, fu, fv):
    if face == "up": return mottle(x, 0, z, BROWN, BROWN_DK)
    if face == "down": return mottle(x, 0, z, BROWN_DK, mul(BROWN_DK, 0.85))
    return mottle(x, y, z, BROWN, BROWN_DK) if y > 4.4 else BROWN_DK


def m_breast(x, y, z, face, fu, fv):
    return mottle(x, y, z, BROWN_DK, mul(BROWN_DK, 0.85), 1.0)


def m_neck(x, y, z, face, fu, fv):
    # the golden hackles cover the back and sides of the neck
    if face == "up" or (face in ("north", "south") and y > 5.2): return GOLD if frac(x * 1.5) > 0.3 else GOLD_DK
    return BROWN


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south") and -3.1 < x < -2.5 and 5.7 < y < 6.3: return EYE
    if face == "up": return GOLD if x > -3.0 else GOLD_DK                 # golden crown and nape
    if face in ("north", "south") and y > 6.2: return GOLD_DK
    if face == "east": return GOLD
    return BROWN


def m_bill(x, y, z, face, fu, fv):
    if x > -4.1: return CERE                                              # yellow cere at the base
    return BILL if x > -4.8 else BILL_TIP


def m_hook(x, y, z, face, fu, fv):
    return BILL_TIP


def m_legs(x, y, z, face, fu, fv):
    return TROUSERS if face != "down" else mul(TROUSERS, 0.85)            # feathered down to the toes


def m_feet(x, y, z, face, fu, fv):
    return CLAW if x > 12.2 else FEET


def m_edge(x, y, z, face, fu, fv):
    return BROWN_DK


def m_tail(x, y, z, face, fu, fv):
    """Long, broad, square tail: grey-brown with faint darker bands and a dark terminal band."""
    end = 16.5 - x
    if end < 0.3 * abs(z - 8.0) / 2.2 - 0.05: return None                 # very slightly rounded corners
    if end < 1.4: return TAIL_B
    return TAIL_B if frac(x / 1.6) < 0.25 else TAIL_G


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: dark brown coverts with a pale tawny bar, then dark greyish secondaries."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.6)
    if c > 0.96 and (lz < 0.14 or lz > 0.86): return None
    if c < 0.1: return BROWN_DK
    if c < 0.35: return mottle(c * 9, 0, abs(z - 8.0), TAWNY, BROWN)
    if c < 0.55: return mottle(c * 9, 0, abs(z - 8.0), BROWN, BROWN_DK)
    return mix(PRIM, TAIL_G, 0.35) if 0.12 < lz < 0.88 else PRIM


FINGERS = 7


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: coverts, then long dark primaries; past 60% of the hand the outer seven separate into fingers."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.08 * s ** 2
    trail = 1.0 - 0.3 * s ** 2
    if s > 0.995 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)
    if s < 0.6 and k < 0.3: return mottle(k * 9, 0, abs(z - 8.0), BROWN, BROWN_DK)
    if s > 0.6:
        f = frac(k * FINGERS)
        gap = 0.08 + 0.55 * (s - 0.6) / 0.4          # the gaps between fingers widen toward the tip
        if f < gap / 2 or f > 1 - gap / 2: return None
    return PRIM if frac(k * FINGERS) > 0.1 else mul(PRIM, 0.8)


# ---------------------------------------------------------------- geometry
ARM_X = (2.4, 11.0)
ARM_ROOT, ARM_LEN = 2.4, 10.0
HAND_X = (3.0, 10.6)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 10.0
WING_Y = 5.8
mz = g.mz
HIP = (8.0, 3.4)
LEG = 3.0
BODY_ORIGIN = (6.5, 4.5)


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 3, WING_Y, z_root if L else mz(z_root)])
    under = El("Arm" + side + "u", arm.frm, arm.to, m_arm, faces=("up",), uv_from=arm,
               origin=[ARM_X[0], WING_Y, arm.origin[2]], rot=(0, 0, 5))
    edge_a = El("EdgeArm" + side, [ARM_X[0] - 0.2, WING_Y - 0.25, a0], [ARM_X[0] + 0.6, WING_Y + 0.25, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 2.5, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.45)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.1, WING_Y - 0.2, e0], [HAND_X[0] + 0.5, WING_Y + 0.2, e1], m_edge)
    return arm.add(under, edge_a, hand.add(edge_h))


def shape():
    body = El("Body", [2.0, 2.6, 5.6], [11.0, 6.4, 10.4], m_body)
    breast = El("Breast", [1.0, 3.0, 6.0], [2.0, 6.0, 10.0], m_breast)
    neck = El("Neck", [-0.8, 4.4, 6.6], [1.2, 6.4, 9.4], m_neck, origin=[1.2, 5.4, 8])
    head = El("Head", [-3.6, 4.3, 6.6], [-0.8, 6.9, 9.4], m_head, origin=[-0.8, 5.6, 8])
    bill = El("Bill", [-5.2, 4.6, 7.4], [-3.6, 5.8, 8.6], m_bill)
    hook = El("BillHook", [-5.7, 4.1, 7.6], [-5.0, 5.0, 8.4], m_hook)
    tail = El("Tail", [10.5, 5.2, 5.8], [16.5, 5.2, 10.2], m_tail, faces=("up",), origin=[10.5, 5.2, 8], rot=(0, 0, 2))
    leg_l = El("LegL", [8.0, 3.0, 8.4], [8.0 + LEG, 3.8, 9.4], m_legs, origin=[HIP[0], HIP[1], 8.9])
    leg_r = El("LegR", [8.0, 3.0, 6.6], [8.0 + LEG, 3.8, 7.6], m_legs, origin=[HIP[0], HIP[1], 7.1])
    foot_l = El("FootL", [11.0, 2.8, 8.2], [12.6, 3.4, 9.6], m_feet)
    foot_r = El("FootR", [11.0, 2.8, 6.4], [12.6, 3.4, 7.8], m_feet)
    leg_l.add(foot_l); leg_r.add(foot_r)
    body.add(breast, neck.add(head.add(bill.add(hook))), tail, leg_l, leg_r, wing("L"), wing("R"))
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


def ground(frame, body, head=(0, 0, 0), neck=(0, 0, 0), wings=None, swing=(0.0, 0.0)):
    els = {k: dict(v) for k, v in g.FOLDED.items()}
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
    flap = [air(0, 0, -14, {"Body": {"rotationZ": 1}}), air(8, 34, 8, {"Body": {"rotationZ": 0}}),
            air(16, 0, 18, {"Body": {"rotationZ": -1}}), air(24, -30, -8, {"Body": {"rotationZ": 0}})]
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
    # upright, about 60 deg from horizontal; neck and head tipped back so the head stays level
    up = dict(neck=(0, 0, 25))
    perch = [ground(0, -60, head=(0, 0, 35), **up), ground(20, -60, head=(0, 30, 35), **up), ground(45, -60, head=(0, 30, 35), **up),
             ground(65, -60, head=(0, -30, 35), **up), ground(90, -60, head=(0, -30, 35), **up), ground(110, -60, head=(0, 0, 35), **up)]
    # mantling over a kill: wings half open and drooped over it, head down to feed
    mantle = {"ArmL": {"rotationX": 22, "rotationY": 40}, "ArmR": {"rotationX": -22, "rotationY": -40},
              "WingL": {"rotationX": 10, "rotationY": 70}, "WingR": {"rotationX": -10, "rotationY": -70}}
    eat = [ground(0, 8, head=(0, 0, 30), neck=(0, 0, 25), wings=mantle), ground(8, 10, head=(0, 0, 50), neck=(0, 0, 35), wings=mantle),
           ground(16, 8, head=(0, 0, 30), neck=(0, 0, 25), wings=mantle)]
    walk = [ground(0, -35, head=(0, 0, 20), neck=(0, 0, 15), swing=(18, -12)), ground(8, -35, head=(0, 0, 20), neck=(0, 0, 15)),
            ground(16, -35, head=(0, 0, 20), neck=(0, 0, 15), swing=(-12, 18)), ground(24, -35, head=(0, 0, 20), neck=(0, 0, 15))]
    death = [g.ground_key(0, Body=(0, 0, 0)), g.ground_key(6, Body=(35, 0, 0), Neck=(0, 0, 10), Head=(0, 0, 10)),
             g.ground_key(14, Body=(82, 0, 0), Neck=(0, 0, 20), Head=(0, 0, 15), LegL=(0, 0, -30), LegR=(0, 0, -15)),
             g.ground_key(23, Body=(82, 0, 0), Neck=(0, 0, 20), Head=(0, 0, 15), LegL=(0, 0, -30), LegR=(0, 0, -15))]
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
