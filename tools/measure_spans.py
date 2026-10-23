"""Measures each bird's wingspan as drawn in flight, for the species files' "wingspan" (config/flocks-species/).

  python tools/measure_spans.py

The server spaces a passing flock's formation by it and has no meshes to measure (the client's BirdMeshes.Span is the
same measure: the widest the model gets across x or z over its fly-flap and fly-soar frames). Prints, per bird, the span
at size 1 and at the entity's client size (entities/<bird>.json), which is the number the species file wants. Run it
again after changing a model's wings or a bird's size.
"""
import importlib, os, re, sys
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import preview_model as pm

ENTITIES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Flocks", "assets", "flocks", "entities")
BIRDS = [("make_greylag", "greylag", "goose"), ("make_mallard", "mallard", "mallard"), ("make_heron", "heron", "heron"),
         ("make_gull", "gull", "gull"), ("make_crow", "crow", "crow"), ("make_eagle", "eagle", "eagle"), ("make_owl", "owl", "owl")]


def span(mod):
    mg = importlib.import_module(mod)
    root = mg.shape(); (mg.g.pack if hasattr(mg, "g") else mg.pack)(root)
    anims = {a["code"]: a for a in (mg.animations() if hasattr(mg, "animations") else mg.g.animations())}
    best = 0
    for name in ("fly-flap", "fly-soar"):
        a = anims.get(name)
        if a is None: continue
        for fr in range(a["quantityframes"]):
            pts = np.concatenate([c for c, _, _ in pm.quads(mg, root, pm.pose(a, fr))])
            best = max(best, pts[:, 0].max() - pts[:, 0].min(), pts[:, 2].max() - pts[:, 2].min())
    return best / 16


for mod, entity, code in BIRDS:
    with open(os.path.join(ENTITIES, entity + ".json"), encoding="utf-8") as fh:
        size = float(re.search(r"\bsize:\s*([0-9.]+)", fh.read()).group(1))
    s = span(mod)
    print(f"{code:8} span {s:.3f} at size 1; size {size} -> wingspan {s * size:.2f}")
