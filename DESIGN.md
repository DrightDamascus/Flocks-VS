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

## Landing
Goal: geese (later cranes) come down on lakes, float, take off and land again, until migration moves them on.

Decision (Dright, 0.6.0): landed birds are real server entities, huntable animals. Flying migration flocks stay
client-only visuals. So the mod is required on the server as well as on clients.
- Why: they must be huntable (arrows and other mods' guns), drop items, and be the same for every player.
- Costs we accept: server ticks and entity sync per bird, chunk saves, and despawn rules. Keep groups small (5-12),
  give them a lifetime (lifeHours) and make takeoff end in a despawn, so they never pile up.
- Combat rule: ranged projectiles kill; thrown spears and melee are dodged (damage zeroed in flocks.waterfowl before
  the health behaviour runs) and the group takes off. Geese are a wild feather source, so the harvest drops plenty.

Done in 0.6.0 (first step): entity flocks:greylag, behaviour flocks.waterfowl (float, drift, paddle away, takeoff,
despawn, spear/melee dodge), and `/flocks land` (finds a fresh-water patch near the player, spawns a group).

Done in 0.7.0, reworked in 0.7.1: resident geese spawn through vanilla's spawner (spawnconditions in
entities/greylag.json, like fish: inside still surface water; like wolves: runtime maxQuantity per player area,
MinDistanceToPlayer). A server OnTrySpawnEntity veto adds what vanilla lacks: the resident season (spring window to
autumn window, all year near the equator), a minimum lake size, and a generous per-area safety cap. Residents leave
with a takeoff when autumn starts. No global cap and no custom placer; geese appear out of sight like other animals.
Not done: nudging a visual V toward a lake (the client has no lake knowledge).

Next steps:
- Hand-off from the sky: when a planned goose flock passes a suitable lake (fresh water, 36+ surface blocks, 64+
  blocks from the player, near its heading), the client asks the server to land it. The server checks the lake
  and spawns the group. The client fades the flying flock out over the lake as the entities appear. The server owns
  the spawn so every player sees the same birds.
- Descent: spawn the entities in the air above the lake in fly-flap/soar, glide them down, then switch to swim. Until
  that exists, they appear on the water.
- Floating stays as now. Cranes would use idle-stand/walk in shallows or on the shore.
- Takeoff triggers: player close (7 blocks), any hit, a storm at the lake (the Migration Stormy check), lifetime or
  the end of flying hours. After takeoff a flock may re-land on another lake (server spawns again) or leave on the
  migration heading.
- Planner: PlannedFlock gets optional stops. Strength() drives how many; once a species' window ends, no new stops
  and resident groups leave. The server should cap total landed geese per area (e.g. 30 within 128 blocks).
