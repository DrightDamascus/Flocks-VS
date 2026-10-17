"""Generates the caged models and the Animal Cages mod patch.

Run from the repo root:  python tools/make_caged.py [--preview out.png]

The Animal Cages mod (modid animalcages) draws a caught animal with its model and no animation. Ours, unanimated, are
in their flight pose with the wings spread, so for each bird this writes its standing pose (the first frame of its own
idle-stand animation, wings folded, standing on its feet) baked into a static model:
  Flocks/assets/flocks/shapes/entity/bird/<texture>-caged.json
EntityBehaviorBird.OnEntityDespawn points the cage at it when a bird is caught. The mouse's model already stands, so it
needs none. Also writes
  Flocks/assets/flocks/patches/animalcages.json
which adds our animals to Animal Cages' default catch lists, with a display scale that fits each in the one-block cage:
the small cage takes the chicken-sized ones, the medium cage the big birds (once hurt below half health, as it does any
wild animal). Players who keep their own cage config (alwaysUseDefaultConfig false) add them there instead.
"""
import importlib, json, os, sys
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import make_greylag as g
import make_carcasses as mc
import preview_model as pv

ROOT = mc.ROOT
FIT = 13.0          # widest the animal may be in the cage, model units (a block is 16)

# entity code: (generator, entity size, cage)
ANIMALS = {
    "greylag": ("make_greylag", 0.98, "medium"),
    "heron": ("make_heron", 1.05, "medium"),
    "eagle": ("make_eagle", 1.1, "medium"),
    "mallard": ("make_mallard", 0.75, "small"),
    "gull": ("make_gull", 0.8, "small"),
    "crow": ("make_crow", 0.75, "small"),
    "owl": ("make_owl", 0.75, "small"),
    "mouse": ("make_mouse", 0.4, "small"),
}


def standing(mod):
    anims = getattr(mod, "animations", g.animations)()     # the gull and the mallard use the goose's
    anim = next(a for a in anims if a["code"] in ("idle-stand", "idle"))
    return anim["keyframes"][0]["elements"]


def scale_for(mod, root, size):
    """The entity's own size, or less if that would be wider than FIT across the ground."""
    pts = np.concatenate([c for c, _, _ in pv.quads(mod, root, {})])
    span = max(pts[:, 0].max() - pts[:, 0].min(), pts[:, 2].max() - pts[:, 2].min())
    return round(min(size, FIT / span), 2)


def main():
    built, entries = [], {"small": [], "medium": []}
    for code, (modname, size, cage) in ANIMALS.items():
        mod = mc.module(modname)
        if code == "mouse":
            root = mod.shape(); g.pack(root)            # stands as it is: the cage uses its own model
        else:
            root, height = mc.build(mod, standing(mod), two_sided=True)   # a cage is drawn as a block: front faces only
            mc.place(mod, root, 1.0, "ground")
            path = os.path.join(ROOT, "shapes", "entity", "bird", f"{mod.TEX}-caged.json")
            mc.write_json(path, mc.shape_json(mod, root, height, code))
            built.append((code, mod, root, mod.TEX))   # TEX now: make_greylag's is the shared one, renamed per bird
        entries[cage].append({"name": code, "scale": scale_for(mod, root, size)})
    dep = [{"modid": "animalcages"}]
    ops = [{"op": "add", "path": f"/{'smallCatchableEntities' if cage == 'small' else 'mediumCatchableEntities'}/-",
            "value": e, "file": "animalcages:config/defaultconfig.json", "dependsOn": dep}
           for cage, es in entries.items() for e in es]
    with open(os.path.join(ROOT, "patches", "animalcages.json"), "w", encoding="utf-8", newline="\n") as f:
        f.write("// The Animal Cages mod (modid animalcages), applied only when it is installed: our animals in its default catch\n"
                "// lists (small cage: the chicken-sized ones and the mouse; medium cage: the big birds, once hurt below half\n"
                "// health). Caught birds are drawn standing with folded wings (shapes/entity/bird/*-caged). Written by\n"
                "// tools/make_caged.py.\n" + json.dumps(ops, indent="\t") + "\n")
    print("wrote", len(built), "caged models and the cage patch:", {k: [(e["name"], e["scale"]) for e in v] for k, v in entries.items()})
    if "--preview" in sys.argv:
        from PIL import Image, ImageDraw
        out = sys.argv[sys.argv.index("--preview") + 1]
        cell = pv.CELL
        sheet = Image.new("RGB", (cell * 2, cell * len(built)), pv.SKY)
        dr = ImageDraw.Draw(sheet)
        for i, (code, mod, root, texname) in enumerate(built):
            tp = os.path.join(ROOT, "textures", "entity", "bird", texname + ".png")
            if not os.path.exists(tp): tp = tp[:-4] + "-drake.png"          # the mallard's is per sex
            tex = np.array(Image.open(tp).convert("RGBA"), float)
            qs2, tex2 = pv.with_plane(pv.quads(mod, root, {}), tex, -0.02, np.array([8.0, 0, 8.0]), 30)
            for j, view in enumerate(((0, 6), (-40, 18))):
                sheet.paste(pv.render(qs2, tex2, view, np.array([8.0, 8.0, 8.0]), 34), (j * cell, i * cell))
            dr.text((4, i * cell + 4), f"{code} caged", fill=(20, 20, 40))
        sheet.save(out)
        print("wrote", out)


if __name__ == "__main__":
    main()
