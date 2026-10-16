# Flocks

Vintage Story 1.22.7 mod (modid `flocks`): migrating bird flocks that mark the seasons, and wild geese that land on
lakes, can be hunted, and come and go with the year. Install on both server and clients. Design notes and decisions:
DESIGN.md. Version history: CHANGELOG.md.

## What it does
- **Passing flocks** (drawn by each client, no entities): geese, cranes and hawks cross the sky on migration days in
  spring and autumn, at their own hours and heights, held back by storms. Geese and cranes call as they pass.
- **Landing flocks** (real entities): on spring migration days a flock of greylag geese flies in from up to 320 blocks
  out and glides down onto a lake near you. Geese also spawn naturally on lakes in their resident season.
- **Geese on the water** drift, paddle away from you, and take off in a hurry when you get close, shoot at them, or
  miss them. They fly to another part of the lake or another lake, coming in along a clear lane, and climb over hills
  and trees on the way. A flock that keeps being disturbed abandons the lake for a while (lake wariness).
- **Flocks have a lasting identity** (saved with the world): a flock that leaves in autumn returns next spring with
  whoever survived; a flock that was killed off is replaced by a new one.
- Arrows and other projectiles kill geese; spears and melee are dodged. Sneaking gets you closer, as with vanilla
  animals; creative players go unnoticed.

## Commands
`/flocks send [count] [species]` sends a passing flock over you. `/flocks schedule` shows today's migration plan.
Admin (controlserver): `/flocks arrive [species]` sends a landing flock to a lake near you now; `/flocks land [count]
[species]` puts a group straight onto nearby water; `/flocks calm` resets lake wariness; `/flocks residents` shows
spawning, loaded birds, flocks and abandoned lakes.

## Settings
`ModConfig/flocks.json` (client): automatic migration, flocks per day, storm thresholds, call volume, species on/off.
`ModConfig/flocks-server.json` (server): resident spawning, lake size and shore distance, crowding cap, lake wariness.

## Birds are data
Each bird is a file in `assets/<domain>/config/flocks-species/` (any mod can add one): its drawn-flock shapes, size,
speed and wingbeat rhythm, migration windows and hours, flock sizes, calls, and, for birds that land, the entity it
lands as. A landing bird's behaviour (flee and takeoff distances, cruise speed, takeoff and splash sounds) is in its
entity file with the `flocks.waterfowl` behaviour, like vanilla animals. See `config/flocks-species/goose.json` and
`entities/greylag.json`; fields are documented in `Flocks/Species.cs`.

## Tools
- Models: `python tools/make_greylag.py` (and `make_crane.py`) regenerate a bird's shape and animations (`--texture`
  repaints the texture, overwriting hand edits); `python tools/preview_greylag.py` renders it without the game.
- Build: `./build_zip.ps1 [-GamePath <folder with VintagestoryAPI.dll>]` -> `dist/flocks_<version>.zip`
  (default GamePath: the 1.22.7 reference DLLs at %USERPROFILE%\agent-tools\vsref1227).
- Engine tests: `./tools/run_engine_tests.ps1` runs the bird AI on a scratch dedicated server (new world, no players)
  against a built test site and prints PASS/FAIL lines with measurements (about 6 minutes). See DESIGN.md.

The amphithere model (tools/make_amphithere.py) ships as assets only and is unused.
