"""Generates the gosling (a young greylag) shape, texture and animations for Flocks.

Run from the repo root:
  python tools/make_gosling.py            shape + animations; texture only if it doesn't exist yet
  python tools/make_gosling.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/gosling.json, textures/entity/bird/gosling.png and
tools/gosling_uv_guide.png.

Uses make_greylag.py's pieces (elements, UV packing, painting) but its own shape and animations: a downy chick has
none of the adult's flight feathers. Big round head, short dark bill, a plump body in olive-grey down, pale yellow
face and underside, wing stubs on the flanks and stout legs with big feet. The legs stand straight down in the rest
pose (the adult's are tucked for flight). It never flies, so there are no flight animations: swim, idle-stand, walk,
graze and death. Same conventions as the greylag: head to -X, left to +Z, up +Y, 16 units = 1 block before the
entity's size; Body pitch is rotationZ (negative = nose up), a negative rotationX raises the left (+Z) stub.
"""
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac

HERE = g.HERE
ROOT = g.ROOT
TEX = "gosling"
g.TEX = TEX                    # El.json names faces' texture "#" + make_greylag.TEX

# ---------------------------------------------------------------- palette (greylag gosling down)
TOP, TOP_DK, TOP_LT = (118, 118, 70), (94, 95, 58), (144, 144, 88)
SIDE = (172, 166, 98)
UNDER, UNDER_LT = (224, 208, 126), (234, 222, 152)
FACE = (216, 200, 112)
CAP = (112, 106, 70)
BILL, NAIL = (58, 58, 56), (120, 116, 104)
FEET = (96, 96, 90)
EYE = (18, 16, 14)


def down(base, x, y, z):
    """Soft down: faint lighter tufts, so the colour isn't flat (the painter adds its own per-pixel noise)."""
    return mix(base, (255, 248, 200), 0.12) if frac(x * 1.7 + y * 0.9 + z * 1.3) < 0.18 else base


# ---------------------------------------------------------------- materials (see make_greylag for the convention)
def m_body(x, y, z, face, fu, fv):
    if face == "up": return down(TOP if frac(x * 0.6 + z * 0.4) > 0.25 else TOP_DK, x, y, z)
    if face == "down": return UNDER
    if face == "west": return UNDER
    if face == "east": return down(SIDE if y < 4.2 else TOP, x, y, z)
    if y > 4.4: return down(TOP, x, y, z)
    if y > 3.2: return down(SIDE, x, y, z)
    return down(UNDER, x, y, z)


def m_breast(x, y, z, face, fu, fv):
    if face == "up": return TOP
    return down(UNDER_LT if y < 4.2 else FACE, x, y, z)


def m_rump(x, y, z, face, fu, fv):
    if face == "down": return UNDER
    return down(TOP_DK if y > 4.0 else SIDE, x, y, z)


def m_neck(x, y, z, face, fu, fv):
    if face == "up": return TOP
    if face == "east": return TOP_DK
    return down(FACE, x, y, z)


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south"):
        if -2.9 < x < -2.1 and 5.7 < y < 6.5: return EYE
        if 5.85 < y < 6.25 and x < -2.9: return mix(FACE, CAP, 0.55)            # faint line from the bill to the eye
    if face == "up": return down(CAP, x, y, z)
    if face == "east": return CAP if y > 5.6 else FACE
    return down(CAP if y > 6.6 else FACE, x, y, z)


def m_bill(x, y, z, face, fu, fv):
    if x < -4.7 or face == "west": return NAIL
    return mul(BILL, 0.85) if face == "down" else BILL


def m_feet(x, y, z, face, fu, fv):
    return mul(FEET, 0.82) if face == "down" else FEET


def m_stub(x, y, z, face, fu, fv):
    return down(TOP_LT if face == "down" else TOP, x, y, z)


def m_tuft(x, y, z, face, fu, fv):
    return TOP_DK


# ---------------------------------------------------------------- geometry
def shape():
    body = El("Body", [0.0, 1.6, 5.6], [5.6, 5.4, 10.4], m_body)
    breast = El("Breast", [-0.6, 2.1, 6.1], [0.0, 5.0, 9.9], m_breast)
    rump = El("Rump", [5.6, 2.3, 6.3], [6.4, 5.0, 9.7], m_rump)
    tuft = El("Tail", [6.2, 4.2, 7.3], [6.9, 4.9, 8.7], m_tuft)
    neck = El("Neck", [-1.2, 3.6, 6.7], [0.8, 5.8, 9.3], m_neck, origin=[0.8, 4.6, 8])
    head = El("Head", [-3.8, 4.2, 6.4], [-0.8, 7.2, 9.6], m_head, origin=[-0.8, 5.0, 8])
    bill = El("Bill", [-5.0, 5.1, 7.35], [-3.8, 5.85, 8.65], m_bill)
    stub_l = El("WingL", [1.4, 3.2, 10.4], [4.6, 4.9, 10.9], m_stub, origin=[1.6, 4.7, 10.4])
    stub_r = El("WingR", [1.4, 3.2, 5.1], [4.6, 4.9, 5.6], m_stub, origin=[1.6, 4.7, 5.6])
    foot_l = El("FootL", [1.8, 0.0, 8.3], [3.8, 0.4, 9.7], m_feet)
    foot_r = El("FootR", [1.8, 0.0, 6.3], [3.8, 0.4, 7.7], m_feet)
    leg_l = El("LegL", [3.0, 0.4, 8.7], [3.6, 1.9, 9.3], m_feet, origin=[3.3, 1.9, 9.0])
    leg_r = El("LegR", [3.0, 0.4, 6.7], [3.6, 1.9, 7.3], m_feet, origin=[3.3, 1.9, 7.0])
    leg_l.add(foot_l); leg_r.add(foot_r)
    body.add(breast, rump, tuft, neck.add(head.add(bill)), stub_l, stub_r, leg_l, leg_r)
    return body


# ---------------------------------------------------------------- animations
def pose(frame, **parts):
    """A keyframe from element rotations (rx, ry, rz) and, as a 4th value, an offsetY."""
    els = {}
    for name, v in parts.items():
        els[name] = {"rotationX": v[0], "rotationY": v[1], "rotationZ": v[2]}
        if len(v) > 3: els[name]["offsetY"] = v[3]
    return {"frame": frame, "elements": els}


def animations():
    def swim(frame, paddle, bob):
        """Afloat: legs swept back under the body, paddling in turn; head up, a slight bob."""
        return pose(frame, Body=(0, 0, bob * 0.4), Neck=(0, 0, -bob), Head=(0, 0, bob * 0.6),
                    LegL=(0, 0, 55 + paddle), LegR=(0, 0, 55 - paddle))
    def stand(frame, neck):
        return pose(frame, Neck=(0, 0, neck), Head=(0, 0, -neck * 0.5))
    def walk(frame, leg, bob, stub):
        """A busy waddle: quick alternate steps, the body rocking, the stubs lifting now and then."""
        return pose(frame, Body=(-bob * 2, 0, 0), Neck=(0, 0, bob), Head=(0, 0, -bob),
                    LegL=(0, 0, leg), LegR=(0, 0, -leg), WingL=(-stub, 0, 0), WingR=(stub, 0, 0))
    def graze(frame, dip):
        """Head down, nibbling at the grass: the neck dips and the bill pecks."""
        return pose(frame, Body=(0, 0, 8), Neck=(0, 0, 26 + dip), Head=(0, 0, 14 + dip * 0.8),
                    LegL=(0, 0, -8), LegR=(0, 0, -8))
    def death(frame, roll):
        return pose(frame, Body=(roll, 0, 0, -roll / 90 * 1.6), Neck=(0, 0, roll * 0.6), Head=(0, 0, roll * 0.3),
                    LegL=(0, 0, -roll * 0.4), LegR=(0, 0, -roll * 0.2))
    return [
        {"name": "swim", "code": "swim", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": [
            swim(0, 18, 0), swim(8, 0, 2), swim(15, -18, 0), swim(23, 0, 2)]},
        {"name": "idle-stand", "code": "idle-stand", "quantityframes": 40, "onAnimationEnd": "Repeat", "keyframes": [
            stand(0, 0), stand(20, 4)]},
        {"name": "walk", "code": "walk", "quantityframes": 16, "onAnimationEnd": "Repeat", "keyframes": [
            walk(0, 28, 0, 0), walk(4, 0, 3, 12), walk(8, -28, 0, 0), walk(12, 0, 3, 6)]},
        {"name": "graze", "code": "graze", "quantityframes": 24, "onAnimationEnd": "Repeat", "keyframes": [
            graze(0, 0), graze(6, 8), graze(12, 0), graze(18, 6)]},
        {"name": "death", "code": "death", "quantityframes": 20, "onActivityStopped": "PlayTillEnd", "onAnimationEnd": "Hold",
         "keyframes": [death(0, 0), death(6, 40), death(12, 85), death(19, 85)]},
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
    g.uv_guide(root, img).save(os.path.join(HERE, "gosling_uv_guide.png"))
    xs = [v for e in root.walk() for v in (e.frm[0], e.to[0])]
    print("length", round((max(xs) - min(xs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
