"""Generates the greylag goose shape, texture and flight animations for Flocks, in the style of vanilla's hawk.

Run from the repo root:
  python tools/make_greylag.py            shape + animations; texture only if it does not exist yet
  python tools/make_greylag.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/greylag.json, textures/entity/bird/greylag.png, and
tools/greylag_uv_guide.png (an 8x blow-up of the UV layout with labels, for repainting by hand).

Style, copied from vanilla's hawk:
  - wings and tail are zero-thickness planes with only the up face; the renderer draws them double-sided
  - feather edges are cut out with transparent pixels, so the silhouette reads as feathers at a distance
  - a thin box along each wing's leading edge, and a second plane tilted a few degrees under the arm for depth
  - 2 texture pixels per model unit, every face painted for its own place on the bird, with shading baked in

Conventions (same as the hawk, so FlockRenderer's yaw works unchanged): head to -X, wings along +-Z, up +Y,
16 units = 1 block before Species.Scale. A negative rotationX raises the +Z (left) wing.
"""
import hashlib, json, math, os, random, sys
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..", "Flocks", "assets", "flocks")
TEX = "greylag"
PX = 2            # texture pixels per model unit
SHEET_W = 64      # texture width in pixels; height grows to fit

# ---------------------------------------------------------------- palette (greylag goose)
BROWN_DK = (72, 64, 56)
BROWN = (104, 95, 85)
BROWN_LT = (134, 125, 113)
FRINGE = (198, 191, 178)
GREY_DK = (124, 127, 128)
GREY = (152, 156, 157)
GREY_LT = (180, 184, 185)
PRIMARY = (58, 56, 55)
PRIMARY_LT = (84, 82, 80)
SECONDARY = (82, 78, 74)
BELLY = (188, 182, 171)
FLANK_DK = (112, 103, 93)
FLANK_LT = (156, 147, 134)
WHITE = (236, 234, 228)
NECK = (122, 112, 100)
NECK_DK = (106, 97, 87)
HEAD = (112, 102, 91)
BILL = (226, 132, 88)
NAIL = (238, 222, 196)
FEET = (224, 148, 128)
EYE = (30, 27, 25)


def mul(c, k):
    return tuple(max(0, min(255, int(round(v * k)))) for v in c)


def mix(a, b, w):
    return tuple(int(round(x + (y - x) * w)) for x, y in zip(a, b))


def frac(v):
    return v - math.floor(v)


# ---------------------------------------------------------------- materials
# Each material maps a point on the bird (model units, rest pose) plus where it sits on its face (fu, fv in 0..1)
# to a colour, or None for a transparent (cut-out) pixel. Patterns use model coordinates, so they line up across faces.

def mantle(x, z):
    """Back feathers: rows of brown feathers with pale fringes, offset like scales."""
    row = math.floor((x - 2.0) / 1.5)
    lx = frac((x - 2.0) / 1.5)
    lz = frac((z + row * 0.75) / 1.5)
    if lx > 0.68: return FRINGE if 0.15 < lz < 0.85 else BROWN_LT
    if lx < 0.3 and 0.3 < lz < 0.7: return BROWN_DK
    return BROWN


def m_body(x, y, z, face, fu, fv):
    if face == "up": return mantle(x, z)
    if face == "down":
        if x > 10.5: return WHITE
        return BELLY if frac(x * 0.7 + z * 0.3) > 0.12 else mix(BELLY, FLANK_DK, 0.35)
    if face == "east": return WHITE
    if face == "west": return mix(BELLY, BROWN_LT, 0.5)
    # sides: back feathers on top, barred flank below, pale belly at the bottom
    if y > 5.6: return mantle(x, y + 4)
    if y > 3.0:
        bar = frac((x - 2.0) / 1.25)
        if x > 11: return WHITE if y < 4.6 else FLANK_LT
        return FLANK_DK if bar < 0.35 else (FRINGE if bar > 0.85 else FLANK_LT)
    return BELLY


def m_breast(x, y, z, face, fu, fv):
    if face == "up": return NECK
    if face == "down": return BELLY
    return mix(BELLY, BROWN_LT, 0.45)


def m_rump(x, y, z, face, fu, fv):
    if face == "up": return GREY if x < 13.4 else WHITE
    return WHITE


def m_neck(x, y, z, face, fu, fv):
    # greylag necks show fine furrows running along the neck
    across = z if face in ("up", "down") else y
    return NECK_DK if frac(across * 1.0) < 0.5 else NECK


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south") and -5.5 < x < -4.9 and 5.2 < y < 5.8: return EYE
    if face == "up": return mul(HEAD, 0.92)
    return HEAD


def m_bill(x, y, z, face, fu, fv):
    if x < -8.2 or face == "west": return NAIL
    return mul(BILL, 0.88) if face == "down" else BILL


def m_feet(x, y, z, face, fu, fv):
    return mul(FEET, 0.85) if face == "down" else FEET


def m_edge(x, y, z, face, fu, fv):
    return mix(GREY_DK, BROWN, 0.4)


def m_tail(x, y, z, face, fu, fv):
    """Tail plane: grey-brown centre, white sides and tip, rounded feather tips cut out at the end."""
    end = 16.5 - x                       # distance from the tail tip
    lz = frac((z - 6.0) / 1.0)           # 1-unit-wide tail feathers
    edge = abs(z - 8.0) / 2.0            # 0 centre .. 1 outer edge
    if end < 0.55 * (2 * lz - 1) ** 2: return None        # rounded tips
    if end < 1.4 * edge ** 3 - 0.2: return None           # fan shape: outer feathers shorter
    if end < 0.8 or edge > 0.8: return WHITE
    return BROWN if 0.15 < lz < 0.85 else BROWN_DK


def wing_coords(x, z, x0, x1, root, length):
    """c: 0 at the leading edge .. 1 at the trailing edge; s: 0 at the wing root .. 1 at the outer end."""
    return (x - x0) / (x1 - x0), (abs(z - 8.0) - root) / length


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: pale grey coverts in scalloped rows, then dark secondaries with pale tips and a notched edge."""
    c, s = wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    span_u = abs(z - 8.0)
    lz = frac(span_u / 1.5)
    if c > 0.95 and (lz < 0.18 or lz > 0.82): return None          # notches between secondary tips
    if c < 0.1: return GREY_DK
    if c < 0.56:
        band = frac((c - 0.1) / 0.115)
        lz2 = frac((span_u + math.floor((c - 0.1) / 0.115) * 0.75) / 1.5)
        if band > 0.7: return GREY_LT if 0.15 < lz2 < 0.85 else GREY
        return GREY if c > 0.25 else mix(GREY_DK, GREY, 0.6)
    if s < 0.16:                                                     # tertials next to the body are browner
        return FRINGE if c > 0.88 else (BROWN if 0.15 < lz < 0.85 else BROWN_DK)
    if c > 0.88: return mix(SECONDARY, FRINGE, 0.55)
    return SECONDARY if 0.12 < lz < 0.88 else mul(SECONDARY, 0.8)


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: pale primary coverts, then dark primaries tapering to a pointed tip with slightly separated ends."""
    c, s = wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.2 * s ** 2
    trail = 1.0 - 0.62 * s ** 1.5
    if s > 0.985 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)          # 0..1 across the remaining chord
    if k < 0.3:
        band = frac(k / 0.1)
        return GREY_LT if band > 0.72 else GREY
    # primaries run along the span; a lighter shaft in each, darker gaps between them
    n = (k - 0.3) / 0.7 * 5.0
    lf = frac(n)
    if k > 0.85 and s > 0.55 and lf < 0.22: return None              # separated feather ends near the tip
    if lf < 0.14: return mul(PRIMARY, 0.82)
    if 0.48 < lf < 0.58: return PRIMARY_LT
    return PRIMARY


# ---------------------------------------------------------------- geometry
ARM_X = (3.4, 10.6)       # chord of the inner wing (leading, trailing)
ARM_ROOT, ARM_LEN = 2.4, 9.0
HAND_X = (3.9, 10.1)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 9.0
WING_Y = 5.6

FACES = ("north", "east", "south", "west", "up", "down")


class El:
    def __init__(self, name, frm, to, mat, origin=None, rot=(0, 0, 0), faces=FACES, uv_from=None, shade_edges=True):
        self.name, self.frm, self.to, self.mat = name, list(frm), list(to), mat
        self.origin = origin or [(a + b) / 2 for a, b in zip(frm, to)]
        self.rot, self.faces, self.children = rot, faces, []
        self.uv_from = uv_from          # reuse another element's UVs (the hawk does this for its tilted under-planes)
        self.shade_edges = shade_edges
        self.uv = {}

    def add(self, *kids):
        self.children += kids
        return self

    def walk(self):
        yield self
        for c in self.children: yield from c.walk()

    def face_size(self, f):
        dx, dy, dz = (b - a for a, b in zip(self.frm, self.to))
        return {"north": (dx, dy), "south": (dx, dy), "east": (dz, dy), "west": (dz, dy), "up": (dx, dz), "down": (dx, dz)}[f]

    def point(self, f, fu, fv):
        """Model-space point under texture position (fu, fv) of face f; axes per vanilla's CubeMeshUtil tables."""
        (x0, y0, z0), (x1, y1, z1) = self.frm, self.to
        return {
            "north": (x1 - fu * (x1 - x0), y1 - fv * (y1 - y0), z0),
            "east": (x1, y1 - fv * (y1 - y0), z1 - fu * (z1 - z0)),
            "south": (x0 + fu * (x1 - x0), y1 - fv * (y1 - y0), z1),
            "west": (x0, y1 - fv * (y1 - y0), z0 + fu * (z1 - z0)),
            "up": (x0 + fu * (x1 - x0), y1, z0 + fv * (z1 - z0)),
            "down": (x1 - fu * (x1 - x0), y0, z0 + fv * (z1 - z0)),
        }[f]

    def json(self, parent=None):
        # VS stores child from/to/rotationOrigin relative to the parent's "from"
        p = parent.frm if parent else [0, 0, 0]
        rel = lambda v: [round(a - b, 3) for a, b in zip(v, p)]
        d = {"name": self.name, "from": rel(self.frm), "to": rel(self.to), "rotationOrigin": rel(self.origin)}
        for axis, v in zip("XYZ", self.rot):
            if v: d["rotation" + axis] = v
        src = self.uv_from or self
        d["faces"] = {f: {"texture": "#" + TEX, "uv": src.uv[f]} for f in self.faces}
        if self.children: d["children"] = [c.json(self) for c in self.children]
        return d


def mz(z):
    return 16 - z


def wing(side):
    L = side == "L"
    zr = lambda a, b: (a, b) if L else (mz(b), mz(a))     # z-range from the centre-out distances
    z_root, z_mid, z_tip = 8 + ARM_ROOT, 8 + ARM_ROOT + ARM_LEN, 8 + ARM_ROOT + ARM_LEN + HAND_LEN
    a0, a1 = zr(z_root, z_mid)
    arm = El("Arm" + side, [ARM_X[0], WING_Y, a0], [ARM_X[1], WING_Y, a1], m_arm, faces=("up",),
             origin=[ARM_X[0] + 3, WING_Y, z_root if L else mz(z_root)])
    # second plane tilted down at the trailing edge, sharing the arm's texture: gives the inner wing some camber
    under = El("Arm" + side + "u", arm.frm, arm.to, m_arm, faces=("up",), uv_from=arm,
               origin=[ARM_X[0], WING_Y, arm.origin[2]], rot=(0, 0, 5))
    edge_a = El("EdgeArm" + side, [ARM_X[0] - 0.2, WING_Y - 0.2, a0], [ARM_X[0] + 0.5, WING_Y + 0.2, a1], m_edge)
    h0, h1 = zr(z_mid, z_tip)
    hand = El("Wing" + side, [HAND_X[0], WING_Y, h0], [HAND_X[1], WING_Y, h1], m_hand, faces=("up",),
              origin=[HAND_X[0] + 2.5, WING_Y, z_mid if L else mz(z_mid)])
    e0, e1 = zr(z_mid, z_mid + HAND_LEN * 0.55)
    edge_h = El("EdgeHand" + side, [HAND_X[0] - 0.1, WING_Y - 0.15, e0], [HAND_X[0] + 0.5, WING_Y + 0.15, e1], m_edge)
    return arm.add(under, edge_a, hand.add(edge_h))


def shape():
    body = El("Body", [2.5, 2.2, 5.6], [12.5, 6.6, 10.4], m_body)
    breast = El("Breast", [1.2, 2.8, 6.1], [2.5, 6.1, 9.9], m_breast)
    rump = El("Rump", [12.5, 3.3, 6.3], [14.0, 6.0, 9.7], m_rump)
    neck = El("Neck", [-3.5, 4.0, 7.0], [1.5, 6.0, 9.0], m_neck, origin=[1.5, 5.0, 8], rot=(0, 0, -4))
    head = El("Head", [-6.4, 3.9, 6.75], [-3.5, 6.4, 9.25], m_head, origin=[-3.5, 5.1, 8], rot=(0, 0, 6))
    bill = El("Bill", [-8.8, 4.3, 7.3], [-6.4, 5.3, 8.7], m_bill)
    tail = El("Tail", [12.5, 5.2, 6.0], [16.5, 5.2, 10.0], m_tail, faces=("up",), origin=[12.5, 5.2, 8], rot=(0, 0, 4))
    feet_l = El("FootL", [13.0, 3.6, 8.3], [14.8, 4.1, 9.1], m_feet)
    feet_r = El("FootR", [13.0, 3.6, 6.9], [14.8, 4.1, 7.7], m_feet)
    # Legs for the ground poses (0.5.1). In flight they lie tucked along the belly, hidden under the body and rump.
    # They borrow the feet's UVs (uv_from), so the texture layout and any hand edits stay exactly as they were;
    # the feet hang off them so they swing down with the legs when standing.
    leg_l = El("LegL", [10.5, 3.6, 8.45], [13.0, 4.1, 8.95], m_feet, origin=[10.5, 3.85, 8.7], uv_from=feet_l)
    leg_r = El("LegR", [10.5, 3.6, 7.05], [13.0, 4.1, 7.55], m_feet, origin=[10.5, 3.85, 7.3], uv_from=feet_r)
    leg_l.add(feet_l); leg_r.add(feet_r)
    body.add(breast, rump, neck.add(head.add(bill)), tail, leg_l, leg_r, wing("L"), wing("R"))
    return body


# ---------------------------------------------------------------- UV packing and painting
def pack(root):
    """Shelf-pack every face that owns its UVs; 1 px gutter. Returns sheet height in pixels."""
    rects = []
    for el in root.walk():
        if el.uv_from: continue
        for f in el.faces:
            w, h = el.face_size(f)
            rects.append((el, f, max(1, math.ceil(w * PX)), max(1, math.ceil(h * PX)), w, h))
    rects.sort(key=lambda r: (-r[3], -r[2], r[0].name, r[1]))
    x = y = shelf = 0
    for el, f, pw, ph, w, h in rects:
        if x + pw > SHEET_W: x, y, shelf = 0, y + shelf + 1, 0
        el.uv[f] = [x / PX, y / PX, x / PX + w, y / PX + h]
        el.__dict__.setdefault("px", {})[f] = (x, y, pw, ph)
        x += pw + 1
        shelf = max(shelf, ph)
    height = y + shelf
    return math.ceil(height / 16) * 16       # the atlas takes any size; keep whole model units


def paint(root, height):
    img = Image.new("RGBA", (SHEET_W, height), (0, 0, 0, 0))
    px = img.load()
    for el in root.walk():
        if el.uv_from: continue
        for f in el.faces:
            x0, y0, pw, ph = el.px[f]
            for j in range(ph):
                for i in range(pw):
                    fu, fv = (i + 0.5) / pw, (j + 0.5) / ph
                    p = el.point(f, fu, fv)
                    c = el.mat(*p, f, fu, fv)
                    if c is None: continue
                    # stable per-pixel noise from the model position, so neighbouring faces agree
                    h = hashlib.md5(("%.2f,%.2f,%.2f" % p).encode()).digest()[0] / 255.0
                    k = 0.94 + 0.12 * h
                    if el.shade_edges and el.faces == FACES:
                        e = min(fu, 1 - fu, fv, 1 - fv)
                        k *= 0.86 + 0.14 * min(1.0, e * 4)          # darker towards each face's edges, like the hawk
                    px[x0 + i, y0 + j] = mul(c, k) + (255,)
    return img


def uv_guide(root, img):
    s = 8
    big = img.resize((img.width * s, img.height * s), Image.NEAREST)
    d = ImageDraw.Draw(big)
    for el in root.walk():
        if el.uv_from: continue
        for f in el.faces:
            x, y, pw, ph = el.px[f]
            d.rectangle([x * s, y * s, (x + pw) * s - 1, (y + ph) * s - 1], outline=(255, 0, 255))
            d.text((x * s + 2, y * s + 1), f"{el.name}.{f[0]}", fill=(255, 0, 255))
    return big


# ---------------------------------------------------------------- animation
def key(frame, arm, hand, extra=None):
    """Symmetric wing pose: positive = raised. Negative rotationX raises the +Z (left) wing."""
    els = {"ArmL": {"rotationX": -arm}, "ArmR": {"rotationX": arm},
           "WingL": {"rotationX": -hand}, "WingR": {"rotationX": hand}}
    if extra: els.update(extra)
    return {"frame": frame, "elements": els}


# ---------------------------------------------------------------- ground poses (0.5.1; not used by the mod yet)
# Wings fold by rotating the existing planes: the arm swings back along the body (rotationY) and droops a little
# onto the flank (rotationX); the hand folds back over the arm. No new elements, so the flight shape is unchanged.
FOLDED = {"ArmL": {"rotationX": -10, "rotationY": 95}, "ArmR": {"rotationX": 10, "rotationY": -95},
          "WingL": {"rotationY": 170}, "WingR": {"rotationY": -170}}


def ground_key(frame, **parts):
    """A ground pose keyframe: folded wings plus the given element rotations, e.g. Neck=(0, 0, -75)."""
    els = {k: dict(v) for k, v in FOLDED.items()}
    for name, (rx, ry, rz) in parts.items():
        els[name] = {"rotationX": rx, "rotationY": ry, "rotationZ": rz}
    return {"frame": frame, "elements": els}


def animations():
    # The hand lags the arm: it trails low on the upstroke and high on the downstroke, so the beat reads as a whip.
    flap = [
        key(0, 0, -16, {"Body": {"rotationZ": 1}}),    # rising through level, hand trailing low
        key(8, 36, 6, {"Body": {"rotationZ": 0}}),     # top of the stroke
        key(16, 0, 18, {"Body": {"rotationZ": -1}}),   # driving down through level, hand trailing high
        key(24, -30, -8, {"Body": {"rotationZ": 0}}),  # bottom of the stroke
    ]
    soar = [key(0, 6, 2), key(15, 3, 4)]               # slight dihedral, gentle rocking
    return [
        {"name": "fly-flap", "code": "fly-flap", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": flap},
        {"name": "fly-soar", "code": "fly-soar", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": soar},
    ] + ground_animations()


def ground_animations():
    """Swimming: wings folded, neck upright, legs left tucked so the feet stay under the waterline; a slow bob.
    Standing: the same upper body with the legs swung straight down and the feet forward. Walking: a waddle."""
    def swim(frame, bob=0.0):
        return ground_key(frame, Neck=(0, 0, -70 + bob), Head=(0, 0, 68 - bob), Body=(0, 0, bob * 0.5))
    def stand(frame, neck=0.0):
        return ground_key(frame, Neck=(0, 0, -80 + neck), Head=(0, 0, 75 - neck),
                          LegL=(0, 0, -90), LegR=(0, 0, -90), FootL=(0, 0, -90), FootR=(0, 0, -90))
    def walk(frame, leg_l, leg_r, neck):
        """Waddle: legs swing alternately about the hip, a little head bob in step."""
        return ground_key(frame, Neck=(0, 0, -80 + neck), Head=(0, 0, 75 - neck), Body=(-neck, 0, 0),
                          LegL=(0, 0, -90 + leg_l), LegR=(0, 0, -90 + leg_r), FootL=(0, 0, -90), FootR=(0, 0, -90))
    return [
        {"name": "swim", "code": "swim", "quantityframes": 40, "onAnimationEnd": "Repeat", "keyframes": [swim(0), swim(20, 3)]},
        {"name": "idle-stand", "code": "idle-stand", "quantityframes": 40, "onAnimationEnd": "Repeat", "keyframes": [stand(0), stand(20, 2)]},
        {"name": "walk", "code": "walk", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": [
            walk(0, 22, -22, 0), walk(8, 0, 0, 3), walk(15, -22, 22, 0), walk(23, 0, 0, 3)]},
    ]


def main():
    repaint = "--texture" in sys.argv
    shapes = os.path.join(ROOT, "shapes", "entity", "bird")
    textures = os.path.join(ROOT, "textures", "entity", "bird")
    os.makedirs(shapes, exist_ok=True)
    os.makedirs(textures, exist_ok=True)
    tex_path = os.path.join(textures, TEX + ".png")

    root = shape()
    height = pack(root)
    data = {
        "textureWidth": SHEET_W // PX, "textureHeight": height // PX,
        "textures": {TEX: "flocks:entity/bird/" + TEX},
        "elements": [root.json()],
        "animations": animations(),
    }
    with open(os.path.join(shapes, TEX + ".json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=1)

    img = paint(root, height)
    # LegL/LegR (0.5.1) borrow the feet's UVs; leaving them out keeps the stamp equal to the painted texture's layout
    layout = hashlib.md5(json.dumps({e.name: e.uv for e in root.walk() if e.name not in ("LegL", "LegR")}, sort_keys=True).encode()).hexdigest()
    stamp = os.path.join(HERE, "greylag_uv_layout.txt")
    old = open(stamp).read().strip() if os.path.exists(stamp) else None
    if repaint or not os.path.exists(tex_path):
        img.save(tex_path)
        print("painted", os.path.normpath(tex_path), img.size)
    else:
        print("kept existing texture", os.path.normpath(tex_path), "(pass --texture to repaint)")
        if old and old != layout:
            print("WARNING: the UV layout changed since the texture was painted; hand edits will be misaligned")
    with open(stamp, "w") as f: f.write(layout + "\n")
    uv_guide(root, img).save(os.path.join(HERE, "greylag_uv_guide.png"))
    print("wrote", os.path.normpath(os.path.join(shapes, TEX + ".json")))


if __name__ == "__main__":
    main()
