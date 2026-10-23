"""Generates the Butchering mod carcasses for Flocks' big birds (goose, heron, eagle, gull).

Run from the repo root:  python tools/make_carcasses.py [--preview out.png]

Writes:
  Flocks/assets/flocks/itemtypes/butchering/deadbird.json  the carcass item (disabled; patches/butchering.json enables it
                                                           when Butchering is installed: its item class only exists then)
  Flocks/assets/flocks/shapes/item/carcass/<bird>-dead.json, <bird>-hanging.json
                                                           the bird's own model, lying dead and hanging head down by the
                                                           feet from Butchering's hook, at its in-game size
  Flocks/assets/flocks/shapes/item/carcass/<bird>-plucked-dead.json, <bird>-plucked-hanging.json
                                                           the same once plucked: no feathers left (the wing and tail
                                                           feather planes, scapulars, hackles, plumes and crest gone), the
                                                           wings bare limbs
  Flocks/assets/flocks/textures/item/carcass/<bird>-plucked.png
                                                           the bird's texture repainted as bare skin, for when it is plucked
  Flocks/assets/flocks/patches/butchering.json             enables the item and makes the four birds butcherable

Butchering (modid butchering) turns a dead animal with the "butcherable" behaviour into a carcass item when picked up
with an empty hand; the carcass carries the animal's own drops (our poultry and feathers) and its weight, and
Butchering scales those and adds its extras (blood, offal) on the hook and table. Like Butchering's own chicken, the
carcass is the animal's own model posed (Dright: its generic stand-in made an eagle look like a leg): feathered while
dead and bled out, plucked once "skinned" (its own plucked model, bare skin, no feathers), the game's rot texture once
rotten. Sizes follow Butchering's
own animals: chickens, hares and foxes are "small" with mass 0.01, its duck 0.02. The mallard, crow, owl and mouse keep
vanilla's quick field harvest (Dright).
"""
import copy, importlib, json, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import make_greylag as g
import preview_model as pv

# make_greylag's own texture name: module() sets g.TEX for each bird, and g is make_greylag itself, so its TEX would
# otherwise be whichever bird came last (the goose's carcass item once pointed at "gull" and never showed plucked)
GREYLAG_TEX = g.TEX

ROOT = os.path.join(HERE, "..", "Flocks", "assets", "flocks")

# code: (generator module, entity size (client.size), mass)
BIRDS = {
    "greylag": ("make_greylag", 0.98, 0.02),
    "heron": ("make_heron", 1.05, 0.01),
    "eagle": ("make_eagle", 1.1, 0.02),
    "gull": ("make_gull", 0.8, 0.01),
}
# parts left off a carcass: the heron's long legs (and feet) would hang a block above the hook (Dright: no legs)
DROP = {"heron": ("LegL", "LegR")}
ROT = "game:block/creature/rot/rot"
SKIN, SKIN_DK, SKIN_LT = (226, 188, 160), (204, 160, 134), (240, 210, 186)

# Hanging from the hook: head down, the feet at the top (y HOOK_Y), centred on the block like Butchering's chicken.
HOOK_Y = 10.0
# the hanging pose: body nose straight down (rotationZ +90: negative is nose up), wings folded, legs and feet up
HANGING = {**{k: dict(v) for k, v in g.FOLDED.items()}, "Body": {"rotationZ": 90}}


def module(name):
    mod = importlib.import_module(name)
    g.TEX = GREYLAG_TEX if mod is g else mod.TEX   # El.json names faces' texture "#" + make_greylag.TEX
    return mod


# What a plucked bird has lost: every feather plane (the tail, the scapulars, the eagle's hackles and feathered leg
# plumes) and the heron's crest. The elements stay, without faces, so the pose still turns what hangs off them.
FEATHERS = ("Tail", "Scapulars", "Hackles", "LegPlume", "Crest")
# The feathered wing (feather planes and their leading-edge bars) goes whole: folded, it stands off the body by the
# feathers' width. In its place each flank gets a bare folded wing lying against it, an upper arm and a forearm, sized
# from the body box (fractions of its length along x and height in y; thickness out from the side).
WING = ("ArmL", "ArmR")
BARE_ARM = dict(x=(0.08, 0.5), y=(0.45, 0.85), t=0.28)
BARE_FOREARM = dict(x=(0.4, 0.85), y=(0.3, 0.62), t=0.2)


def pluck(root):
    """The feathers off (after packing: the new wing pieces borrow the body's texture rectangle, plain skin)."""
    body = next(el for el in root.walk() if el.name == "Body")
    for el in root.walk():
        if el.name.startswith(FEATHERS): el.faces = ()
        if el.name in WING:
            for e in el.walk(): e.faces = ()
    (x0, y0, z0), (x1, y1, z1) = body.frm, body.to
    lx, hy = x1 - x0, y1 - y0
    for side, sign in (("L", 1), ("R", -1)):
        for name, d in (("BareArm", BARE_ARM), ("BareForearm", BARE_FOREARM)):
            t = d["t"] * hy
            za, zb = (z1 - 0.1, z1 - 0.1 + t) if sign > 0 else (z0 + 0.1 - t, z0 + 0.1)
            body.add(g.El(name + side, [x0 + d["x"][0] * lx, y0 + d["y"][0] * hy, za], [x0 + d["x"][1] * lx, y0 + d["y"][1] * hy, zb],
                          body.mat, uv_from=body))


def death_pose(mod):
    """The last keyframe of the bird's death animation: lying on its side, wings folded."""
    anim = next(a for a in getattr(mod, "animations", g.animations)() if a["code"] == "death")   # the gull uses the goose's
    return anim["keyframes"][-1]["elements"]


def build(mod, pose, drop=(), two_sided=False, plucked=False):
    """The bird's model with the pose baked into its elements' rotations (an animation's rotations add to them), less
    the dropped parts (after packing, so the texture layout stays the entity's). two_sided: the feather planes (wings,
    tail: a top face only) get a bottom face too, its texture mirrored so the cut-outs match. The game draws entities
    from both sides, but blocks and items only from the front, so in a cage the wings vanished from below.
    plucked: the feathers off (pluck), after packing, so the plucked texture's layout still fits."""
    root = mod.shape()
    height = g.pack(root)
    if plucked:
        for el in root.walk(): el.frm, el.to = list(el.frm), list(el.to)
        pluck(root)
    if two_sided:
        for el in root.walk():
            if tuple(el.faces) != ("up",): continue
            el.faces = ("up", "down")
            src = el.uv_from or el
            u0, v0, u1, v1 = src.uv["up"]
            src.uv["down"] = [u1, v0, u0, v1]       # the down face runs along x the other way (El.point)
            src.px["down"] = src.px["up"]           # (for preview_model, which reads the pixel rectangle)
    for el in list(root.walk()):
        el.children = [c for c in el.children if c.name not in drop]
    for el in root.walk():
        p = pose.get(el.name)
        if not p: continue
        el.rot = tuple(r + p.get("rotation" + a, 0) for r, a in zip(el.rot, "XYZ"))
        st = [p.get("stretch" + a, 1) for a in "XYZ"]
        if any(v != 1 for v in st):     # a stretch (the heron's folded arm) scales the element and its children about
            o = list(el.origin)         # its origin; baked into their geometry (close enough for children turned too)
            sc = lambda v: [oo + (vv - oo) * k for vv, oo, k in zip(v, o, st)]
            for e in el.walk():
                e.frm, e.to, e.origin = sc(e.frm), sc(e.to), sc(e.origin)
        off = [p.get("offset" + a, 0) for a in "XYZ"]
        if any(off):                    # an offset moves the element and everything hanging off it (the ground stance's lift)
            for e in el.walk():
                e.frm = [v + o for v, o in zip(e.frm, off)]; e.to = [v + o for v, o in zip(e.to, off)]
                e.origin = [v + o for v, o in zip(e.origin, off)]
    return root, height


def place(mod, root, size, anchor):
    """Scales the posed model to the bird's in-game size and moves it: anchor "ground" puts its lowest point on y=0,
    centred on the block; "hook" puts its highest point (the feet) at HOOK_Y."""
    pts = np.concatenate([c for c, _, _ in pv.quads(mod, root, {})])
    lo, hi = pts.min(0), pts.max(0)
    cx, cz = (lo[0] + hi[0]) / 2, (lo[2] + hi[2]) / 2
    ref_y = lo[1] if anchor == "ground" else hi[1]
    target_y = 0.0 if anchor == "ground" else HOOK_Y

    def tf(v):
        return [round((v[0] - cx) * size + 8, 3), round((v[1] - ref_y) * size + target_y, 3), round((v[2] - cz) * size + 8, 3)]
    for el in root.walk():
        el.frm, el.to, el.origin = tf(el.frm), tf(el.to), tf(el.origin)
    return root


def shape_json(mod, root, height, code):
    return {"textureWidth": g.SHEET_W // g.PX, "textureHeight": height // g.PX,
            "textures": {mod.TEX: f"flocks:entity/bird/{mod.TEX}"}, "elements": [root.json()]}


def plucked(mod):
    """The bird's texture repainted as bare skin with goose bumps, keeping the bill, legs and feet (and the cut-out
    outline of every feather plane)."""
    root = mod.shape()
    height = g.pack(root)
    keep = ("Bill", "Leg", "Foot", "Feet", "Toe", "Claw", "Nail")
    for el in root.walk():
        if any(k in el.name for k in keep): continue
        orig = el.mat

        def skin(x, y, z, face, fu, fv, orig=orig):
            if orig(x, y, z, face, fu, fv) is None: return None
            bump = g.frac(x * 1.7 + z * 2.3) < 0.12 and g.frac(y * 1.9 - x * 0.7) < 0.3
            return SKIN_DK if bump else (SKIN_LT if face == "up" else SKIN)
        el.mat = skin
    return g.paint(root, height)


def item():
    textures, attrs, shapes = {}, {}, {}
    for code, (modname, size, mass) in BIRDS.items():
        tex = module(modname).TEX
        textures[f"deadbird-{code}-1-dead"] = {tex: {"base": f"flocks:entity/bird/{tex}"}}
        textures[f"deadbird-{code}-1-bledout"] = {tex: {"base": f"flocks:entity/bird/{tex}"}}   # bled, not yet plucked
        textures[f"deadbird-{code}-1-skinned"] = {tex: {"base": f"flocks:item/carcass/{code}-plucked"}}
        textures[f"deadbird-{code}-1-rotten"] = {tex: {"base": ROT}}
        # the first match wins: plucked first
        shapes[f"deadbird-{code}-1-skinned"] = {"base": f"flocks:item/carcass/{code}-plucked-dead"}
        shapes[f"deadbird-{code}-1-*"] = {"base": f"flocks:item/carcass/{code}-dead"}
        common = {
            "size": 0.5, "mass": mass,
            "bloodType": "butchering:bloodportion",
            "butcheringWorkLoad": "small",
            "handbookByType": {"*-rotten": {"exclude": False}, "*": {"exclude": True}},
            "hangingShape": {"base": f"flocks:item/carcass/{code}-hanging"},
            "excludeRewards": [],
            "butcheringRewards": [],      # from the bird's own drops (poultry, feathers), scaled by Butchering
            "skinningRewards": [],
        }
        attrs[f"deadbird-{code}-1-skinned"] = {**common, "hangingShape": {"base": f"flocks:item/carcass/{code}-plucked-hanging"}}
        attrs[f"deadbird-{code}-1-*"] = common
    return {
        "enabled": False,
        "code": "deadbird",
        "class": "butcherable",
        "variantgroups": [
            {"code": "type", "states": list(BIRDS)},
            {"code": "texture", "states": ["1"]},
            {"code": "state", "states": ["dead", "skinned", "bledout", "rotten"]},
        ],
        "maxstacksize": 1,
        "behaviors": [],
        "texturesByType": textures,
        "attributesByType": attrs,
        "shapeByType": shapes,
        "storageFlagsbytype": {"*-rotten": 1, "*": 32769},
        "transitionablePropsByType": {
            "*-rotten": [],
            "*": [{"type": "perish", "freshHours": {"avg": 80}, "transitionHours": {"avg": 24},
                   "transitionedStack": {"type": "item", "code": "flocks:deadbird-{type}-1-rotten"}, "transitionRatio": 1}],
        },
        "creativeinventory": {"general": ["*"], "items": ["*"]},
        "heldTpIdleAnimation": "holdunderarm",
        # the models are about a block long: smaller in the slot and in the hands (tunable in game with .tfedit)
        "guiTransform": {"translation": {"x": 0, "y": 0, "z": 0}, "rotation": {"x": -30, "y": 135, "z": 0},
                         "origin": {"x": 0.5, "y": 0.2, "z": 0.5}, "scale": 0.7, "rotate": True},
        "fpHandTransform": {"translation": {"x": 0, "y": 0, "z": 0.3}, "rotation": {"x": 0, "y": 90, "z": 0},
                            "origin": {"x": 0.5, "y": 0.2, "z": 0.5}, "scale": 0.6},
        "tpHandTransform": {"translation": {"x": -0.6, "y": -0.2, "z": -0.5}, "rotation": {"x": 0, "y": 90, "z": -20},
                            "origin": {"x": 0.5, "y": 0.2, "z": 0.5}, "scale": 0.5},
        "groundTransform": {"translation": {"x": 0, "y": 0, "z": 0}, "rotation": {"x": 0, "y": 0, "z": 0},
                            "origin": {"x": 0.5, "y": 0, "z": 0.5}, "scale": 1},
    }


def patches():
    dep = [{"modid": "butchering"}]
    ops = [{"op": "replace", "path": "/enabled", "value": True, "file": "flocks:itemtypes/butchering/deadbird.json", "dependsOn": dep}]
    for code in BIRDS:
        for side in ("server", "client"):
            ops.append({"op": "add", "path": f"/{side}/behaviors/-",
                        "value": {"code": "butcherable", "item": f"flocks:deadbird-{code}-1-dead"},
                        "file": f"flocks:entities/{code}.json", "dependsOn": dep})
    return ops


def write_json(path, data, note=None):
    body = json.dumps(data, indent="\t")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        # a comment goes inside the braces: a JSON patch can't reach a file whose first token is a comment
        f.write("{\n" + note + body[2:] + "\n" if note else body + "\n")


def main():
    for sub in (("itemtypes", "butchering"), ("textures", "item", "carcass"), ("shapes", "item", "carcass")):
        os.makedirs(os.path.join(ROOT, *sub), exist_ok=True)
    built = []
    for code, (modname, size, _) in BIRDS.items():
        mod = module(modname)
        for kind, pose, anchor, bare in (("dead", death_pose(mod), "ground", False), ("hanging", HANGING, "hook", False),
                                         ("plucked-dead", death_pose(mod), "ground", True), ("plucked-hanging", HANGING, "hook", True)):
            root, height = build(mod, pose, DROP.get(code, ()), plucked=bare)
            place(mod, root, size, anchor)
            write_json(os.path.join(ROOT, "shapes", "item", "carcass", f"{code}-{kind}.json"), shape_json(mod, root, height, code))
            built.append((code, kind, mod, root))
        plucked(mod).save(os.path.join(ROOT, "textures", "item", "carcass", f"{code}-plucked.png"))
    old = os.path.join(ROOT, "textures", "item", "carcass")
    for f in os.listdir(old):
        if f.endswith("-feathers.png"): os.remove(os.path.join(old, f))     # the earlier generic-shape textures
    write_json(os.path.join(ROOT, "itemtypes", "butchering", "deadbird.json"), item(),
               "\t// Butchering mod carcasses for the big birds: written by tools/make_carcasses.py (edit that, not this). Disabled\n"
               "\t// here; patches/butchering.json enables it when Butchering is installed (its item class only exists then).\n")
    with open(os.path.join(ROOT, "patches", "butchering.json"), "w", encoding="utf-8", newline="\n") as f:
        f.write("// The Butchering mod (modid butchering), applied only when it is installed: the goose, heron, eagle and gull\n"
                "// become carcasses to carry home and butcher (itemtypes/butchering/deadbird.json); the smaller birds keep the\n"
                "// quick field harvest. Written by tools/make_carcasses.py.\n" + json.dumps(patches(), indent="\t") + "\n")
    print("wrote", len(built), "carcass shapes,", len(BIRDS), "plucked textures, the item and the patch")
    if "--preview" in sys.argv:
        preview(built, sys.argv[sys.argv.index("--preview") + 1])


def preview(built, out):
    """Each shape from the side and in 3/4, on a ground plane; hanging ones with a mark at the hook height."""
    from PIL import Image, ImageDraw
    cell = pv.CELL
    sheet = Image.new("RGB", (cell * 2, cell * len(built)), pv.SKY)
    dr = ImageDraw.Draw(sheet)
    for i, (code, kind, mod, root) in enumerate(built):
        module(mod.__name__)
        path = ("textures", "item", "carcass", f"{code}-plucked.png") if kind.startswith("plucked") else ("textures", "entity", "bird", mod.TEX + ".png")
        tex = np.array(Image.open(os.path.join(ROOT, *path)).convert("RGBA"), float)
        qs = pv.quads(mod, root, {})
        y0 = 0.0 if kind.endswith("dead") else -16.0
        qs2, tex2 = pv.with_plane(qs, tex, y0 - 0.02, np.array([8.0, 0, 8.0]), 30)
        centre = np.array([8.0, 4.0 if kind.endswith("dead") else -2.0, 8.0])
        for j, view in enumerate(((0, 6), (-40, 18))):
            sheet.paste(pv.render(qs2, tex2, view, centre, 40), (j * cell, i * cell))
        dr.text((4, i * cell + 4), f"{code} {kind}" + (f" (hook at y {HOOK_Y:.0f}, block 0-16)" if kind.endswith("hanging") else ""), fill=(20, 20, 40))
    sheet.save(out)
    print("wrote", out)


if __name__ == "__main__":
    main()
