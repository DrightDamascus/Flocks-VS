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


# ---------------------------------------------------------------- texture toolkit (0.22.0, from the eagle's repaint)
# Vanilla paints its animals with soft, low-contrast clouds and a per-pixel speckle, and no hard geometric pattern
# (see the hawk and the hen). So every material here is a base colour broken up by value noise sampled in model space
# (so faces that meet agree), stretched along the way the feathers lie, plus grain.
def _h(i, j, k):
    n = (i * 374761393 + j * 668265263 + k * 1274126177) & 0xFFFFFFFF
    n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0


def vnoise(x, y, z):
    """Smooth value noise, 0..1, lattice spacing 1."""
    ix, iy, iz = math.floor(x), math.floor(y), math.floor(z)
    fx, fy, fz = x - ix, y - iy, z - iz
    sx, sy, sz = (t * t * (3 - 2 * t) for t in (fx, fy, fz))
    lerp = lambda a, b, t: a + (b - a) * t
    c = [[[_h(ix + a, iy + b, iz + d) for d in (0, 1)] for b in (0, 1)] for a in (0, 1)]
    return lerp(lerp(lerp(c[0][0][0], c[0][0][1], sz), lerp(c[0][1][0], c[0][1][1], sz), sy),
                lerp(lerp(c[1][0][0], c[1][0][1], sz), lerp(c[1][1][0], c[1][1][1], sz), sy), sx)


def cloud(x, y, z, s=1.6, stretch=(1, 1, 1)):
    """Two octaves of noise, 0..1; stretch > 1 along an axis draws streaks that way (feathers lying along it)."""
    x, y, z = x / (s * stretch[0]), y / (s * stretch[1]), z / (s * stretch[2])
    return 0.65 * vnoise(x, y, z) + 0.35 * vnoise(x * 2.3 + 17.1, y * 2.3 + 5.3, z * 2.3 + 11.7)


def grain(c, x, y, z, amt=0.05):
    return mul(c, 1 + amt * (2 * _h(round(x * 2) + 500, round(y * 2) + 500, round(z * 2) + 500) - 1))


def clamp01(v):
    return max(0.0, min(1.0, v))


def plumage(x, y, z, base, dark, light, s=1.6, stretch=(2.2, 0.8, 0.8), seed=0.0):
    """Base colour with soft darker and lighter patches, and grain."""
    n = cloud(x + seed, y, z, s, stretch)
    if n < 0.42: c = mix(base, dark, 0.7 * clamp01((0.42 - n) / 0.25))
    elif n > 0.58: c = mix(base, light, 0.7 * clamp01((n - 0.58) / 0.3))
    else: c = base
    return grain(c, x, y, z)


FEATHER = 1.8


def feathered(c, a, b, light, sa=1.3, sb=1.0, fringe=0.45, seed=0, shadow=0.74):
    """Lays visible feathers over a colour: staggered, slightly irregular rows of rounded feathers, each with its own
    tone, a faint shaft, a paler fringe at the tip and a soft shadow just past it where it overlaps the next row.
    a runs the way the feathers point (toward their tips), b across them; sa, sb are a feather's length and width,
    scaled by FEATHER (at 2 px per unit a feather needs to be 4-6 px to read as one, not as speckle)."""
    sa, sb, fringe = sa * FEATHER, sb * FEATHER, min(0.75, fringe * 1.3)
    j = math.floor(b / sb)
    u = a / sa + 0.5 * (j % 2) + 0.35 * _h(j, 7, seed) + (cloud(a, b, seed, 1.6) - 0.5) * 0.2
    i, la, lb = math.floor(u), frac(u), frac(b / sb)
    tone = 0.9 + 0.2 * _h(i, j, 99 + seed)
    tip = 1 - 0.32 * (2 * lb - 1) ** 2                                # the rounded tip of this feather
    if la > tip: return mul(c, shadow)                                # shadow under the next row's tip
    t = la / tip
    c = mul(c, tone)
    if abs(lb - 0.5) < 0.09 and t < 0.75: c = mul(c, 0.9)             # shaft
    if t > 0.74: c = mix(c, light, fringe * min(1.0, (t - 0.74) / 0.16))
    return c


def rough(t, x, y, z, amt=0.5, s=1.0):
    """A ragged edge for a blend: t (0..1) pushed about by noise, so colour boundaries feather out."""
    return clamp01(t + (cloud(x, y, z, s) - 0.5) * amt)


def lift(c, y, lo, hi):
    """Vanilla's baked light: a little darker low on the flank."""
    return mul(c, 0.82 + 0.22 * clamp01((y - lo) / (hi - lo)))


SHINE = (236, 212, 172)        # every eye's catch-light: a pale tan (Dright: light brown, not white)
PUPIL = (18, 15, 13)


def round_eye(du, dv, r, iris, ring=None, ring_w=0.15, pupil=0.0):
    """A round eye for a head painted at double density (El res=2: 0.25 units a pixel). du, dv: offset from the eye's
    centre in model units, du negative toward the bill. iris darkens toward its rim; pupil > 0 draws a pupil of that
    radius (dark-eyed birds leave it 0); ring: a bare-skin eye-ring colour around it. One pixel of shine on the
    upper front. None outside the eye."""
    d = math.hypot(du, dv)
    if d > r + (ring_w if ring else 0): return None
    if d > r: return ring
    o = 0.5 * r if pupil else 0.32 * r      # beside a pupil, not on it (a small pupil may be a single pixel)
    if abs(du + o) < 0.125 and abs(dv - o) < 0.125: return SHINE
    if d < pupil: return PUPIL
    return mix(mul(iris, 1.1), mul(iris, 0.6), clamp01((d / r - 0.3) / 0.7))


# ---------------------------------------------------------------- materials
# Each material maps a point on the bird (model units, rest pose) plus where it sits on its face (fu, fv in 0..1)
# to a colour, or None for a transparent (cut-out) pixel. Patterns use model coordinates, so they line up across faces.

def m_body(x, y, z, face, fu, fv):
    """Grey-brown back of pale-fringed feathers in rows; flanks barred by rows of dark feathers with pale edges;
    pale belly; white under the tail."""
    if face == "up": return feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), x, z, FRINGE, 1.5, 1.3, 0.7, shadow=0.82)
    if face == "down":
        under = feathered(plumage(x, y, z, BELLY, mix(BELLY, FLANK_DK, 0.3), WHITE, 1.4), x, z, WHITE, 0.8, 0.8, 0.3)
        return mix(under, grain(WHITE, x, y, z), rough((x - 10.2) / 0.8, x, y, z, 0.5))
    if face == "east": return grain(WHITE, x, y, z, 0.03)
    if face == "west": return plumage(x, y, z, mix(BELLY, BROWN_LT, 0.5), BROWN_LT, BELLY, 1.2)
    back = feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), x, y, FRINGE, 1.5, 1.3, 0.7, shadow=0.82)
    flank = feathered(plumage(x, y, z, FLANK_DK, BROWN_DK, FLANK_LT, 1.2), -y, x, FRINGE, 0.75, 0.9, 0.55)
    belly = plumage(x, y, z, BELLY, FLANK_LT, WHITE, 1.4)
    c = mix(belly, flank, rough((y - 2.8) / 0.6, x, y, z, 0.5))
    c = mix(c, back, rough((y - 5.4) / 0.5, x, y, z, 0.6))
    c = mix(c, grain(WHITE, x, y, z, 0.03), rough((x - 11.0) / 0.6, x, y, z, 0.5) * clamp01((5.0 - y) / 0.5))
    return lift(c, y, 2.2, 6.6)


def m_breast(x, y, z, face, fu, fv):
    if face == "up": return plumage(x, y, z, NECK, NECK_DK, BROWN_LT)
    if face == "down": return plumage(x, y, z, BELLY, FLANK_LT, WHITE, 1.2)
    return feathered(plumage(x, y, z, mix(BELLY, BROWN_LT, 0.45), BROWN_LT, BELLY, 1.0), -y, z + x, BELLY, 0.7, 0.7, 0.4)


def m_rump(x, y, z, face, fu, fv):
    if face == "up":
        return mix(plumage(x, y, z, GREY, GREY_DK, GREY_LT), grain(WHITE, x, y, z, 0.03), rough((x - 13.2) / 0.5, x, y, z, 0.5))
    return grain(WHITE, x, y, z, 0.03)


def m_neck(x, y, z, face, fu, fv):
    # greylag necks show fine furrows of feathers running along them
    c = plumage(x, y, z, NECK, NECK_DK, BROWN_LT, 1.2)
    across = z if face in ("up", "down") else y
    f = frac(across * 1.0 + (cloud(x, 0, across, 0.8) - 0.5) * 0.3)
    return mul(c, 0.84) if f < 0.3 else c


EYE_C = (-5.15, 5.55)
EYE_RING = (226, 128, 92)      # the greylag's orange-pink eye-ring


def m_head(x, y, z, face, fu, fv):
    if face in ("north", "south"):
        e = round_eye(x - EYE_C[0], y - EYE_C[1], 0.36, (40, 30, 24), EYE_RING, 0.13)
        if e: return e
    c = plumage(x, y, z, HEAD, mul(HEAD, 0.85), BROWN_LT, 0.8)
    if face == "up": c = mul(c, 0.92)
    if face in ("north", "south") and x < -5.9 and y < 5.4: c = mix(c, (220, 214, 200), 0.35)   # pale at the bill's base
    return c


def m_bill(x, y, z, face, fu, fv):
    if x < -8.25 or face == "west": return grain(NAIL, x, y, z, 0.03)      # the pale nail at the tip
    c = mix(BILL, mul(BILL, 0.85), clamp01((x + 6.4) / -2.0))
    if face == "up": c = mul(c, 1.08)
    elif face == "down": c = mul(c, 0.82)
    elif -7.3 < x < -6.9 and 5.0 < y < 5.2: c = mul(c, 0.6)               # nostril
    return grain(c, x, y, z, 0.03)


def m_feet(x, y, z, face, fu, fv):
    c = mul(FEET, 0.82) if face == "down" else FEET
    return grain(mul(c, 0.9) if frac(x * 2.5) < 0.2 else c, x, y, z, 0.04)


def m_edge(x, y, z, face, fu, fv):
    return plumage(x, y, z, mix(GREY_DK, BROWN, 0.4), BROWN_DK, GREY_DK, 1.0)


def m_tail(x, y, z, face, fu, fv):
    """Tail plane: grey-brown centre, white sides and tip, rounded feather tips cut out at the end."""
    end = 16.5 - x                       # distance from the tail tip
    lz = frac((z - 6.0) / 1.0)           # 1-unit-wide tail feathers
    edge = abs(z - 8.0) / 2.0            # 0 centre .. 1 outer edge
    if end < 0.55 * (2 * lz - 1) ** 2: return None        # rounded tips
    if end < 1.4 * edge ** 3 - 0.2: return None           # fan shape: outer feathers shorter
    c = plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT, 1.0, (2.0, 1, 0.6))
    white = max(rough((0.9 - end) / 0.4, x, y, z, 0.4), rough((edge - 0.75) / 0.15, x, y, z, 0.4))
    c = mix(c, grain(WHITE, x, y, z, 0.03), white)
    return mul(c, 0.86) if min(lz, 1 - lz) < 0.1 else c


def m_web(x, y, z, face, fu, fv):
    return grain(mul(FEET, 0.88 if face == "up" else 0.75), x, y, z, 0.05)


def m_claw(x, y, z, face, fu, fv):
    return grain((206, 196, 180), x, y, z, 0.04)


def m_scapulars(x, y, z, face, fu, fv):
    """Pale-fringed back feathers over the wing root, their rounded tips cut out at the rear."""
    end = SCAP_X[1] - x
    lz = frac(abs(z - 8.0) / 1.1)
    if end < 0.9 * (2 * lz - 1) ** 2: return None
    return feathered(plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT), x, abs(z - 8.0), FRINGE, 1.5, 1.3, 0.7, shadow=0.82)


def wing_coords(x, z, x0, x1, root, length):
    """c: 0 at the leading edge .. 1 at the trailing edge; s: 0 at the wing root .. 1 at the outer end."""
    return (x - x0) / (x1 - x0), (abs(z - 8.0) - root) / length


def m_arm(x, y, z, face, fu, fv):
    """Inner wing: pale grey coverts in rows of pale-edged feathers, then dark secondaries with pale tips and a notched
    edge; browner tertials by the body."""
    c, s = wing_coords(x, z, ARM_X[0], ARM_X[1], ARM_ROOT, ARM_LEN)
    span_u = abs(z - 8.0)
    lz = frac(span_u / 1.5)
    if c > 0.95 and (lz < 0.18 or lz > 0.82): return None          # notches between secondary tips
    coverts = feathered(plumage(x, y, z, GREY, GREY_DK, GREY_LT, 1.2), x, span_u, mix(GREY_LT, WHITE, 0.4), 1.0, 1.1, 0.6, shadow=0.84)
    coverts = mix(coverts, plumage(x, y, z, GREY_DK, BROWN, GREY, 1.0), clamp01((0.18 - c) / 0.1))
    sec = plumage(x, y, z, SECONDARY, PRIMARY, PRIMARY_LT, 1.0, (2.6, 1, 0.5))
    if s < 0.16: sec = plumage(x, y, z, BROWN, BROWN_DK, BROWN_LT, 1.0, (2.6, 1, 0.5))   # tertials are browner
    if min(lz, 1 - lz) < 0.12: sec = mul(sec, 0.8)
    elif abs(lz - 0.5) < 0.06 and c < 0.85: sec = mul(sec, 0.88)
    sec = mix(sec, FRINGE, 0.55 * rough((c - 0.86) / 0.06, x, y, z, 0.3))          # pale tips
    return mix(coverts, sec, rough((c - 0.53) / 0.1, x, y, z, 0.6))


def m_hand(x, y, z, face, fu, fv):
    """Outer wing: pale grey primary coverts, then dark primaries tapering to a pointed tip with separated ends."""
    c, s = wing_coords(x, z, HAND_X[0], HAND_X[1], HAND_ROOT, HAND_LEN)
    lead = 0.2 * s ** 2
    trail = 1.0 - 0.62 * s ** 1.5
    if s > 0.985 or c < lead or c > trail: return None
    k = (c - lead) / (trail - lead)          # 0..1 across the remaining chord
    n = (k - 0.3) / 0.7 * 5.0
    lf = frac(n)
    if k > 0.85 and s > 0.55 and lf < 0.22: return None              # separated feather ends near the tip
    coverts = feathered(plumage(x, y, z, GREY, GREY_DK, GREY_LT, 1.2), x, abs(z - 8.0), mix(GREY_LT, WHITE, 0.4), 0.8, 0.85, 0.55)
    prim = plumage(x, y, z, PRIMARY, mul(PRIMARY, 0.8), PRIMARY_LT, 1.0, (0.6, 1, 2.6))
    prim = mix(prim, GREY_DK, 0.4 * clamp01((0.5 - s) / 0.4))        # greyer toward the base
    if lf < 0.14: prim = mul(prim, 0.78)
    elif 0.48 < lf < 0.58: prim = mul(prim, 1.18)                    # pale shafts
    return mix(prim, coverts, rough((0.32 - k) / 0.08, x, y, z, 0.5))


# ---------------------------------------------------------------- geometry
ARM_X = (3.4, 10.6)       # chord of the inner wing (leading, trailing)
ARM_ROOT, ARM_LEN = 2.4, 9.0
HAND_X = (3.9, 10.1)
HAND_ROOT, HAND_LEN = ARM_ROOT + ARM_LEN, 9.0
WING_Y = 5.6

FACES = ("north", "east", "south", "west", "up", "down")


class El:
    def __init__(self, name, frm, to, mat, origin=None, rot=(0, 0, 0), faces=FACES, uv_from=None, shade_edges=True, res=1):
        self.name, self.frm, self.to, self.mat = name, list(frm), list(to), mat
        self.origin = origin or [(a + b) / 2 for a, b in zip(frm, to)]
        self.rot, self.faces, self.children = rot, faces, []
        self.uv_from = uv_from          # reuse another element's UVs (the hawk does this for its tilted under-planes)
        self.shade_edges = shade_edges
        self.res = res                  # texture density relative to PX: 2 paints this element's faces at twice the detail
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

# ---------------------------------------------------------------- detail builders (0.22.0, after the eagle's)
# Every bird's legs lie along +x at rest with the feet hanging off the end; standing, the leg turns 90 degrees down
# and the foot another 90 (FootL rotationZ -90), so in a foot's rest frame +x points forward and +y down. These
# builders work in that frame: toes reach +x, claws curl toward +y (into the ground when standing).
def perching_foot(side, x0, yc, zc, m_toe, m_claw, toes=(1.2, 1.4, 1.2), hind=0.9, t=0.4, w=0.36, spread=24, claw=0.5):
    """A foot like vanilla's hawk's: a pad at the leg's end (x0), three splayed front toes and a hind toe, each with
    its own claw. Returns the pad, named Foot<side> for the animations; its origin is its centre, as before."""
    pad = El("Foot" + side, [x0 - 0.2, yc - t / 2, zc - 0.55], [x0 + 0.6, yc + t / 2, zc + 0.55], m_toe)
    xb = x0 + 0.4
    for i, (dz, yaw, length) in enumerate(zip((-0.32, 0.0, 0.32), (spread, 0, -spread), toes)):
        z = zc + dz
        toe = El(f"Toe{side}{i + 1}", [xb, yc - t / 2, z - w / 2], [xb + length, yc + t / 2, z + w / 2], m_toe,
                 origin=[xb, yc, z], rot=(0, yaw, 0))
        tx = xb + length
        toe.add(El(f"Claw{side}{i + 1}", [tx - 0.1, yc - t / 2 + 0.06, z - w / 3], [tx + claw, yc + t / 2 - 0.06, z + w / 3],
                   m_claw, origin=[tx - 0.1, yc, z], rot=(0, 0, 50)))
        pad.add(toe)
    hx = x0 + 0.1
    back = El(f"Toe{side}Back", [hx - hind, yc - t / 2, zc - w / 2], [hx, yc + t / 2, zc + w / 2], m_toe, origin=[hx, yc, zc])
    back.add(El(f"Claw{side}Back", [hx - hind - claw, yc - t / 2 + 0.06, zc - w / 3], [hx - hind + 0.1, yc + t / 2 - 0.06, zc + w / 3],
                m_claw, origin=[hx - hind + 0.1, yc, zc], rot=(0, 0, -50)))
    return pad.add(back)


def webbed_foot(side, x0, yc, zc, m_toe, m_web, m_claw, length=1.8, spread=24, t=0.35, w=0.3, centre=0.9):
    """A waterbird's foot: three splayed toes joined by a web with a concave edge between the toe tips, small claws,
    and a stub of hind toe. The pad's origin sits `centre` past the leg's end (x0), where the old one-box foot's
    centre was, so the standing birds stand exactly as high as before."""
    xo = x0 + centre
    pad = El("Foot" + side, [xo - 0.4, yc - t / 2, zc - 0.4], [xo + 0.3, yc + t / 2, zc + 0.4], m_toe, origin=[xo, yc, zc])
    reach = length * math.sin(math.radians(spread)) + w

    def web(x, y, z, face, fu, fv):
        dx, dz = x - xo, z - zc
        r, ang = math.hypot(dx, dz), abs(math.degrees(math.atan2(dz, dx)))
        if dx <= 0 or ang > spread: return None
        if r > length * (0.98 - 0.22 * math.sin(math.pi * frac(ang / spread))): return None   # scalloped between toes
        return m_web(x, y, z, face, fu, fv)

    pad.add(El("FootWeb" + side, [xo, yc, zc - reach], [xo + length, yc, zc + reach], web, faces=("up",)))
    for i, (yaw, dz) in enumerate(((spread, -0.05), (0, 0.0), (-spread, 0.05))):
        z = zc + dz
        toe = El(f"Toe{side}{i + 1}", [xo, yc - t / 2, z - w / 2], [xo + length, yc + t / 2, z + w / 2], m_toe,
                 origin=[xo, yc, z], rot=(0, yaw, 0))
        tx = xo + length
        toe.add(El(f"Claw{side}{i + 1}", [tx - 0.05, yc - t / 2 + 0.05, z - w / 3], [tx + 0.3, yc + t / 2 - 0.05, z + w / 3], m_claw,
                   origin=[tx - 0.05, yc, z], rot=(0, 0, 35)))
        pad.add(toe)
    pad.add(El(f"Toe{side}Back", [xo - 0.75, yc - t / 2 + 0.05, zc - w / 3], [xo - 0.35, yc + t / 2 - 0.05, zc + w / 3], m_toe))
    return pad


def split_tail(tail, m_tail, fan=6, overlap=0.8, inset=0.2):
    """Vanilla's hawk tail: the tail plane in three, the outer two a little lower and fanned under the middle one.
    Takes the one-plane Tail and narrows it to the middle third; the side planes are its children, so the
    animations that bend Tail carry them."""
    (x0, y, z0), (x1, _, z1) = tail.frm, tail.to
    third = (z1 - z0) / 3
    tail.frm[2], tail.to[2] = z0 + third - overlap / 2, z1 - third + overlap / 2
    left = El("TailL", [x0, y - 0.1, z1 - third - overlap / 2], [x1 - inset, y - 0.1, z1], m_tail, faces=("up",),
              origin=[x0, y - 0.1, z1 - third], rot=(0, -fan, 0))
    right = El("TailR", [x0, y - 0.1, z0], [x1 - inset, y - 0.1, z0 + third + overlap / 2], m_tail, faces=("up",),
               origin=[x0, y - 0.1, z0 + third], rot=(0, fan, 0))
    return tail.add(left, right)


def scapulars(side, x0, x1, y, z_root, inner, outer, m_scap):
    """A feathered plane lying over the wing root and the back's edge (the eagle's scapulars), so the wing grows out
    of the back instead of meeting it at a seam. A child of the body, as real scapulars stay on the back: on the arm
    they swung out sideways when the wing folded. inner/outer: how far it reaches in over the back and out past
    the wing root."""
    a, b = (z_root - inner, z_root + outer) if side == "L" else (mz(z_root + outer), mz(z_root - inner))
    return El("Scapulars" + side, [x0, y, a], [x1, y, b], m_scap, faces=("up",),
              origin=[x0, y, z_root if side == "L" else mz(z_root)], rot=(0, 0, -3))


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


SCAP_X = (3.6, 9.8)


def shape():
    body = El("Body", [2.5, 2.2, 5.6], [12.5, 6.6, 10.4], m_body)
    breast = El("Breast", [1.2, 2.8, 6.1], [2.5, 6.1, 9.9], m_breast)
    belly = El("Belly", [3.5, 1.7, 6.2], [11.0, 2.2, 9.8], m_body)          # rounds the underside (0.22.0)
    rump = El("Rump", [12.5, 3.3, 6.3], [14.0, 6.0, 9.7], m_rump)
    neck = El("Neck", [-3.5, 4.0, 7.0], [1.5, 6.0, 9.0], m_neck, origin=[1.5, 5.0, 8], rot=(0, 0, -4))
    neck_base = El("NeckBase", [-0.2, 3.6, 6.6], [1.8, 6.3, 9.4], m_neck)   # the neck thickens into the breast
    head = El("Head", [-6.4, 3.9, 6.75], [-3.5, 6.4, 9.25], m_head, origin=[-3.5, 5.1, 8], rot=(0, 0, 6), res=2)
    bill = El("Bill", [-8.8, 4.3, 7.3], [-6.4, 5.3, 8.7], m_bill, res=2)
    bill_base = El("BillBase", [-6.9, 4.2, 7.1], [-6.3, 5.7, 8.9], m_head, res=2)   # the bill's deep base, feathered
    tail = split_tail(El("Tail", [12.5, 5.2, 6.0], [16.5, 5.2, 10.0], m_tail, faces=("up",), origin=[12.5, 5.2, 8], rot=(0, 0, 4)), m_tail)
    # Legs (0.5.1): in flight they lie tucked along the belly, hidden under the body and rump. 0.22.0: webbed feet
    # with toes and claws; the legs reach down to them (the one-box foot hung 0.7 below the leg's end).
    legs = []
    for side, zc in (("L", 8.7), ("R", 7.3)):
        leg = El("Leg" + side, [10.5, 3.6, zc - 0.25], [13.7, 4.1, zc + 0.25], m_feet, origin=[10.5, 3.85, zc])
        legs.append(leg.add(webbed_foot(side, 13.0, 3.85, zc, m_feet, m_web, m_claw)))
    scaps = [scapulars(s, SCAP_X[0], SCAP_X[1], 6.72, 8 + ARM_ROOT, 1.6, 0.7, m_scapulars) for s in "LR"]
    body.add(breast, belly, rump, neck.add(neck_base, head.add(bill, bill_base)), tail, *legs, *scaps, wing("L"), wing("R"))
    return body


# ---------------------------------------------------------------- UV packing and painting
def pack(root):
    """Shelf-pack every face that owns its UVs; 1 px gutter. Returns sheet height in pixels."""
    rects = []
    for el in root.walk():
        if el.uv_from: continue
        for f in el.faces:
            w, h = el.face_size(f)
            r = el.res
            rects.append((el, f, max(1, math.ceil(w * PX * r)), max(1, math.ceil(h * PX * r)), w * r, h * r))
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
# onto the flank (rotationX); the hand folds back over the arm. 0.22.0: drawn in (offsetZ) and down so the leading
# edges no longer stand out at the sides like a cape; the folded wings lie over the back and upper flanks.
FOLDED = {"ArmL": {"rotationX": 18, "rotationY": 95, "offsetZ": -1.6, "offsetY": -0.9},
          "ArmR": {"rotationX": -18, "rotationY": -95, "offsetZ": 1.6, "offsetY": -0.9},
          "WingL": {"rotationY": 170}, "WingR": {"rotationY": -170}}
# the fold before 0.22.0 (flat, leading edges standing out at the sides), for the birds not yet given their own
FOLDED_FLAT = {"ArmL": {"rotationX": -10, "rotationY": 95}, "ArmR": {"rotationX": 10, "rotationY": -95},
               "WingL": {"rotationY": 170}, "WingR": {"rotationY": -170}}


def ground_key_flat(frame, **parts):
    return ground_key(frame, FOLDED_FLAT, **parts)


def ground_key(frame, folded=None, **parts):
    """A ground pose keyframe: folded wings plus the given element rotations, e.g. Neck=(0, 0, -75)."""
    els = {k: dict(v) for k, v in (folded or FOLDED).items()}
    for name, (rx, ry, rz) in parts.items():
        els[name] = {"rotationX": rx, "rotationY": ry, "rotationZ": rz}
    return {"frame": frame, "elements": els}


AXES = {"offset": 0, "rotation": 0, "stretch": 1}


def complete_axes(anims):
    """The game casts every keyframe component of a set group (offset, rotation, stretch) straight to float, so an
    element that sets one axis must set all three or the client crashes the first time the animation plays.
    It also refuses (crashes on) a keyframe at or past quantityframes, so that is checked here too."""
    for anim in anims:
        bad = [kf["frame"] for kf in anim["keyframes"] if kf["frame"] >= anim["quantityframes"]]
        if bad:
            raise ValueError(f"animation {anim['code']}: keyframe(s) {bad} at or past quantityframes {anim['quantityframes']}"
                             " (the game crashes); loops close by themselves, so leave the last key off")
        for kf in anim["keyframes"]:
            for el in kf["elements"].values():
                for group, rest in AXES.items():
                    axes = [group + a for a in "XYZ"]
                    if any(a in el for a in axes):
                        for a in axes: el.setdefault(a, rest)
    return anims


def animations():
    # The hand lags the arm: it trails low on the upstroke and high on the downstroke, so the beat reads as a whip.
    flap = [
        # 0.21.2: deepened from +36/-30 (a player: they don't really flap, they just float)
        key(0, 0, -20, {"Body": {"rotationZ": 1}}),    # rising through level, hand trailing low
        key(8, 48, 8, {"Body": {"rotationZ": 0}}),     # top of the stroke
        key(16, 0, 24, {"Body": {"rotationZ": -1}}),   # driving down through level, hand trailing high
        key(24, -42, -10, {"Body": {"rotationZ": 0}}), # bottom of the stroke
    ]
    soar = [key(0, 6, 2), key(15, 3, 4)]               # slight dihedral, gentle rocking
    return [
        {"name": "fly-flap", "code": "fly-flap", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": flap},
        {"name": "fly-soar", "code": "fly-soar", "quantityframes": 30, "onAnimationEnd": "Repeat", "keyframes": soar},
    ] + ground_animations() + action_animations()


def action_animations():
    """Takeoff, landing flare and death (0.9.0). All rotations; Body pitch is rotationZ (negative = nose up)."""
    def takeoff(frame, arm, hand, legs):
        """A hurried, deep wingbeat with the body pitched up, neck stretched forward and the feet running on the water."""
        return key(frame, arm, hand, {
            "Body": {"rotationZ": -18}, "Neck": {"rotationZ": -12}, "Head": {"rotationZ": 10},
            "LegL": {"rotationZ": -60 + legs}, "LegR": {"rotationZ": -60 - legs},
            "FootL": {"rotationZ": -45}, "FootR": {"rotationZ": -45}})
    def flare(frame, arm, hand):
        """Landing: wings held high and swept forward, tips cupped down, body steeply nose-up, feet thrust forward."""
        k = key(frame, arm, hand, {
            "Body": {"rotationZ": -30}, "Neck": {"rotationZ": -28}, "Head": {"rotationZ": 26},
            "LegL": {"rotationZ": -125}, "LegR": {"rotationZ": -125}, "FootL": {"rotationZ": -70}, "FootR": {"rotationZ": -70}})
        k["elements"]["ArmL"]["rotationY"] = -14; k["elements"]["ArmR"]["rotationY"] = 14
        return k
    def death(frame, roll, wing):
        """Rolls onto its side; the upper wing loosens a little, neck limp, legs loose."""
        k = ground_key(frame, Body=(roll, 0, 0), Neck=(0, 0, -70 + roll), Head=(0, 0, 60 - roll * 0.4),
                       ArmR=(FOLDED["ArmR"]["rotationX"] + wing * 0.5, -95 + wing, 0), WingR=(0, -170 + wing * 0.9, 0),
                       LegL=(0, 0, -roll * 0.5), LegR=(0, 0, -roll * 0.3), FootL=(0, 0, -roll * 0.7), FootR=(0, 0, -roll * 0.7))
        k["elements"]["ArmR"].update({a: v for a, v in FOLDED["ArmR"].items() if a.startswith("offset")})   # tucked as folded
        return k
    return [
        {"name": "takeoff", "code": "takeoff", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [
            takeoff(0, 0, -22, 25), takeoff(5, 55, 10, 0), takeoff(10, 0, 26, -25), takeoff(15, -45, -10, 0)]},
        {"name": "land", "code": "land", "quantityframes": 20, "onAnimationEnd": "Repeat", "keyframes": [
            flare(0, 34, -16), flare(10, 26, -10)]},
        {"name": "death", "code": "death", "quantityframes": 24, "onActivityStopped": "PlayTillEnd", "onAnimationEnd": "Hold",
         "keyframes": [death(0, 0, 0), death(6, 35, 5), death(14, 82, 10), death(23, 82, 10)]},
    ]


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
        "animations": complete_axes(animations()),
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
