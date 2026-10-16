"""Generates the grey heron shape, texture and animations for Flocks.

Run from the repo root:
  python tools/make_heron.py            shape + animations; texture only if it does not exist yet
  python tools/make_heron.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/heron.json, textures/entity/bird/heron.png and tools/heron_uv_guide.png.

Built from make_greylag.py's pieces (elements, UV packing, painting, keyframe helpers). Its own geometry: a slim body,
a neck in two segments (Neck, Neck2) so it can make the heron's S, a crested head, a long dagger bill, long legs and
broad rounded wings. Its own animations: flight with the neck drawn back into the shoulders and the legs trailing,
a hunched rest, an alert stretch, a stalking walk with the neck low and forward, the strike, takeoff, landing and
death (flocks.waterfowl in wading mode plays them).

Neck conventions: each neck segment and the head extend toward -X from their origin, so a negative rotationZ raises
their far end. A segment's pitch above forward-horizontal is minus the sum of its own and its parents' rotationZ.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac

HERE = g.HERE
ROOT = g.ROOT
TEX = "heron"
g.TEX = TEX

# ---------------------------------------------------------------- palette (grey heron)
GREY, GREY_LT, GREY_DK = (134, 142, 150), (170, 176, 182), (104, 110, 118)
PLUME = (196, 200, 204)          # pale scapular plumes on the back
BLACK, BLACK_LT = (36, 36, 40), (60, 60, 66)
WHITE, NECK_W = (234, 234, 230), (212, 212, 208)
STREAK = (58, 58, 64)            # black streaks down the front of the neck
BILL, BILL_DK = (210, 172, 70), (176, 140, 56)
LEGS = (160, 132, 88)
EYE = (226, 196, 64)


# ---------------------------------------------------------------- materials
def m_body(x, y, z, face, fu, fv):
    if face == "up":
        return PLUME if frac(z * 0.9 + x * 0.12) > 0.78 else GREY       # long pale plumes along the back
    if face == "down": return WHITE if frac(x * 0.6) > 0.25 else mix(WHITE, STREAK, 0.35)
    if face in ("north", "south"):
        if x < 4.2 and y > 4.6: return BLACK                            # black shoulder patch
        if y < 3.6: return mix(WHITE, GREY_LT, 0.4)
        return GREY_LT if y < 4.6 else GREY
    return GREY_LT


def m_rump(x, y, z, face, fu, fv):
    return GREY if face == "up" else GREY_LT


def m_neck(x, y, z, face, fu, fv):
    # pale neck; the front (the down face, which faces forward when the neck is raised) carries a line of black streaks
    if face == "down": return STREAK if abs(z - 8.0) < 0.18 and frac(x * 1.1) < 0.6 else NECK_W
    return NECK_W if face != "up" else mix(NECK_W, GREY_LT, 0.4)


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south") and -9.55 < x < -9.05 and 5.35 < y < 5.85: return EYE
    if face in ("north", "south") and 5.45 < y < 5.95 and x > -9.6: return BLACK   # stripe from the eye to the crest
    if face == "up" and abs(z - 8.0) > 0.45: return BLACK                       # dark sides of the crown
    return WHITE


def m_crest(x, y, z, face, fu, fv):
    return BLACK


def m_bill(x, y, z, face, fu, fv):
    return BILL_DK if face == "down" else BILL


def m_legs(x, y, z, face, fu, fv):
    return mul(LEGS, 0.85) if face == "down" else LEGS


def m_edge(x, y, z, face, fu, fv):
    return GREY_DK


def m_tail(x, y, z, face, fu, fv):
    end = 14.4 - x
    edge = abs(z - 8.0) / 1.9
    if end < 0.9 * edge ** 2 - 0.1: return None                             # short rounded tail
    return GREY if frac((z - 6.1) / 0.95) > 0.12 else GREY_DK


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: pale leading edge, grey coverts, then dark grey-black secondaries (the contrast herons show in flight)."""
    c, s = g.wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    lz = frac(abs(z - 8.0) / 1.5)
    if c > 0.95 and (lz < 0.16 or lz > 0.84): return None
    if c < 0.08: return WHITE
    if c < 0.55: return GREY_LT if frac((c - 0.08) / 0.12) > 0.75 else GREY
    return BLACK_LT if 0.12 < lz < 0.88 else BLACK


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: broad rounded black primaries with grey coverts at the leading edge, tips slightly fingered."""
    c, s = g.wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.12 * s ** 2
    trail = 1.0 - 0.45 * s ** 2.2                    # rounded, not pointed
    if s > 0.985 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)
    if k < 0.25: return GREY
    lf = frac((k - 0.25) / 0.75 * 6.0)
    if k > 0.8 and s > 0.7 and lf < 0.2: return None                     # separated tips
    return BLACK if lf > 0.12 else mul(BLACK, 0.8)


# ---------------------------------------------------------------- geometry
ARM_X = (2.6, 10.6)
ARM_ROOT, ARM_LEN = 2.0, 9.0
HAND_X = (3.0, 10.0)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 9.0
WING_Y = 5.6
mz = g.mz
LEG = 22.0                                    # 0.96 blocks at the entity's size 0.7
HIP_L, HIP_R = (10.0, 3.45, 8.55), (10.0, 3.45, 7.45)
BODY_ORIGIN = (6.5, 4.5)                      # the Body element's rotation origin (x, y): the centre of its box


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 3, WING_Y, z_root if L else mz(z_root)])
    under = El("Arm" + side + "u", arm.frm, arm.to, m_arm, faces=("up",), uv_from=arm,
               origin=[ARM_X[0], WING_Y, arm.origin[2]], rot=(0, 0, 5))
    edge_a = El("EdgeArm" + side, [ARM_X[0] - 0.2, WING_Y - 0.2, a0], [ARM_X[0] + 0.5, WING_Y + 0.2, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 2.5, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.5)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.1, WING_Y - 0.15, e0], [HAND_X[0] + 0.5, WING_Y + 0.15, e1], m_edge)
    return arm.add(under, edge_a, hand.add(edge_h))


def shape():
    body = El("Body", [2.0, 2.8, 6.0], [11.0, 6.2, 10.0], m_body)
    rump = El("Rump", [11.0, 3.4, 6.6], [12.2, 5.6, 9.4], m_rump)
    neck = El("Neck", [-3.0, 4.4, 7.4], [2.0, 5.6, 8.6], m_neck, origin=[2.0, 5.0, 8])
    neck2 = El("Neck2", [-8.0, 4.5, 7.5], [-3.0, 5.5, 8.5], m_neck, origin=[-3.0, 5.0, 8])
    head = El("Head", [-10.4, 4.3, 7.1], [-8.0, 6.1, 8.9], m_head, origin=[-8.0, 5.2, 8])
    bill = El("Bill", [-15.0, 4.7, 7.65], [-10.4, 5.4, 8.35], m_bill)
    crest = El("Crest", [-7.4, 5.85, 7.85], [-4.6, 6.15, 8.15], m_crest, origin=[-8.0, 6.0, 8], rot=(0, 0, 12))
    tail = El("Tail", [11.5, 5.4, 6.1], [14.4, 5.4, 9.9], m_tail, faces=("up",), origin=[11.5, 5.4, 8], rot=(0, 0, 4))
    # freakishly long legs (Dright): long enough to stand on the bottom of a full block of water with the body clear of it
    leg_l = El("LegL", [10.0, 3.2, 8.3], [10.0 + LEG, 3.7, 8.8], m_legs, origin=list(HIP_L))
    leg_r = El("LegR", [10.0, 3.2, 7.2], [10.0 + LEG, 3.7, 7.7], m_legs, origin=list(HIP_R))
    foot_l = El("FootL", [10.0 + LEG, 3.1, 8.0], [13.0 + LEG, 3.6, 9.1], m_legs)
    foot_r = El("FootR", [10.0 + LEG, 3.1, 6.9], [13.0 + LEG, 3.6, 8.0], m_legs)
    leg_l.add(foot_l); leg_r.add(foot_r)
    body.add(rump, neck.add(neck2.add(head.add(bill, crest))), tail, leg_l, leg_r, wing("L"), wing("R"))
    return body


# ---------------------------------------------------------------- animation
def neck(lower, upper, head):
    """Neck pose from absolute pitches in degrees above forward-horizontal: lower and upper neck, and the head (bill)."""
    return {"Neck": {"rotationZ": -lower}, "Neck2": {"rotationZ": -(upper - lower)}, "Head": {"rotationZ": -(head - upper)}}


def with_(base, **parts):
    els = {k: dict(v) for k, v in base.items()}
    for name, rot in parts.items():
        els[name] = dict(rot) if isinstance(rot, dict) else {"rotationX": rot[0], "rotationY": rot[1], "rotationZ": rot[2]}
    return els


FOLDED = g.FOLDED


def stance(body, swing_l=0.0, swing_r=0.0):
    """Ground legs for a body tilt (rotationZ, degrees): each leg hangs straight down plus its swing whatever the body's
    tilt, feet flat and forward, and the Body is lifted (offsetY, model units) so the lower foot rests at y = 0.
    The flight poses have no lift: there the legs trail behind."""
    t = math.radians(body)
    hip_y = BODY_ORIGIN[1] + (HIP_L[0] - BODY_ORIGIN[0]) * math.sin(t) + (HIP_L[1] - BODY_ORIGIN[1]) * math.cos(t)
    lowest = min(math.sin(math.radians(-90 + sw)) for sw in (swing_l, swing_r))   # -1 when a leg hangs straight down
    lift = round(-(hip_y + LEG * lowest - 0.3), 2)
    return {"Body": {"rotationZ": body, "offsetY": lift},
            "LegL": {"rotationZ": -90 + swing_l - body}, "LegR": {"rotationZ": -90 + swing_r - body},
            "FootL": {"rotationZ": -90 - swing_l}, "FootR": {"rotationZ": -90 - swing_r}}


DRAWN_IN = neck(105, -35, 0)                 # flight: neck up and back between the shoulders, head forward
TRAIL = {"LegL": {"rotationZ": -6}, "LegR": {"rotationZ": -6}, "FootL": {"rotationZ": -8}, "FootR": {"rotationZ": -8}}


def flight_key(frame, arm, hand, extra=None):
    k = g.key(frame, arm, hand)
    k["elements"].update(DRAWN_IN); k["elements"].update(TRAIL)
    if extra: k["elements"].update(extra)
    return k


def ground(frame, neckpose, body=-18, swing_l=0.0, swing_r=0.0, extra=None):
    els = with_(FOLDED)
    els.update(neckpose)
    els.update(stance(body, swing_l, swing_r))
    if extra: els.update(extra)
    return {"frame": frame, "elements": els}


def animations():
    # slow, deep, arched beats; neck drawn in and legs trailing throughout
    flap = [
        flight_key(0, 0, -18, {"Body": {"rotationZ": 1}}),
        flight_key(8, 40, 6, {"Body": {"rotationZ": 0}}),
        flight_key(16, 2, 22, {"Body": {"rotationZ": -1}}),
        flight_key(24, -38, -14, {"Body": {"rotationZ": 0}}),
    ]
    soar = [flight_key(0, 4, -6), flight_key(15, 2, -4)]     # wings bowed slightly down
    # takeoff: big strokes, neck half out, legs dangling
    half_out = neck(55, 25, 0)
    dangle = {"LegL": {"rotationZ": -55}, "LegR": {"rotationZ": -65}, "FootL": {"rotationZ": -40}, "FootR": {"rotationZ": -40}}
    def takeoff(frame, arm, hand):
        k = g.key(frame, arm, hand); k["elements"].update(half_out); k["elements"].update(dangle)
        k["elements"]["Body"] = {"rotationZ": -22}
        return k
    # landing: wings high and cupped, legs thrust forward-down, neck up in an S
    def flare(frame, arm, hand):
        k = g.key(frame, arm, hand); k["elements"].update(neck(70, 110, 0))
        k["elements"].update({"LegL": {"rotationZ": -125}, "LegR": {"rotationZ": -125}, "FootL": {"rotationZ": -60}, "FootR": {"rotationZ": -60}})
        k["elements"]["Body"] = {"rotationZ": -32}
        k["elements"]["ArmL"]["rotationY"] = -12; k["elements"]["ArmR"]["rotationY"] = 12
        return k
    # on the ground
    rest = [ground(0, neck(100, -10, 0), body=-14), ground(30, neck(98, -8, 2), body=-14)]        # hunched, head on the shoulders
    alert = [ground(0, neck(78, 95, 0), body=-30), ground(20, neck(80, 96, 0), body=-30)]          # stretched up, watching
    stalk = [neck(28, 4, -8), neck(26, 2, -10)]                                                    # neck low and forward
    def step(frame, l, r, n):
        return ground(frame, stalk[n], body=-10, swing_l=l, swing_r=r)
    walk = [step(0, 16, -10, 0), step(10, 0, 0, 1), step(20, -10, 16, 0), step(30, 0, 0, 1)]   # slow, deliberate steps
    # the strike: cocked back, lunge down and forward, hold, recover
    strike = [ground(0, neck(60, 150, 0), body=-12), ground(6, neck(64, 158, 4), body=-12),
              ground(9, neck(10, -35, -55), body=8), ground(16, neck(12, -32, -50), body=8), ground(28, neck(100, -10, 0), body=-14)]
    death = [g.ground_key(0, Body=(0, 0, 0), Neck=(0, 0, 0), Neck2=(0, 0, 0), Head=(0, 0, 0)),
             g.ground_key(6, Body=(35, 0, 0), Neck=(0, 0, 10), Neck2=(0, 0, 10), Head=(0, 0, 10)),
             g.ground_key(14, Body=(82, 0, 0), Neck=(0, 0, 15), Neck2=(0, 0, 25), Head=(0, 0, 10), LegL=(0, 0, -30), LegR=(0, 0, -15)),
             g.ground_key(23, Body=(82, 0, 0), Neck=(0, 0, 15), Neck2=(0, 0, 25), Head=(0, 0, 10), LegL=(0, 0, -30), LegR=(0, 0, -15))]
    return [
        {"name": "fly-flap", "code": "fly-flap", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": flap},
        {"name": "fly-soar", "code": "fly-soar", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": soar},
        {"name": "takeoff", "code": "takeoff", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [
            takeoff(0, 0, -22), takeoff(5, 55, 10), takeoff(10, 0, 26), takeoff(15, -45, -10)]},
        {"name": "land", "code": "land", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [flare(0, 36, -16), flare(10, 28, -10)]},
        {"name": "idle-stand", "code": "idle-stand", "quantityframes": 60, "onAnimationEnd": "Repeat", "keyframes": rest},
        {"name": "alert", "code": "alert", "quantityframes": 40, "onAnimationEnd": "Repeat", "keyframes": alert},
        {"name": "walk", "code": "walk", "quantityframes": 40, "onAnimationEnd": "Repeat", "keyframes": walk},
        {"name": "strike", "code": "strike", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": strike},
        # "swim" is what the waterfowl behaviour falls back to on deep water; a heron just stands there hunched
        {"name": "swim", "code": "swim", "quantityframes": 60, "onAnimationEnd": "Repeat", "keyframes": rest},
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
    g.uv_guide(root, img).save(os.path.join(HERE, "heron_uv_guide.png"))
    zs = [v for e in root.walk() for v in (e.frm[2], e.to[2])]
    print("wingspan", round((max(zs) - min(zs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
