"""Generates the tawny owl shape, texture and animations for Flocks.

Run from the repo root:
  python tools/make_owl.py            shape + animations; texture only if it does not exist yet
  python tools/make_owl.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/owl.json, textures/entity/bird/owl.png and tools/owl_uv_guide.png.

Built from make_greylag.py's pieces (elements, UV packing, painting, keyframe helpers) and make_crow.py's ground stance
(legs that stand the body up at the entity's feet). Its own geometry: a plump body, a big round head straight on the
shoulders with a facial disc and dark eyes, a small hooked bill, a short barred tail, broad rounded wings with only a
hint of fingers, and short feathered legs. Its own animations, for flocks.owl: soft flapping and gliding flight,
takeoff, the landing flare (also the pounce: talons forward), sitting upright on a branch and turning its head far
round, hooting, eating (on the ground, over its catch), walking, and death.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac

HERE = g.HERE
ROOT = g.ROOT
TEX = "owl"
g.TEX = TEX

# ---------------------------------------------------------------- palette (tawny owl, the common rufous-brown morph)
RUF, RUF_DK, RUF_LT = (146, 94, 54), (96, 60, 34), (180, 128, 80)
STREAK = (58, 38, 24)
BUFF, BUFF_LT = (206, 170, 122), (230, 208, 170)
SPOT = (236, 226, 204)                         # the white spots along the scapulars and coverts
DISC, DISC_LT, RIM = (176, 128, 84), (196, 152, 106), (80, 52, 32)
EYE = (16, 12, 10)
BILL, BILL_DK = (214, 198, 156), (170, 152, 112)
CLAW = (44, 38, 34)


def mottle(x, y, z, scale=1.0):
    """Upperparts: rufous with dark shaft streaks and fine barring, so it reads as bark at a distance."""
    lx, lz = frac(x / (1.1 * scale)), frac((z + math.floor(x / (1.1 * scale)) * 0.5) / (1.0 * scale))
    if 0.42 < lz < 0.58 and lx < 0.8: return STREAK
    if lx > 0.78: return RUF_DK if 0.2 < lz < 0.8 else RUF
    if frac(y * 1.7 + x * 0.3) < 0.18: return RUF_DK
    return RUF if frac(z * 0.7 + y * 0.2) > 0.3 else RUF_LT


def underparts(u, v):
    """Buff breast and belly with bold dark streaks crossed by fine bars (u across, v along the body)."""
    lu = frac(u / 1.2)
    if lu < 0.16: return STREAK
    if lu < 0.3 and frac(v / 0.8) < 0.3: return RUF_DK
    return BUFF if frac(v * 0.9 + u * 0.2) > 0.2 else BUFF_LT


# ---------------------------------------------------------------- materials
def m_body(x, y, z, face, fu, fv):
    if face == "up":
        # a row of white spots along each side of the back (the scapulars)
        if abs(abs(z - 8.0) - 2.0) < 0.35 and 0.3 < frac(x / 1.3) < 0.6: return SPOT
        return mottle(x, y, z)
    if face == "down": return underparts(z, x)
    if face == "west": return underparts(z, y)
    if face == "east": return mottle(x, y, z)
    return mottle(x, y, z) if y > 4.8 else underparts(x, y)


def m_breast(x, y, z, face, fu, fv):
    return underparts(z if face in ("west", "up", "down") else x, y)


def m_neck(x, y, z, face, fu, fv):
    return mottle(x, y, z, 0.8) if face in ("up", "east") else underparts(z, y)


EYES = ((6.85, 6.9), (9.15, 6.9))          # (z, y) of each eye on the face
EYE_R, DISC_R = 0.6, 1.75


def facial_disc(z, y):
    """The face: two round discs about the eyes with fine concentric rings, a dark rim round the outside, pale
    'eyebrows' forming a V between the eyes down to the bill, big black eyes."""
    d = min(math.hypot(z - ez, y - ey) for ez, ey in EYES)
    if d < EYE_R: return EYE
    if d < EYE_R + 0.25: return RUF_DK
    if abs(z - 8.0) < 0.55 and 5.6 < y < 8.2 and abs(z - 8.0) < (y - 5.4) * 0.35 + 0.15: return BUFF_LT
    if d < DISC_R: return DISC_LT if frac(d * 2.2) > 0.55 else DISC
    if d < DISC_R + 0.45 and y < 8.0: return RIM
    return mottle(0, y, z, 0.8)


def m_head(x, y, z, face, fu, fv):
    if face == "west": return facial_disc(z, y)
    if face in ("north", "south") and x < -1.6 and y < 7.6:
        return RIM if x > -2.0 else DISC              # the disc wraps a little round the sides of the face
    if face == "down": return underparts(z, x)
    return mottle(x, y, z, 0.8)


def m_bill(x, y, z, face, fu, fv):
    return BILL_DK if face in ("down", "north", "south") and y < 5.3 else BILL


def m_legs(x, y, z, face, fu, fv):
    return BUFF if frac(x * 1.5 + y) > 0.25 else BUFF_LT        # feathered to the toes


def m_feet(x, y, z, face, fu, fv):
    return CLAW if x > HIP[0] + LEG + 0.9 else BUFF_LT


def m_edge(x, y, z, face, fu, fv):
    return RUF_DK


def m_tail(x, y, z, face, fu, fv):
    """Short, rounded, rufous with dark bars."""
    end = 11.0 - x
    edge = abs(z - 8.0) / 1.7
    if end < 1.1 * edge ** 2 - 0.05: return None
    if frac((x - 7.4) / 0.9) < 0.28: return STREAK
    return RUF if frac((z - 6.3) / 0.85) > 0.12 else RUF_DK


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: mottled coverts with a row of white spots at their tips, then barred secondaries."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.2)
    if c > 0.96 and (lz < 0.1 or lz > 0.9): return None
    if c < 0.08: return RUF_DK
    if c < 0.45:
        if abs(c - 0.4) < 0.04 and 0.3 < lz < 0.7: return SPOT
        return mottle(c * 8, 0, abs(z - 8.0))
    if frac((c - 0.45) / 0.12) < 0.35: return RUF_DK
    return RUF_LT if 0.1 < lz < 0.9 else RUF


FINGERS = 4


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: dark primary coverts, then broad buff-rufous primaries barred dark, rounded at the tip; only the
    very ends of the outer feathers separate."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.12 * s ** 2
    trail = 1.0 - 0.5 * s ** 2.2
    if s > 0.99 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)
    if k < 0.25: return RUF_DK if frac(s * 6) > 0.3 else RUF
    if s > 0.82:
        f = frac(k * FINGERS)
        gap = 0.08 + 0.3 * (s - 0.82) / 0.18
        if f < gap / 2 or f > 1 - gap / 2: return None
    if frac(s * 7.5) < 0.3: return STREAK                       # bars across the primaries
    return RUF_LT if frac(k * FINGERS) > 0.12 else RUF


# ---------------------------------------------------------------- geometry
ARM_X = (1.6, 7.6)
ARM_ROOT, ARM_LEN = 2.2, 5.8
HAND_X = (1.9, 7.3)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 7.2
WING_Y = 5.8
mz = g.mz
HIP = (6.2, 2.8)
LEG = 2.2
BODY_ORIGIN = (4.4, 4.5)
NECK_ORIGIN = (0.6, 5.6)


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 2.4, WING_Y, z_root if L else mz(z_root)])
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
    body = El("Body", [1.0, 2.4, 5.6], [7.8, 6.6, 10.4], m_body)
    breast = El("Breast", [0.2, 2.8, 6.0], [1.0, 6.2, 10.0], m_breast)
    neck = El("Neck", [-0.6, 4.6, 6.4], [1.0, 6.6, 9.6], m_neck, origin=[NECK_ORIGIN[0], NECK_ORIGIN[1], 8])
    head = El("Head", [-2.6, 4.2, 5.7], [1.4, 8.4, 10.3], m_head, origin=[0.2, 5.4, 8])
    bill = El("Bill", [-3.0, 5.2, 7.6], [-2.6, 6.2, 8.4], m_bill)
    tip = El("BillTip", [-3.05, 4.75, 7.75], [-2.65, 5.25, 8.25], m_bill)
    tail = El("Tail", [7.4, 4.0, 6.3], [11.0, 4.0, 9.7], m_tail, faces=("up",), origin=[7.4, 4.0, 8], rot=(0, 0, 3))
    leg_l = El("LegL", [HIP[0], 2.5, 8.5], [HIP[0] + LEG, 3.2, 9.3], m_legs, origin=[HIP[0], HIP[1], 8.9])
    leg_r = El("LegR", [HIP[0], 2.5, 6.7], [HIP[0] + LEG, 3.2, 7.5], m_legs, origin=[HIP[0], HIP[1], 7.1])
    foot_l = El("FootL", [HIP[0] + LEG, 2.3, 8.2], [HIP[0] + LEG + 1.3, 2.75, 9.6], m_feet)
    foot_r = El("FootR", [HIP[0] + LEG, 2.3, 6.4], [HIP[0] + LEG + 1.3, 2.75, 7.8], m_feet)
    leg_l.add(foot_l); leg_r.add(foot_r)
    body.add(breast, neck.add(head.add(bill.add(tip))), tail, leg_l, leg_r, wing("L"), wing("R"))
    return body


# ---------------------------------------------------------------- animation
def stance(body, swing_l=0.0, swing_r=0.0, hop=0.0):
    """Ground legs for a body tilt (rotationZ, negative = nose up), as for the crow: legs straight down plus swing,
    feet flat and forward, body lifted so the feet rest at y = 0."""
    t = math.radians(body)
    hip_y = BODY_ORIGIN[1] + (HIP[0] - BODY_ORIGIN[0]) * math.sin(t) + (HIP[1] - BODY_ORIGIN[1]) * math.cos(t)
    lowest = min(math.sin(math.radians(-90 + sw)) for sw in (swing_l, swing_r))
    lift = round(-(hip_y + LEG * lowest - 0.4) + hop, 2)
    return {"Body": {"rotationZ": body, "offsetY": lift},
            "LegL": {"rotationZ": -90 + swing_l - body}, "LegR": {"rotationZ": -90 + swing_r - body},
            "FootL": {"rotationZ": -90 - swing_l}, "FootR": {"rotationZ": -90 - swing_r}}


def sit(frame, body=-62, look=0.0, nod=0.0, tail=24, swing=(0.0, 0.0), hop=0.0, neck_extra=0.0):
    """Upright on a branch or the ground. The neck takes back most of the body's tilt so the head sits level on top;
    look turns the head (rotationY, about the near-vertical neck: owls look far round), nod tips it down."""
    els = {k: dict(v) for k, v in g.FOLDED.items()}
    els.update(stance(body, *swing, hop=hop))
    els["Neck"] = {"rotationZ": -body + neck_extra}
    els["Head"] = {"rotationY": look, "rotationZ": nod}
    els["Tail"] = {"rotationZ": tail}
    return {"frame": frame, "elements": els}


TUCKED = {"LegL": {"rotationZ": 10}, "LegR": {"rotationZ": 10}, "FootL": {"rotationZ": 30}, "FootR": {"rotationZ": 30}}


def air(frame, arm, hand, extra=None):
    k = g.key(frame, arm, hand)
    k["elements"].update(TUCKED)
    if extra: k["elements"].update(extra)
    return k


def animations():
    # soft, deep, unhurried beats
    flap = [air(0, 0, -14, {"Body": {"rotationZ": 1}}), air(8, 40, 10, {"Body": {"rotationZ": 0}}),
            air(15, 0, 18, {"Body": {"rotationZ": -1}}), air(23, -34, -8, {"Body": {"rotationZ": 0}})]
    glide = [air(0, 3, 0), air(20, 1, 2), air(40, 3, 0)]
    dangle = {"LegL": {"rotationZ": -55}, "LegR": {"rotationZ": -65}, "FootL": {"rotationZ": -30}, "FootR": {"rotationZ": -30}}

    def takeoff(frame, arm, hand):
        k = g.key(frame, arm, hand); k["elements"].update(dangle); k["elements"]["Body"] = {"rotationZ": -24}
        return k

    def flare(frame, arm, hand):
        """Landing, and the pounce: wings high and spread, body pitched up, talons thrust forward."""
        k = g.key(frame, arm, hand)
        k["elements"].update({"Body": {"rotationZ": -34}, "Neck": {"rotationZ": 18}, "Head": {"rotationZ": 10},
                              "LegL": {"rotationZ": -135}, "LegR": {"rotationZ": -135},
                              "FootL": {"rotationZ": -40}, "FootR": {"rotationZ": -40}})
        k["elements"]["ArmL"]["rotationY"] = -12; k["elements"]["ArmR"]["rotationY"] = 12
        return k
    # sitting: still, head turning slowly far round and back, now and then a little bob
    stand = [sit(0), sit(30, look=0), sit(50, look=85), sit(80, look=85), sit(95, look=0, nod=-6), sit(110, look=0),
             sit(130, look=-95), sit(160, look=-95), sit(180, look=0)]
    # hooting: leaning forward, head lowered, tail cocked, a pulse for the notes
    hoot = [sit(0, body=-50, nod=8, tail=34), sit(6, body=-48, nod=14, tail=38, neck_extra=-6), sit(14, body=-50, nod=8, tail=34),
            sit(30, body=-50, nod=10, tail=36), sit(40, body=-50, nod=8, tail=34)]
    # eating its catch on the ground: leaning well forward, tugging and gulping
    def gulp(frame, down):
        return sit(frame, body=-22, nod=12 + down * 0.6, tail=10, neck_extra=24 + down * 0.6)
    eat = [gulp(0, 0), gulp(5, 30), gulp(9, 10), gulp(14, 34), gulp(20, 0), sit(28, body=-40, nod=-12), sit(40, body=-40, nod=-12)]
    walk = [sit(0, body=-45, swing=(18, -12)), sit(7, body=-45, hop=0.15), sit(14, body=-45, swing=(-12, 18)), sit(21, body=-45, hop=0.15)]
    death = [g.ground_key(0, Body=(0, 0, 0)), g.ground_key(6, Body=(35, 0, 0), Neck=(0, 0, 10), Head=(0, 0, 10)),
             g.ground_key(14, Body=(82, 0, 0), Neck=(0, 0, 20), Head=(0, 0, 10), LegL=(0, 0, -30), LegR=(0, 0, -15)),
             g.ground_key(23, Body=(82, 0, 0), Neck=(0, 0, 20), Head=(0, 0, 10), LegL=(0, 0, -30), LegR=(0, 0, -15))]
    return [
        {"name": "fly-flap", "code": "fly-flap", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": flap},
        {"name": "fly-soar", "code": "fly-soar", "quantityframes": 60, "onAnimationEnd": "Repeat", "keyframes": glide},
        {"name": "takeoff", "code": "takeoff", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [
            takeoff(0, 0, -22), takeoff(5, 55, 10), takeoff(10, 0, 26), takeoff(15, -45, -10)]},
        {"name": "land", "code": "land", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [flare(0, 42, -10), flare(10, 34, -4)]},
        {"name": "idle-stand", "code": "idle-stand", "quantityframes": 190, "onAnimationEnd": "Repeat", "keyframes": stand},
        {"name": "hoot", "code": "hoot", "quantityframes": 44, "onAnimationEnd": "Repeat", "keyframes": hoot},
        {"name": "walk", "code": "walk", "quantityframes": 28, "onAnimationEnd": "Repeat", "keyframes": walk},
        {"name": "eat", "code": "eat", "quantityframes": 44, "onAnimationEnd": "Repeat", "keyframes": eat},
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
    g.uv_guide(root, img).save(os.path.join(HERE, "owl_uv_guide.png"))
    zs = [v for e in root.walk() for v in (e.frm[2], e.to[2])]
    print("wingspan", round((max(zs) - min(zs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
