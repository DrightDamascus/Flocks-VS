"""Generates the carrion crow shape, texture and animations for Flocks.

Run from the repo root:
  python tools/make_crow.py            shape + animations; texture only if it does not exist yet
  python tools/make_crow.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/crow.json, textures/entity/bird/crow.png and tools/crow_uv_guide.png.

Built from make_greylag.py's pieces (elements, UV packing, painting, keyframe helpers), with ground poses done the
eagle's way (make_eagle.py): legs that stand the body up at the entity's feet. Its own geometry: a compact black body
with a blue-purple sheen, a big head, a stout bill, a rounded tail, legs and feet for walking, and broad wings with five
separated primaries at each tip. Its own animations, for flocks.corvid: flapping and gliding flight, takeoff, landing,
standing and looking round (also on a perch), walking, pecking at the ground, and death.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac, plumage, feathered, rough, lift, grain, clamp01, round_eye, cloud

HERE = g.HERE
ROOT = g.ROOT
TEX = "crow"
g.TEX = TEX

# ---------------------------------------------------------------- palette (carrion crow)
BLACK, BLACK_LT, BLACK_DK = (26, 26, 32), (40, 40, 50), (16, 16, 20)
SHEEN, SHEEN_LT = (44, 50, 78), (62, 70, 106)            # the blue-purple gloss on the back and wing coverts
UNDER = (36, 36, 42)
BILL, BILL_LT = (22, 22, 24), (48, 46, 46)
EYE = (78, 60, 50)
LEGS = (30, 30, 32)


# ---------------------------------------------------------------- materials
# Painted with make_greylag's texture toolkit (plumage, feathered, rough...): black feathering in vanilla's soft manner,
# each feather's edge catching a little of the blue-purple gloss, more of it on the upper surfaces.
def gloss(x, y, z, a, b, amount=1.0, size=1.0):
    c = plumage(x, y, z, BLACK, BLACK_DK, BLACK_LT, 1.2)
    c = mix(c, SHEEN, 0.5 * amount * cloud(x, y, z, 1.0))                       # the sheen comes and goes
    return feathered(c, a, b, mix(SHEEN_LT, BLACK_LT, 0.3), size, size * 0.9, 0.55 * amount + 0.15, shadow=0.7)


def m_body(x, y, z, face, fu, fv):
    if face == "up": return gloss(x, y, z, x, z)
    if face == "down": return plumage(x, y, z, UNDER, BLACK_DK, BLACK_LT, 1.2)
    if face in ("east", "west"): return plumage(x, y, z, BLACK, BLACK_DK, BLACK_LT, 1.2)
    return lift(gloss(x, y, z, x, y, clamp01((y - 3.6) / 2.0)), y, 2.8, 6.2)


def m_breast(x, y, z, face, fu, fv):
    if face == "down": return plumage(x, y, z, UNDER, BLACK_DK, BLACK_LT, 1.2)
    return gloss(x, y, z, -y, z + x, 0.3, 0.8)


def m_belly(x, y, z, face, fu, fv):
    return plumage(x, y, z, UNDER, BLACK_DK, BLACK_LT, 1.2)


def m_neck(x, y, z, face, fu, fv):
    if face == "up": return gloss(x, y, z, x, z, 0.8, 0.7)
    return gloss(x, y, z, x, y, 0.35, 0.6)


EYE_C = (-2.2, 6.25)


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south"):
        e = round_eye(x - EYE_C[0], y - EYE_C[1], 0.3, EYE)
        if e: return e
    if face == "up": return gloss(x, y, z, x, z, 0.9, 0.6)
    return lift(gloss(x, y, z, x, y, 0.4, 0.5), y, 4.5, 7.0)


def m_bill(x, y, z, face, fu, fv):
    c = BILL
    if face == "up": c = mix(BILL_LT, BILL, clamp01((-4.2 - x) / 1.5))           # the culmen catches the light
    elif face in ("north", "south"): c = mix(BILL, BILL_LT, 0.35 * clamp01((y - 5.4) / 0.6))
    return grain(c, x, y, z, 0.04)


def m_bristles(x, y, z, face, fu, fv):
    """The stiff feathers lying forward over the nostrils at the bill's base."""
    return feathered(plumage(x, y, z, BLACK, BLACK_DK, BLACK_LT, 0.6), -x, z, BLACK_LT, 0.35, 0.3, 0.4, shadow=0.75)


def m_legs(x, y, z, face, fu, fv):
    return grain(mul(LEGS, 0.85) if frac(x * 2.5) < 0.2 else LEGS, x, y, z, 0.06)


def m_toe(x, y, z, face, fu, fv):
    c = mul(LEGS, 0.8) if face == "down" else LEGS
    return grain(mul(c, 0.82) if frac(x * 3) < 0.2 else c, x, y, z, 0.06)


def m_claw(x, y, z, face, fu, fv):
    return grain(BLACK_DK, x, y, z, 0.05)


def m_edge(x, y, z, face, fu, fv):
    return plumage(x, y, z, BLACK_DK, mul(BLACK_DK, 0.8), BLACK, 1.0)


def m_tail(x, y, z, face, fu, fv):
    """Rounded, slightly fanned tail: the middle feathers longest."""
    end = 14.2 - x
    edge = abs(z - 8.0) / 1.6
    if end < 1.2 * edge ** 2 - 0.05: return None
    lz = frac((z - 6.4) / 0.8)
    c = mix(plumage(x, y, z, BLACK, BLACK_DK, BLACK_LT, 1.0, (2.0, 1, 0.6)), SHEEN, 0.3 * cloud(x, y, z, 1.0))
    return mul(c, 0.7) if min(lz, 1 - lz) < 0.1 else c


SCAP_X = (2.8, 7.4)


def m_scapulars(x, y, z, face, fu, fv):
    end = SCAP_X[1] - x
    lz = frac(abs(z - 8.0) / 0.9)
    if end < 0.8 * (2 * lz - 1) ** 2: return None
    return gloss(x, y, z, x, abs(z - 8.0))


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: glossy coverts, then black secondaries with notched tips."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.3)
    if c > 0.95 and (lz < 0.14 or lz > 0.86): return None
    coverts = gloss(x, y, z, x, abs(z - 8.0), 1.0, 0.9)
    coverts = mix(coverts, plumage(x, y, z, BLACK_DK, mul(BLACK_DK, 0.8), BLACK, 1.0), clamp01((0.12 - c) / 0.05))
    sec = mix(plumage(x, y, z, BLACK, BLACK_DK, BLACK_LT, 1.0, (2.6, 1, 0.5)), SHEEN, 0.25 * cloud(x, y, z, 1.0))
    if min(lz, 1 - lz) < 0.12: sec = mul(sec, 0.7)
    return mix(coverts, sec, rough((c - 0.5) / 0.08, x, y, z, 0.5))


FINGERS = 5


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: coverts, then broad black primaries; past 65% of the hand the outer five separate into fingers."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.1 * s ** 2
    trail = 1.0 - 0.35 * s ** 2
    if s > 0.99 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)
    f = frac(k * FINGERS)
    if s > 0.65:
        gap = 0.1 + 0.5 * (s - 0.65) / 0.35
        if f < gap / 2 or f > 1 - gap / 2: return None
    prim = mix(plumage(x, y, z, BLACK, BLACK_DK, BLACK_LT, 1.0, (0.6, 1, 2.6)), SHEEN, 0.2 * cloud(x, y, z, 1.0))
    if f < 0.1: prim = mul(prim, 0.7)
    elif abs(f - 0.5) < 0.06: prim = mul(prim, 1.25)                   # shafts
    coverts = gloss(x, y, z, x, abs(z - 8.0), 1.0, 0.9)
    return mix(prim, coverts, rough((0.3 - k) / 0.08, x, y, z, 0.5) * clamp01((0.6 - s) / 0.1))


# ---------------------------------------------------------------- geometry
ARM_X = (2.4, 8.6)
ARM_ROOT, ARM_LEN = 2.2, 6.2
HAND_X = (2.8, 8.4)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 7.6
WING_Y = 5.8
mz = g.mz
HIP = (7.4, 3.4)
LEG = 2.6
BODY_ORIGIN = (5.8, 4.5)


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 2.5, WING_Y, z_root if L else mz(z_root)])
    under = El("Arm" + side + "u", arm.frm, arm.to, m_arm, faces=("up",), uv_from=arm,
               origin=[ARM_X[0], WING_Y, arm.origin[2]], rot=(0, 0, 5))
    edge_a = El("EdgeArm" + side, [ARM_X[0] - 0.2, WING_Y - 0.2, a0], [ARM_X[0] + 0.5, WING_Y + 0.2, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 2.2, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.45)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.1, WING_Y - 0.15, e0], [HAND_X[0] + 0.4, WING_Y + 0.15, e1], m_edge)
    return arm.add(under, edge_a, hand.add(edge_h))


def shape():
    body = El("Body", [2.0, 2.8, 5.9], [9.6, 6.2, 10.1], m_body)
    breast = El("Breast", [1.0, 3.2, 6.2], [2.0, 6.0, 9.8], m_breast)
    belly = El("Belly", [2.8, 2.35, 6.4], [8.4, 2.8, 9.6], m_belly)        # rounds the underside (0.22.0)
    neck = El("Neck", [-0.6, 4.6, 6.8], [1.2, 6.4, 9.2], m_neck, origin=[1.2, 5.5, 8])
    neck_base = El("NeckBase", [0.4, 4.2, 6.5], [1.6, 6.5, 9.5], m_neck)   # the shaggy throat and neck into the breast
    head = El("Head", [-3.2, 4.5, 6.75], [-0.6, 7.0, 9.25], m_head, origin=[-0.6, 5.7, 8], res=2)
    bill = El("Bill", [-5.4, 4.95, 7.35], [-3.2, 6.05, 8.65], m_bill, res=2)
    tip = El("BillTip", [-5.9, 4.75, 7.6], [-5.2, 5.6, 8.4], m_bill, res=2)
    # the nasal bristles: stiff feathers lying forward over the bill's base, a crow's mark
    bristles = El("Bristles", [-4.1, 5.55, 7.25], [-3.1, 6.25, 8.75], m_bristles, res=2)
    tail = g.split_tail(El("Tail", [9.2, 5.3, 6.4], [14.2, 5.3, 9.6], m_tail, faces=("up",), origin=[9.2, 5.3, 8], rot=(0, 0, 3)), m_tail, fan=7)
    legs = []
    for side, zc in (("L", 8.75), ("R", 7.25)):
        leg = El("Leg" + side, [HIP[0], 3.1, zc - 0.25], [HIP[0] + LEG, 3.6, zc + 0.25], m_legs, origin=[HIP[0], HIP[1], zc])
        legs.append(leg.add(g.perching_foot(side, HIP[0] + LEG, 3.35, zc, m_toe, m_claw, toes=(1.0, 1.2, 1.0), hind=0.8,
                                            t=0.32, w=0.28, claw=0.4)))
    scaps = [g.scapulars(s, SCAP_X[0], SCAP_X[1], 6.32, 8 + ARM_ROOT, 1.4, 0.5, m_scapulars) for s in "LR"]
    body.add(breast, belly, neck.add(neck_base, head.add(bill.add(tip), bristles)), tail, *legs, *scaps, wing("L"), wing("R"))
    return body


# ---------------------------------------------------------------- animation
def stance(body, swing_l=0.0, swing_r=0.0, hop=0.0):
    """Ground legs for a body tilt (rotationZ, negative = nose up): legs straight down plus swing, feet flat and
    forward, body lifted so the feet rest at y = 0 (as for the eagle); hop lifts it a little more."""
    t = math.radians(body)
    hip_y = BODY_ORIGIN[1] + (HIP[0] - BODY_ORIGIN[0]) * math.sin(t) + (HIP[1] - BODY_ORIGIN[1]) * math.cos(t)
    lowest = min(math.sin(math.radians(-90 + sw)) for sw in (swing_l, swing_r))
    lift = round(-(hip_y + LEG * lowest - 0.45) + hop, 2)
    return {"Body": {"rotationZ": body, "offsetY": lift},
            "LegL": {"rotationZ": -90 + swing_l - body}, "LegR": {"rotationZ": -90 + swing_r - body},
            "FootL": {"rotationZ": -90 - swing_l}, "FootR": {"rotationZ": -90 - swing_r}}


def ground(frame, body, head=(0, 0, 0), neck=(0, 0, 0), swing=(0.0, 0.0), hop=0.0, tail=0.0):
    els = {k: dict(v) for k, v in g.FOLDED.items()}
    els.update(stance(body, *swing, hop=hop))
    els["Neck"] = {"rotationX": neck[0], "rotationY": neck[1], "rotationZ": neck[2]}
    els["Head"] = {"rotationX": head[0], "rotationY": head[1], "rotationZ": head[2]}
    els["Tail"] = {"rotationZ": 3 + tail}
    return {"frame": frame, "elements": els}


TUCKED = {"LegL": {"rotationZ": 8}, "LegR": {"rotationZ": 8}, "FootL": {"rotationZ": 25}, "FootR": {"rotationZ": 25}}


def air(frame, arm, hand, extra=None):
    k = g.key(frame, arm, hand)
    k["elements"].update(TUCKED)
    if extra: k["elements"].update(extra)
    return k


def animations():
    # rowing flight: steady, fairly deep beats
    flap = [air(0, 0, -20, {"Body": {"rotationZ": 1}}), air(7, 50, 10, {"Body": {"rotationZ": 0}}),
            air(15, 0, 24, {"Body": {"rotationZ": -1}}), air(22, -42, -10, {"Body": {"rotationZ": 0}})]   # deeper in 0.21.2
    glide = [air(0, 4, 2), air(20, 2, 4), air(40, 4, 2)]
    dangle = {"LegL": {"rotationZ": -55}, "LegR": {"rotationZ": -65}, "FootL": {"rotationZ": -30}, "FootR": {"rotationZ": -30}}

    def takeoff(frame, arm, hand):
        k = g.key(frame, arm, hand); k["elements"].update(dangle); k["elements"]["Body"] = {"rotationZ": -20}
        return k

    def flare(frame, arm, hand):
        k = g.key(frame, arm, hand)
        k["elements"].update({"Body": {"rotationZ": -26}, "LegL": {"rotationZ": -120}, "LegR": {"rotationZ": -120},
                              "FootL": {"rotationZ": -50}, "FootR": {"rotationZ": -50}})
        k["elements"]["ArmL"]["rotationY"] = -14; k["elements"]["ArmR"]["rotationY"] = 14
        return k
    # standing (on the ground or a perch): body fairly level, head up, looking round now and then
    up = dict(neck=(0, 0, 10))
    stand = [ground(0, -22, head=(0, 0, 18), **up), ground(25, -22, head=(0, 35, 18), **up), ground(45, -22, head=(0, 35, 18), **up),
             ground(60, -22, head=(0, -30, 18), **up), ground(85, -22, head=(0, -30, 18), **up), ground(100, -22, head=(0, 0, 18), **up)]
    # walking: a purposeful stride, head bobbing in step
    walk = [ground(0, -18, head=(0, 0, 14), neck=(0, 0, 4), swing=(20, -14)), ground(6, -18, head=(0, 0, 10), neck=(0, 0, 12)),
            ground(12, -18, head=(0, 0, 14), neck=(0, 0, 4), swing=(-14, 20)), ground(18, -18, head=(0, 0, 10), neck=(0, 0, 12))]
    # pecking at the ground: tipped forward, two quick jabs, a look up
    def peck(frame, down):
        return ground(frame, 18 + down * 0.3, head=(0, 0, 20 + down * 0.5), neck=(0, 0, 30 + down), tail=-8)
    eat = [peck(0, 0), peck(4, 28), peck(7, 4), peck(10, 30), peck(14, 0), ground(20, -10, head=(0, 0, 12), neck=(0, 0, 8)),
           ground(30, -10, head=(0, 0, 12), neck=(0, 0, 8))]
    death = [g.ground_key(0, Body=(0, 0, 0)), g.ground_key(6, Body=(35, 0, 0), Neck=(0, 0, 10), Head=(0, 0, 10)),
             g.ground_key(14, Body=(82, 0, 0), Neck=(0, 0, 25), Head=(0, 0, 15), LegL=(0, 0, -30), LegR=(0, 0, -15)),
             g.ground_key(23, Body=(82, 0, 0), Neck=(0, 0, 25), Head=(0, 0, 15), LegL=(0, 0, -30), LegR=(0, 0, -15))]
    return [
        {"name": "fly-flap", "code": "fly-flap", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": flap},
        {"name": "fly-soar", "code": "fly-soar", "quantityframes": 60, "onAnimationEnd": "Repeat", "keyframes": glide},
        {"name": "takeoff", "code": "takeoff", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [
            takeoff(0, 0, -22), takeoff(5, 55, 10), takeoff(10, 0, 26), takeoff(15, -45, -10)]},
        {"name": "land", "code": "land", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [flare(0, 38, -16), flare(10, 30, -10)]},
        {"name": "idle-stand", "code": "idle-stand", "quantityframes": 110, "onAnimationEnd": "Repeat", "keyframes": stand},
        {"name": "walk", "code": "walk", "quantityframes": 24, "onAnimationEnd": "Repeat", "keyframes": walk},
        {"name": "eat", "code": "eat", "quantityframes": 40, "onAnimationEnd": "Repeat", "keyframes": eat},
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
    g.uv_guide(root, img).save(os.path.join(HERE, "crow_uv_guide.png"))
    zs = [v for e in root.walk() for v in (e.frm[2], e.to[2])]
    print("wingspan", round((max(zs) - min(zs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
