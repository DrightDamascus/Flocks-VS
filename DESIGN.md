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
1. Calls/sounds: answered in 0.8.0 - freely licensed recordings (CC0 / CC BY-SA, see CREDITS.md); flying flocks call
   now and then from one bird, landed geese use entity idle/hurt/death sounds plus takeoff wingbeats.
2. Models: answered - own low-poly shapes per migrant (greylag goose done in 0.3.0, common crane in 0.5.0); the vanilla hawk stays as a hawk species.
3. Should flocks follow the global latitude or be world-wide synchronized per hemisphere for multiplayer consistency?
4. Night migration (many songbirds migrate at night) - show silhouettes against the moon or skip?
5. Integrate Continental Terrain climate in step 2 or later?

## Landing
Goal: geese (later cranes) come down on lakes, float, take off and land again, until migration moves them on.

Decision (Dright, 0.6.0): landed birds are real server entities, huntable animals. Flying migration flocks stay
client-only visuals. So the mod is required on the server as well as on clients.
- Why: they must be huntable (arrows and other mods' guns), drop items, and be the same for every player.
- Costs we accept: server ticks and entity sync per bird, chunk saves, and despawn rules. Keep groups small (5-12).
  Until 0.9.0 every takeoff ended in a despawn so they never piled up. Decision (Dright, 0.9.0): a flushed group
  doesn't vanish; it lands elsewhere on the lake or on another lake. Geese leave the world only when departing for
  good (end of the resident season, a test goose's lifeHours) or after ~1200 blocks with no lake found, and only
  once no player is within 128 blocks. Pile-ups stay bounded by vanilla's maxQuantity and the residents' area cap.
- Combat rule: ranged projectiles kill; thrown spears and melee are dodged (damage zeroed in flocks.waterfowl before
  the health behaviour runs) and the group takes off. Geese are a wild feather source, so the harvest drops plenty.

Done in 0.6.0 (first step): entity flocks:greylag, behaviour flocks.waterfowl (float, drift, paddle away, takeoff,
despawn, spear/melee dodge), and `/flocks land` (finds a fresh-water patch near the player, spawns a group).

Done in 0.7.0, reworked in 0.7.1: resident geese spawn through vanilla's spawner (spawnconditions in
entities/greylag.json, like fish: inside still surface water; like wolves: runtime maxQuantity (all loaded geese, scaled per player),
MinDistanceToPlayer). A server OnTrySpawnEntity veto adds what vanilla lacks: the resident season (spring window to
autumn window, all year near the equator), a minimum lake size, a maximum distance from shore (0.8.4: 8-direction scan, ShoreDistance), and a generous per-area safety cap. Residents leave
with a takeoff when autumn starts. No global cap and no custom placer; geese appear out of sight like other animals.
Not done: nudging a visual V toward a lake (the client has no lake knowledge).

Done in 0.9.0: flight for the landed geese (Waterfowl.cs, LakeSearch.cs). Takeoff is a hurried run across the water
(takeoff animation: deep fast strokes, body nose-up, feet pattering), a climb to 11-13.5 blocks/s, then a glide in
(fly-soar, then the land flare: wings high and cupped, feet forward) and a short skid. The group's spot is picked
once: open water on the same lake at least 26 blocks from the threat and 14 from the goose, near shore; else the
nearest lake within 160 blocks not toward the threat; else they fly on, searching every 4 s. Flight state is saved
in WatchedAttributes. A death animation (roll onto the side, neck limp) replaces the frozen swim pose.

Decisions (Dright, 0.10.0) for flocks landing and leaving:
- Flocks that only pass over: 7-25 birds (hawks 3-10), unchanged. Landing flocks: 7-12, and natural resident groups
  match (spawn groupSize 7-12; runtime maxQuantity, a cap on all loaded geese, raised 12 -> 24: about two groups).
  A departing flock is simply whatever group is on the lake.
- Landing flocks come from the migration schedule, and landed flocks rejoin it when they leave. A flock that is
  killed off is replaced by a new one at the next migration. This needs a lasting, server-side identity per flock
  (the client schedules can't see deaths): FlockRegistry.
- Distant flight stays client-drawn (sky renderer: visible 300-1500 blocks, no server cost). Real entities only for
  the final approach and the climb-out: the server only simulates entities within 128 blocks of a player.
- No size matching yet: the sky goose is drawn ~4.5x the entity (6.4 vs 1.4 block wingspan) and the jump at a
  hand-off is left visible on purpose, to judge it. Planned fix if needed: scale drawn birds by viewer distance.
- Geese are the test species; per-species data (family unit, flock size) is to follow for the others.

Done in 0.10.0 (first half): FlockRegistry (saved with the world; every goose carries a flock id; deaths counted;
states Landed / Away / Lost) and the departure hand-off. Departing geese (autumn, test-goose lifeHours, or nowhere
to land after being flushed) climb out; once their centre is 80 blocks from every player the server sends their
positions, heading and speed to clients within 1600 blocks, removes the entities and marks the flock Away. Clients
continue them as a drawn flock starting exactly there, easing into a V, up to cruising height and species speed
over ~10 s.

Done in 0.11.0 (second half): landing flocks (LandingFlocks.cs, server). On spring migration days each online player
gets up to one landing a day (scaled by the day's spring strength, at a goose flying hour); `/flocks arrive` forces
one. It needs open water 40-160 blocks from the player with no geese on it, in the resident season. The flock is the
one Away longest (returning with whoever is left), else a replacement for a Lost one, else new (7-12). The server
fixes the path up front (a straight line along the arrival heading, from ~800 blocks out to a hand-off point 60
blocks short of the lake, cruise height clear of all loaded terrain along it, a descent steep enough to clear any
hill) and sends it to every client within 1600 blocks; each draws the flock from the path and the time alone, so all
agree where every bird is. At the hand-off the server spawns the real geese at the same spots, already flying at
13 blocks/s toward their own landing spots, and they glide in (Waterfowl). Autumn has no landings: geese out of
season would leave at once. The drawn formation closes to the real geese's spacing over the last 8 s, but the birds
stay sky-sized until the swap (size jump left visible on purpose).

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
