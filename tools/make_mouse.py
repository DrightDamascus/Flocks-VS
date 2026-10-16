"""Generates the wood mouse shape, texture and animations for Flocks (the tawny owl's prey).

Run from the repo root:
  python tools/make_mouse.py            shape + animations; texture only if it does not exist yet
  python tools/make_mouse.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/animal/mouse.json, textures/entity/animal/mouse.png and
tools/mouse_uv_guide.png.

Built from make_greylag.py's pieces (elements, UV packing, painting). Head to -X like vanilla's animals, so vanilla's
AI turns it the right way. Modelled at twice the size it is drawn (entity client.size 0.5) for finer texture pixels:
warm brown back, a sharp line to the white belly, big dark eyes, big ears, a long tail. Animations named for vanilla's
AI tasks (entities/mouse.json): idle, sniff, walk, run, eat (sitting up), sleep (curled), death.
"""
import json, math, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import make_greylag as g
from make_greylag import El, mul, mix, frac

HERE = g.HERE
ROOT = g.ROOT
TEX = "mouse"
SUBDIR = "animal"
g.TEX = TEX

BROWN, BROWN_DK, BROWN_LT = (138, 96, 60), (104, 70, 42), (164, 120, 78)
BELLY = (222, 216, 204)
EAR = (168, 122, 104)
PINK = (204, 146, 140)
EYE = (14, 10, 10)
TAIL, TAIL_UNDER = (122, 98, 80), (190, 170, 156)


def fur(x, y, z):
    """Brown with a fine ticked look."""
    return BROWN_DK if frac(x * 1.3 + z * 0.7) < 0.14 else (BROWN_LT if frac(x * 0.9 - z * 1.1 + y) < 0.12 else BROWN)


def m_body(x, y, z, face, fu, fv):
    if face == "down": return BELLY
    if face in ("north", "south"): return BELLY if y < 2.4 else fur(x, y, z)
    if face == "west": return BELLY if y < 2.6 else fur(x, y, z)
    return fur(x, y, z)


def m_head(x, y, z, face, fu, fv):
    if face == "down": return BELLY
    if face in ("north", "south"):
        if y < 2.9: return BELLY
        return fur(x, y, z)
    return fur(x, y, z)


def m_snout(x, y, z, face, fu, fv):
    if face == "west" and y > 3.0: return PINK
    return BELLY if y < 2.9 or face == "down" else fur(x, y, z)


def m_eye(x, y, z, face, fu, fv):
    return EYE if face in ("north", "south", "west", "up") else BROWN_DK


def m_ear(x, y, z, face, fu, fv):
    return mix(EAR, PINK, 0.4) if face == "west" else EAR


def m_leg(x, y, z, face, fu, fv):
    return PINK if y < 0.5 else (BELLY if face != "east" else BROWN)


def m_tail(x, y, z, face, fu, fv):
    return TAIL_UNDER if face == "down" else (TAIL if frac(x * 1.6) > 0.15 else mul(TAIL, 0.85))


def shape():
    body = El("Body", [3.8, 1.4, 6.2], [10.4, 4.8, 9.8], m_body, origin=[7.0, 2.4, 8])
    head = El("Head", [0.6, 1.8, 6.6], [4.4, 4.8, 9.4], m_head, origin=[4.2, 3.2, 8])
    snout = El("Snout", [-0.5, 2.0, 7.2], [0.6, 3.6, 8.8], m_snout)
    eye_l = El("EyeL", [1.5, 3.6, 9.4], [2.4, 4.4, 9.65], m_eye)
    eye_r = El("EyeR", [1.5, 3.6, 6.35], [2.4, 4.4, 6.6], m_eye)
    ear_l = El("EarL", [3.0, 4.5, 8.5], [3.5, 6.4, 10.0], m_ear, origin=[3.25, 4.6, 9.0], rot=(-15, 0, 0))
    ear_r = El("EarR", [3.0, 4.5, 6.0], [3.5, 6.4, 7.5], m_ear, origin=[3.25, 4.6, 7.0], rot=(15, 0, 0))
    head.add(snout, eye_l, eye_r, ear_l, ear_r)
    tail1 = El("Tail1", [10.2, 2.4, 7.7], [14.6, 2.9, 8.3], m_tail, origin=[10.2, 2.65, 8])
    tail2 = El("Tail2", [14.6, 2.4, 7.75], [19.0, 2.85, 8.25], m_tail, origin=[14.6, 2.65, 8])
    tail1.add(tail2)
    # front legs short, hind legs with big feet
    leg_fl = El("LegFL", [4.6, 0.0, 8.5], [5.6, 2.2, 9.3], m_leg, origin=[5.1, 2.0, 8.9])
    leg_fr = El("LegFR", [4.6, 0.0, 6.7], [5.6, 2.2, 7.5], m_leg, origin=[5.1, 2.0, 7.1])
    leg_hl = El("LegHL", [8.0, 0.0, 8.6], [10.2, 2.4, 9.7], m_leg, origin=[9.4, 2.2, 9.1])
    leg_hr = El("LegHR", [8.0, 0.0, 6.3], [10.2, 2.4, 7.4], m_leg, origin=[9.4, 2.2, 6.9])
    body.add(head, tail1, leg_fl, leg_fr, leg_hl, leg_hr)
    return body


def key(frame, **parts):
    els = {}
    for name, v in parts.items():
        if len(v) == 3: els[name] = {"rotationX": v[0], "rotationY": v[1], "rotationZ": v[2]}
        else: els[name] = {"rotationX": v[0], "rotationY": v[1], "rotationZ": v[2], "offsetX": 0, "offsetY": v[3], "offsetZ": 0}
    return {"frame": frame, "elements": els}


def animations():
    tail = dict(Tail1=(0, 0, 6), Tail2=(0, 0, -4))
    idle = [key(0, Head=(0, 0, 0), **tail), key(20, Head=(0, 8, 2), **tail), key(40, Head=(0, -6, 0), Tail1=(0, 4, 6), Tail2=(0, 6, -4)),
            ]
    # sniffing: quick little nods of the nose
    sniff = [key(0, Head=(0, 0, 4), **tail), key(3, Head=(0, 0, -6), **tail), key(6, Head=(0, 0, 4), **tail),
             key(9, Head=(0, 0, -6), **tail), key(12, Head=(0, 6, 4), **tail), key(20, Head=(0, 6, 4), **tail)]

    def gait(frame, a, b, body=0.0, lift=0.0, tl=6):
        return key(frame, Body=(0, 0, body, lift), LegFL=(0, 0, a), LegFR=(0, 0, -a), LegHL=(0, 0, b), LegHR=(0, 0, -b),
                   Tail1=(0, 0, tl), Tail2=(0, 0, -2))
    walk = [gait(0, 30, -30), gait(5, 0, 0, lift=0.2), gait(10, -30, 30), gait(15, 0, 0, lift=0.2)]

    def bound(frame, front, hind, body, lift):
        return key(frame, Body=(0, 0, body, lift), LegFL=(0, 0, front), LegFR=(0, 0, front), LegHL=(0, 0, hind), LegHR=(0, 0, hind),
                   Tail1=(0, 0, 12), Tail2=(0, 0, 4))
    run = [bound(0, -45, 40, 4, 0.0), bound(3, 0, -40, -8, 1.2), bound(6, 45, -50, 6, 0.6), bound(9, -45, 40, 4, 0.0)]
    # sitting up on the haunches, nibbling at something held in the front paws
    def nib(frame, chew):
        return key(frame, Body=(0, 0, -40, 1.2), Head=(0, 0, 30 + chew), LegFL=(0, 0, -60), LegFR=(0, 0, -60),
                   LegHL=(0, 0, 40), LegHR=(0, 0, 40), Tail1=(0, 0, 44), Tail2=(0, 0, -6))
    eat = [nib(0, 0), nib(4, 8), nib(8, 0), nib(12, 8), nib(16, 0), nib(30, 0)]
    # curled up: low, head tucked, tail round
    curl = dict(Body=(0, 0, 0, -0.6), Head=(0, 30, 18), LegFL=(0, 0, 70), LegFR=(0, 0, 70), LegHL=(0, 0, -70), LegHR=(0, 0, -70),
                Tail1=(0, 70, 0), Tail2=(0, 80, 0))
    sleep = [key(0, **curl), key(40, **{**curl, "Body": (0, 0, 0, -0.5)})]
    death = [key(0, Body=(0, 0, 0, 0)), key(6, Body=(50, 0, 0, 0.3), LegFL=(0, 0, 20), LegHL=(0, 0, -20)),
             key(12, Body=(88, 0, 0, 0.6), LegFL=(0, 0, 30), LegFR=(0, 0, 10), LegHL=(0, 0, -30), LegHR=(0, 0, -10), Tail1=(0, 20, 0)),
             key(20, Body=(88, 0, 0, 0.6), LegFL=(0, 0, 30), LegFR=(0, 0, 10), LegHL=(0, 0, -30), LegHR=(0, 0, -10), Tail1=(0, 20, 0))]
    return [
        {"name": "idle", "code": "idle", "quantityframes": 60, "onAnimationEnd": "Repeat", "keyframes": idle},
        {"name": "sniff", "code": "sniff", "quantityframes": 24, "onAnimationEnd": "Repeat", "keyframes": sniff},
        {"name": "walk", "code": "walk", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": walk},
        {"name": "run", "code": "run", "quantityframes": 12, "onAnimationEnd": "Repeat", "keyframes": run},
        {"name": "eat", "code": "eat", "quantityframes": 32, "onAnimationEnd": "Repeat", "keyframes": eat},
        {"name": "sleep", "code": "sleep", "quantityframes": 80, "onAnimationEnd": "Repeat", "keyframes": sleep},
        {"name": "death", "code": "death", "quantityframes": 21, "onActivityStopped": "PlayTillEnd", "onAnimationEnd": "Hold", "keyframes": death},
    ]


def main():
    repaint = "--texture" in sys.argv
    shapes = os.path.join(ROOT, "shapes", "entity", SUBDIR)
    textures = os.path.join(ROOT, "textures", "entity", SUBDIR)
    os.makedirs(shapes, exist_ok=True); os.makedirs(textures, exist_ok=True)
    root = shape()
    height = g.pack(root)
    data = {
        "textureWidth": g.SHEET_W // g.PX, "textureHeight": height // g.PX,
        "textures": {TEX: "flocks:entity/" + SUBDIR + "/" + TEX},
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
    g.uv_guide(root, img).save(os.path.join(HERE, "mouse_uv_guide.png"))
    xs = [v for e in root.walk() for v in (e.frm[0], e.to[0])]
    print("nose to tail tip", round((max(xs) - min(xs)) / 16, 2), "blocks at size 1")


if __name__ == "__main__":
    main()
