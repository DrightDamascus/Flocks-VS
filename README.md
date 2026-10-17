# Flocks

Vintage Story 1.22.7 mod (modid `flocks`): migrating bird flocks that mark the seasons, and wild geese that land on
lakes, can be hunted, and come and go with the year. Install on both server and clients. Design notes and decisions:
DESIGN.md. Version history: CHANGELOG.md.

## What it does
- **Passing flocks** (drawn by each client, no entities): geese, cranes, mallards and now and then a heron cross
  the sky on migration days in spring and autumn, at their own hours and heights, held back by storms, calling as they
  pass. On the coast, loose groups of gulls pass at any time of year.
- **Landing flocks** (real entities): on spring migration days a flock of greylag geese or mallards flies in from up
  to 320 blocks out and glides down onto a lake near you. Both also spawn naturally on lakes (mallards on ponds too)
  in their resident season.
- **Grey herons**, big ones, stand in water a block deep, stalk and strike, and flap off croaking when you get too
  close.
- **Golden eagles** perch on the highest point around, soar in circles on thermals over open high ground, and stoop on
  hares and ducks from high up, then feed on the kill.
- **Herring gulls** live on the sea along the coast all year. They are bold and loud, wheel over the water after taking
  off, and now and then go up of their own accord and settle further along the shore.
- **Carrion crows** feed in small groups on open ground, fly up into the trees cawing when you come close, roost in
  the trees at night, and raid crops, killing them as hares do. Fences don't stop them; a **straw dummy** does:
  no crow raids crops or comes down to feed within 15 blocks of one, ever.
- **Tawny owls** roost hidden in the trees by day and sit tight until you are nearly under them. After dusk they
  hunt from tree tops at the edge of the woods, fence posts and walls, hooting through the night, and glide down silently onto
  **wood mice**, small and rare animals out in the grass at night.
- **The handbook** has a page for each bird and a "Birds" guide page (migration, wary birds, crows and crops, owls,
  hunting).
- The birds are deliberately bigger than life (about half again), so they read as birds next to the game's animals.
- **Geese on the water** drift, paddle away from you, and take off in a hurry when you get close, shoot at them, or
  miss them. They fly to another part of the lake or another lake, coming in along a clear lane, and climb over hills
  and trees on the way. A flock that keeps being disturbed abandons the lake for a while (lake wariness).
- **Flocks have a lasting identity** (saved with the world): a flock that leaves in autumn returns next spring with
  whoever survived; a flock that was killed off is replaced by a new one.
- Arrows and other projectiles kill birds; thrown spears fly past them and melee is dodged. Sneaking gets you closer, as with vanilla
  animals; creative players go unnoticed.

## Other mods
- **Biomes**: each bird (and the mouse) spawns only in its own realms, and landing and passing flocks keep to them too.
- **Butchering**: geese, herons, eagles and gulls become carcasses (the bird itself, hanging on the hook) to carry home
  and butcher; the smaller birds are plucked on the spot as before.

## Commands
`/flocks send [count] [species]` sends a passing flock over you. `/flocks schedule` shows today's migration plan.
Admin (controlserver): `/flocks arrive [species]` sends a landing flock to a lake near you now; `/flocks land [count]
[species]` puts a group straight onto nearby water; `/flocks calm` resets lake wariness; `/flocks residents` shows
spawning, loaded birds, flocks and abandoned lakes.

## Settings
`ModConfig/flocks.json` (client): automatic migration, flocks per day, storm thresholds, call volume, species on/off.
`ModConfig/flocks-server.json` (server): resident spawning, lake size and shore distance, crowding caps, lake wariness,
crop raiding (on/off, how often) and the scarecrow's radius.

## Birds are data
Each bird is a file in `assets/<domain>/config/flocks-species/` (any mod can add one): its drawn-flock shapes, size,
speed and wingbeat rhythm, migration windows and hours, flock sizes, calls, and, for birds that land, the entity it
lands as. A landing bird's behaviour (flee and takeoff distances, cruise speed, takeoff and splash sounds) is in its
entity file with the `flocks.waterfowl` behaviour, like vanilla animals. See `config/flocks-species/goose.json` and
`entities/greylag.json`; fields are documented in `Flocks/Species.cs`.

## Tools
- Models: `python tools/make_greylag.py` (and `make_crane.py`, `make_mallard.py`, `make_heron.py`, `make_eagle.py`,
  `make_gull.py`, `make_crow.py`, `make_owl.py`, `make_mouse.py`) regenerate a
  bird's shape and animations (`--texture` repaints the texture, overwriting hand edits); `python tools/preview_greylag.py` renders it without the game,
  and `python tools/preview_model.py make_owl out.png idle-stand:0 fly-flap:8` renders any of them in chosen frames.
- Build: `./build_zip.ps1 [-GamePath <folder with VintagestoryAPI.dll>]` -> `dist/flocks_<version>.zip`
  (default GamePath: the 1.22.7 reference DLLs at %USERPROFILE%\agent-tools\vsref1227).
- Engine tests: `./tools/run_engine_tests.ps1` runs the bird AI on a scratch dedicated server (new world, no players)
  against a built test site and prints PASS/FAIL lines with measurements (about 9 minutes). See DESIGN.md.

The amphithere model (tools/make_amphithere.py) ships as assets only and is unused.
