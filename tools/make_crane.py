"""Generates the common crane (Grus grus) shape, texture and flight animations for Flocks.

Modeled on tools/make_greylag.py (same pipeline, conventions and texel density); run from the repo root:
  python tools/make_crane.py            shape + animations; texture only if it does not exist yet
  python tools/make_crane.py --texture  also (re)paint the texture, overwriting any hand edits

Writes Flocks/assets/flocks/shapes/entity/bird/crane.json, textures/entity/bird/crane.png, and
tools/crane_uv_guide.png (an 8x blow-up of the UV layout with labels).

Crane in flight: neck held straight out ahead, long legs trailing straight back past a short tail, long broad
wings with the primaries spread as fingers. Grey body, black-and-white head and neck stripe, small red crown,
black primaries and secondaries, bushy dark tertials drooping over the tail.
Wings, tail and tertials are zero-thickness planes (up face only) drawn double-sided, feather edges cut out.

Conventions (same as the goose and hawk): head to -X, wings along +-Z, up +Y, 16 units = 1 block before
Species.Scale. A negative rotationX raises the +Z (left) wing.
"""
import hashlib, json, math, os, random, sys
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..", "Flocks", "assets", "flocks")
TEX = "crane"
PX = 2            # texture pixels per model unit (same as the goose)
SHEET_W = 64      # texture width in pixels; height grows to fit

# ---------------------------------------------------------------- palette (common crane)
GREY_DK = (112, 118, 124)
GREY = (146, 152, 158)
GREY_LT = (172, 178, 183)
FRINGE = (190, 194, 197)
BLACK = (34, 33, 34)
BLACK_LT = (58, 57, 58)
SOOT = (48, 46, 46)
WHITE = (232, 232, 228)
RED = (196, 38, 34)
BILL = (156, 150, 128)
BILL_TIP = (196, 188, 160)
LEGS = (52, 50, 50)
EYE = (150, 40, 30)


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
    """Back: soft grey feathers with faintly paler fringes."""
    row = math.floor((x - 2.0) / 1.5)
    lx = frac((x - 2.0) / 1.5)
    lz = frac((z + row * 0.75) / 1.5)
    if lx > 0.72: return GREY_LT if 0.15 < lz < 0.85 else GREY
    if lx < 0.25 and 0.3 < lz < 0.7: return GREY_DK
    return GREY


def m_body(x, y, z, face, fu, fv):
    if face == "up": return mantle(x, z)
    if face == "down": return GREY if frac(x * 0.6 + z * 0.3) > 0.15 else GREY_DK
    if face == "west": return GREY_DK
    if face == "east": return mix(GREY_DK, SOOT, 0.4)
    if y > 5.4: return mantle(x, y + 4)
    return GREY if frac((x - 2.0) / 1.4) > 0.2 else GREY_DK


def m_rump(x, y, z, face, fu, fv):
    return mix(GREY_DK, SOOT, 0.3) if face != "down" else GREY_DK


def m_neck(x, y, z, face, fu, fv):
    """Black fore-neck, a white stripe running back from behind the eye down the side, grey at the base."""
    if x > 0.0: return GREY_DK if face != "up" else GREY
    if face == "up": return BLACK if x < -4.0 else mix(BLACK, GREY_DK, (x + 4.0) / 4.0)
    if face == "down": return BLACK
    if face in ("north", "south"):
        # white band along the upper side of the neck, fading out toward the body
        if y > 4.9 and x < -3.0: return WHITE
        return BLACK
    return BLACK


def m_head(x, y, z, face, fu, fv):
    if face == "up":
        return RED if -11.6 < x < -10.4 else BLACK         # small red crown patch
    if face in ("north", "south"):
        if -12.3 < x < -11.8 and 5.0 < y < 5.5: return EYE
        if x > -11.5 and y < 5.4: return WHITE              # white starts behind the eye
        if -11.6 < x < -10.4 and y > 5.6: return RED
        return BLACK
    return BLACK


def m_bill(x, y, z, face, fu, fv):
    if x < -15.4: return BILL_TIP
    return mul(BILL, 0.85) if face == "down" else BILL


def m_legs(x, y, z, face, fu, fv):
    return mul(LEGS, 0.8) if face == "down" else LEGS


def m_edge(x, y, z, face, fu, fv):
    return mix(GREY_DK, GREY, 0.3)


def m_tail(x, y, z, face, fu, fv):
    """Short square grey tail with a dark tip band; rounded feather tips cut out."""
    end = 15.0 - x
    lz = frac((z - 6.25) / 0.875)
    if end < 0.4 * (2 * lz - 1) ** 2: return None
    if end < 0.7: return SOOT
    return GREY if 0.15 < lz < 0.85 else GREY_DK


def m_tertials(x, y, z, face, fu, fv):
    """The crane's 'bustle': long curly dark tertials drooping over the tail, with ragged cut-out ends."""
    end = TERT_X[1] - x
    lz = frac(abs(z - 8.0) / 0.6)
    edge = abs(z - 8.0) / 2.2
    ragged = 0.9 * abs(math.sin(z * 5.1)) + 0.5 * edge
    if end < ragged: return None
    if edge > 0.92 and end < 2.0: return None
    if lz < 0.15: return mul(SOOT, 0.75)
    return SOOT if 0.4 < lz else BLACK_LT


def wing_coords(x, z, x0, x1, root, length):
    """c: 0 at the leading edge .. 1 at the trailing edge; s: 0 at the wing root .. 1 at the outer end."""
    return (x - x0) / (x1 - x0), (abs(z - 8.0) - root) / length


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: grey coverts in rows, then a broad band of black secondaries with a notched trailing edge."""
    c, s = wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    span_u = abs(z - 8.0)
    lz = frac(span_u / 1.5)
    if c > 0.95 and (lz < 0.18 or lz > 0.82): return None          # notches between secondary tips
    if c < 0.08: return GREY_DK
    if c < 0.5:
        band = frac((c - 0.08) / 0.105)
        lz2 = frac((span_u + math.floor((c - 0.08) / 0.105) * 0.75) / 1.5)
        if band > 0.72: return FRINGE if 0.15 < lz2 < 0.85 else GREY_LT
        return GREY if c > 0.2 else mix(GREY_DK, GREY, 0.6)
    if c < 0.56: return mix(GREY_DK, BLACK, 0.5)
    if s < 0.14:                                                     # tertial bases next to the body, sooty
        return SOOT if 0.15 < lz < 0.85 else BLACK
    return BLACK_LT if 0.12 < lz < 0.88 else BLACK


FINGERS = 6   # spread primaries at the wingtip


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: grey primary coverts, then black primaries; the outer third splits into spread fingers."""
    c, s = wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.12 * s ** 2
    trail = 1.0 - 0.3 * s ** 1.5
    if s > 0.995 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)          # 0..1 across the remaining chord
    if s > 0.62:
        # fingers: alternating feather / gap across the chord, each feather tapering to a rounded tip
        n = k * (FINGERS * 2 - 1)
        i = math.floor(n)
        if i % 2 == 1: return None
        tip = 0.62 + (1.0 - 0.62) * (1 - 0.35 * abs((i / 2) - (FINGERS - 1) * 0.6) / FINGERS)
        if s > tip: return None
        lf = frac(n)
        if s > tip - 0.05 and (lf < 0.2 or lf > 0.8): return None
        return BLACK_LT if 0.4 < lf < 0.6 else BLACK
    if k < 0.3 and s < 0.5:
        return GREY_LT if frac(k / 0.1) > 0.72 else GREY_DK
    n = k * 5.0
    lf = frac(n)
    if lf < 0.14: return mul(BLACK, 0.8)
    if 0.48 < lf < 0.58: return BLACK_LT
    return BLACK


# ---------------------------------------------------------------- geometry
ARM_X = (3.0, 11.8)       # broad chord of the inner wing (leading, trailing)
ARM_ROOT, ARM_LEN = 2.4, 10.5
HAND_X = (3.4, 11.2)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 11.0
WING_Y = 5.6
TERT_X = (10.0, 16.6)     # tertial bustle, drooping over and past the tail

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
    body = El("Body", [2.5, 2.6, 6.0], [12.0, 6.4, 10.0], m_body)
    rump = El("Rump", [12.0, 3.4, 6.6], [13.2, 5.8, 9.4], m_rump)
    # long neck held straight out ahead, slightly drooping; small head and long straight bill
    neck = El("Neck", [-9.5, 4.3, 7.3], [2.5, 5.7, 8.7], m_neck, origin=[2.5, 5.0, 8], rot=(0, 0, 3))
    head = El("Head", [-12.4, 4.2, 7.0], [-9.5, 6.0, 9.0], m_head, origin=[-9.5, 5.1, 8], rot=(0, 0, -2))
    bill = El("Bill", [-16.0, 4.6, 7.6], [-12.4, 5.3, 8.4], m_bill)
    tail = El("Tail", [12.0, 5.0, 6.25], [15.0, 5.0, 9.75], m_tail, faces=("up",), origin=[12.0, 5.0, 8], rot=(0, 0, 3))
    tert = El("Tertials", [TERT_X[0], 5.9, 5.8], [TERT_X[1], 5.9, 10.2], m_tertials, faces=("up",),
              origin=[TERT_X[0], 5.9, 8], rot=(0, 0, 8))
    # long legs trailing straight back well past the tail, toes together
    leg_l = El("LegL", [11.0, 3.2, 8.3], [23.0, 3.8, 8.9], m_legs, origin=[11.0, 3.5, 8.6], rot=(0, 0, -2))
    leg_r = El("LegR", [11.0, 3.2, 7.1], [23.0, 3.8, 7.7], m_legs, origin=[11.0, 3.5, 7.4], rot=(0, 0, -2))
    foot_l = El("FootL", [23.0, 3.1, 8.2], [25.2, 3.6, 9.0], m_legs)
    foot_r = El("FootR", [23.0, 3.1, 7.0], [25.2, 3.6, 7.8], m_legs)
    leg_l.add(foot_l); leg_r.add(foot_r)
    body.add(rump, neck.add(head.add(bill)), tail, tert, leg_l, leg_r, wing("L"), wing("R"))
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
    # Cranes beat slower and deeper than geese (Species.FlapSeconds sets the speed): a long, deep downstroke with the
    # hand lagging the arm, and a quick, high upstroke. Neck and legs ride steady; the body bobs a touch.
    flap = [
        key(0, 0, -20, {"Body": {"rotationZ": 1.5}}),    # rising through level, hand trailing low
        key(8, 44, 10, {"Body": {"rotationZ": 0}}),      # top of the stroke, high
        key(16, 2, 24, {"Body": {"rotationZ": -1.5}}),   # driving down through level, hand trailing high
        key(24, -40, -12, {"Body": {"rotationZ": 0}}),   # deep bottom of the stroke
    ]
    soar = [key(0, 4, -2), key(15, 2, 0)]                # flat glide, fingers drooping slightly
    return [
        {"name": "fly-flap", "code": "fly-flap", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": flap},
        {"name": "fly-soar", "code": "fly-soar", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": soar},
    ] + ground_animations()


def ground_animations():
    """Standing: wings folded, neck up in a shallow S (neck tipped up, head back level), legs straight down with the
    toes forward. Walking: legs swing alternately about the hip, with a small head bob in step."""
    def stand(frame, leg_l=0.0, leg_r=0.0, neck=0.0, body=0.0):
        return ground_key(frame, Neck=(0, 0, -72 + neck), Head=(0, 0, 58 - neck), Body=(0, 0, body),
                          LegL=(0, 0, -88 + leg_l), LegR=(0, 0, -88 + leg_r), FootL=(0, 0, -90), FootR=(0, 0, -90))
    idle = [stand(0), stand(20, neck=2)]
    walk = [stand(0, 18, -18, 0, 1), stand(8, 0, 0, 4, 0), stand(15, -18, 18, 0, 1), stand(23, 0, 0, 4, 0)]
    return [
        {"name": "idle-stand", "code": "idle-stand", "quantityframes": 40, "onAnimationEnd": "Repeat", "keyframes": idle},
        {"name": "walk", "code": "walk", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": walk},
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
    layout = hashlib.md5(json.dumps({e.name: e.uv for e in root.walk()}, sort_keys=True).encode()).hexdigest()
    stamp = os.path.join(HERE, "crane_uv_layout.txt")
    old = open(stamp).read().strip() if os.path.exists(stamp) else None
    if repaint or not os.path.exists(tex_path):
        img.save(tex_path)
        print("painted", os.path.normpath(tex_path), img.size)
    else:
        print("kept existing texture", os.path.normpath(tex_path), "(pass --texture to repaint)")
        if old and old != layout:
            print("WARNING: the UV layout changed since the texture was painted; hand edits will be misaligned")
    with open(stamp, "w") as f: f.write(layout + "\n")
    uv_guide(root, img).save(os.path.join(HERE, "crane_uv_guide.png"))
    print("wrote", os.path.normpath(os.path.join(shapes, TEX + ".json")))


if __name__ == "__main__":
    main()
