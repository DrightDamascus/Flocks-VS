# Flocks - design (step 1)

## Behaviour
- Purely visual, client-only. Birds never land, never collide, are not entities, cannot be hunted.
- Flocks spawn out of view, cross the sky ~60-120 blocks above the player's ground level, and despawn out of view.
- Species (real migrants): greylag goose, common crane -> V/echelon formations of 7-25, slow wingbeats, calls;
  barn swallow, common starling -> loose flocks of 20-80 (starlings may murmurate briefly in autumn dusk).
- Only one flock in the sky at a time; a few per migration day at most.

## Timing rules
- Inputs: vanilla calendar GetSeasonRel(pos) (hemisphere-aware, 0 = start of spring) and OnGetLatitude(z).
- Spring window seasonRel 0.05-0.25: head poleward. Autumn window 0.55-0.80: head equatorward.
- Peak frequency mid-window; species stagger (swallows/cranes early, geese late autumn/early spring).
  Done: goose spring 0.03-0.22 / autumn 0.62-0.85; crane spring 0.02-0.18 / autumn 0.55-0.75, daytime, flying higher.
- Latitude: no migration within ~10 deg of the equator (that is the wintering ground); higher latitudes start
  autumn earlier and spring later. Headings get +/-20 deg jitter so flocks are not on rails.
- Optional later: read Continental Terrain climate (Koppen zone, temperature) when installed, softly via reflection/API.

## Rendering
- One client IRenderer (EnumRenderStage.Opaque) drawing instanced meshes: one small mesh per species, two or three
  baked wing poses swapped by time, per-bird transform in an instance buffer. One draw call per species.
- Why: no server entities = no network traffic, no AI ticks, no chunk loading; at 60+ blocks up a bird is a few pixels,
  so low-poly meshes with 2-3 frames read fine. Cost is a few hundred tris and one CPU update loop at 20-60 Hz.
- Flock motion: leader follows a straight heading + gentle noise; followers hold V slots or boids-lite offsets.

## Config (ModConfig flocks.json, client)
- enabled, maxFlocksPerDay, altitudeMin/Max, species toggles, audioVolume, debugLog (season log), forceMigration (test).

## Open questions
1. Calls/sounds: vanilla has none for these species - record/license our own or go silent?
2. Models: answered - own low-poly shapes per migrant (greylag goose done in 0.3.0, common crane in 0.5.0); the vanilla hawk stays as a hawk species.
3. Should flocks follow the global latitude or be world-wide synchronized per hemisphere for multiplayer consistency?
4. Night migration (many songbirds migrate at night) - show silhouettes against the moon or skip?
5. Integrate Continental Terrain climate in step 2 or later?

## Landing (design only, not implemented)
Goal: geese (later cranes) come down on lakes, float, take off and land again, until migration moves them on.

Recommendation: keep landed birds client-only visuals, like the flying flocks.
- Real server entities would give hunting and one shared truth in multiplayer. But they cost server ticks, network
  traffic and chunk loading/saving, they need AI, pathing and despawn rules, and they would turn a visual mod into a
  gameplay one. That is a much bigger change.
- Client-only keeps it cheap: a flock of 7-25 birds is one renderer entry with baked meshes, and nothing is saved.
  The cost is that each player sees their own birds, and you can't hunt them. Plans already come from the world seed
  and day number, so players near each other get the same flocks at the same times. Picking the lake from the seed as
  well would put them on the same water, but players arriving at different times won't see identical birds. That is
  fine for ambience.
- Huntable birds, if wanted later, should be a separate opt-in: an entity spawned only when a player gets close to a
  landed flock, not a change to the visual system.

Picking a lake (client, at descent time, from loaded chunks only):
- Sample the rain map along the flock's path from ~150 to ~400 blocks ahead and to either side. A column counts as
  water when its top block is still fresh water (lakes, not ocean). Flood-fill from a hit and accept it at roughly
  12x12 surface blocks or more. Prefer open water, at least 64 blocks from the player, within ~45 deg of the heading,
  and seeded per day/flock so it is reproducible. No lake found: the flock just flies on as now.

Descent and landing:
- Leave cruise height early and glide down at a few blocks per second (soar poses, wide shallow circle over the lake).
  Do the last ~8 blocks with flaps and the nose up, then a short skid onto the surface while the wings fold
  (flap -> blend -> swim). The formation loosens into a raft over the last ~20 blocks. Terrain clearance stays on
  until the final approach.

Floating:
- Each bird drifts slowly (0.1-0.3 blocks/s) with small random turns, bobbing in the swim pose. A circle around the
  landing point keeps them on the water and they push away from the shore. Cranes would use idle-stand/walk in
  shallows or on the shore. No collisions, no pathing: positions only.

Takeoff triggers:
- Player within ~24 blocks (sooner when sprinting or mounted), a storm at the lake (the same Stormy check), a timer
  (seeded, ~1-4 game hours afloat), or the flight-hours window ending. Takeoff: run along the surface into the wind
  with fast flaps, climb out, re-form the V, then either circle to another lake or continue on the migration heading.

Repeat loop and the planner:
- PlannedFlock gets an optional stop list. Each plan entry still starts as a flying pass, but on a migration day a
  goose flock may have one or two stops (chance from strength, a flight-hours cap). After each stop the flock
  continues on its heading. A flock that is still afloat when the hours window ends either stays the night
  (resumes the next morning if that day still has migration strength) or leaves at dusk.
- Once a species' window ends (strength 0), resident flocks leave and no new stops are planned. "Migration moves them
  on" falls out of the existing Strength().
- The renderer only allows one flock in the sky at a time; a landed flock counts as on the ground, so it does not
  hold up the next planned flyover.

First step (small, testable): `/flocks land` (debug only). It sends one goose flock that picks the nearest qualifying
lake in loaded chunks, glides down, floats there with the swim pose (bake the swim cycle like fly-soar), and takes
off again when the player comes within 24 blocks. No planner changes yet. That proves out the lake check, the pose
baking and the landing transitions before any scheduling.
