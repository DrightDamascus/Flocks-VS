# Changelog

## 0.2.2
- Robust bird mesh loading: tries hawk, then tree sparrow (its shape points at bird/sparrow/ but the texture ships
  in bird/passerine/, so that path is overridden), then chicken hen. Checks every texture exists before baking,
  catches bake exceptions, and logs which model loaded or why each one was skipped.
- Note: the "hawk shape not found" errors Dright saw were from 0.2.0 (wrong `survival` domain); 0.2.1 was not installed yet.

## 0.2.1
- Fix: `/flocks` "command does not exist". 0.2.0 registered it only as a client command, which VS runs with a dot
  (`.flocks`). Now `/flocks send [count] [species]` is a server command that tells your client (network channel
  `flocks`) to spawn the flock; `.flocks send ...` still works client-side. Mod now loads on both sides.
- Fix: hawk shape/texture not found. Vanilla `assets/survival` files live in the `game` domain, not `survival`;
  now `game:shapes/entity/animal/bird/raptor/hawk/hawk.json` and its `game:` texture.
- Meshes bake and the renderer registers only once per session.

## 0.2.0
- `/flocks send [count] [species]` (client command): sends a client-only, visual V of hawks (default 12, max 60)
  60-120 blocks above your ground level. Enters ~150 blocks out, crosses overhead with slight jitter, despawns ~170 past.
- Uses vanilla's unreleased hawk shape (game:shapes/entity/animal/bird/raptor/hawk/hawk.json) and its texture
  game:textures/entity/animal/bird/raptor/hawk/hawk-redtail-male1.png, referenced in place, not copied.
- fly-flap is baked into 10 static meshes and fly-soar into 1; birds alternate flap/soar. Drawn by one client renderer.

## 0.1.0
- Skeleton: client logs season, latitude and migration direction once per in-game day. DESIGN.md.


