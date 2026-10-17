# Flocks - design (step 1)

## Behaviour
- Purely visual, client-only. Birds never land, never collide, are not entities, cannot be hunted.
- Flocks spawn out of view, cross the sky ~60-120 blocks above the player's ground level, and despawn out of view.
- Species (real migrants): greylag goose, common crane -> V/echelon formations of 7-25, slow wingbeats, calls;
  barn swallow, common starling -> loose flocks of 20-80 (starlings may murmurate briefly in autumn dusk).
- A few flocks per migration day at most. Until 0.19.0 only one flock could be in the sky at a time; now several can
  (Dright: why one at a time?), kept apart like air traffic: a new flock cruises at least 15 blocks above or below
  every flock already up, and when two come near each other without that separation, the newer climbs over the older.

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
2. Models: answered - own low-poly shapes per migrant (greylag goose done in 0.3.0, common crane in 0.5.0); the vanilla hawk stayed as a hawk species until 0.19.1, when it was removed.
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
- Sizes (Dright, 0.11.1): one fixed size per bird at every distance, no distance tricks, so it can be judged by eye.
  The sky goose is drawn at the landed entity's size, read from entities/greylag.json client.size; raise that one
  number to enlarge both. Since 0.17.0 every bird is about half again its real size (Dright: true-size birds looked
  tiny next to the game's boars and deer, and a block is a metre only loosely anyway): goose 0.98 (2.6-block
  wingspan), mallard 0.75, heron 1.05, eagle 1.1, cranes 3.3 and hawks 1.8 block wingspans (Species.Wingspan).
  Formation spacing and the flight look-ahead's half-span grew with them. Until 0.11.0 the sky birds were ~4.5x real size to read from far off; at
  true size a goose is ~2 px at ~540 blocks, which is now where flocks appear and fade.
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

Decision (Dright, 0.12.0): landing and departing flocks are real geese the whole way; the drawn-flock swaps of
0.10.0-0.11.1 are gone (the transition never looked right). The engine allows it: the server ticks an entity, and
sends it to a player whose client has the chunk, within max(128, entity.SimulationRange) blocks, and clients render
entities out to their view distance (decompiled PhysicsManager / SystemRenderEntities). A flying goose's
SimulationRange is 400 (EntityBehaviorWaterfowl.FlightRange); on the water it is back to the default 128. Landing
flocks appear as a V of geese up to 320 blocks out along the arrival heading (loaded chunks, within 360 of the
player), all at 13 blocks/s, each heading for its own spot. Departing flocks fly off and are removed 360 blocks from
every player (3-4 px by then; out of sight below that view distance), and their record goes Away. Only flocks that
merely pass over are still drawn by the client. Goose size 0.65 (1.7-block wingspan), hitbox scaled with it.

In-engine tests (0.12.1): `tools/run_engine_tests.ps1` starts a scratch dedicated server (new world, no players) with
the built mod and the test mod in tools/FlocksTest. It builds a test site in the sky at spawn (a stone slab, pond A
open with a 14-high wall beside it, pond B ringed by 8-high "trees") and runs: settle, flush toward the wall, a flush
in the tree-ringed pond, departure, a landing flock from 320 blocks over natural terrain, and repeated cycles with
memory and entity counts. Every flying goose is sampled 10x a second for wall hits, being inside ground or trees,
hanging in the air, aborted glides (with the reason) and where it lands. Two engine facts it relies on: entities with
no player near don't tick unless AlwaysActive, and the server runs physics only for entities a player tracks
(PhysicsManager skips IsTracked == 0), so the test runs the geese's physics itself at 30 ticks a second.

Landing approach (0.12.1): a goose picks a lane into its spot, the clearest of 16 directions (the gentlest descent
that keeps 1.5 blocks over banks and trees within 40 blocks, preferring its own side), flies out to it, follows it in,
and glides down once its line is at least that steep (up to 45 deg, slowing to hold it). A glide is aborted only for
something still between the goose and its spot. A spot is given up after 40 s, 3 aborted glides, or no lane under
45 deg; after 3 given-up spots the flock rejoins the migration.

Decisions (Dright, 0.13.0), Phase 1 AI:
- Missed shots flush the group like a hit: any fast non-creature whose path passes within 4 blocks (arrows, spears,
  stones, other mods' shot), flying on away from the shooter.
- Lake wariness, not player memory: a flock learns to avoid a place, it does not hold a grudge against a player.
  Every flush +1, a kill +0.5 more; from 1 the flock is skittish (flee and takeoff distances x1.8); at 2.5 it abandons
  the lake and rejoins the migration, and no geese land or spawn within 64 blocks for 2 in-game days. Wears off at 1
  per in-game day. A careful hunter gets about two geese from a flock before it goes. All in flocks-server.json;
  `/flocks calm` resets it. Vanilla deer only have a short spooked state (fleeondamage, 40-60 s) and a herd alarm.
- Players are sensed as vanilla animals sense them (AiTaskBaseTargetable.CanSensePlayer): animalSeekingRange stat,
  sneaking on the ground x0.6, creative and spectator players unnoticed.
- Done in 0.14.0: data-driven species. Each bird is a file in config/flocks-species/ (any domain, so other mods can
  add birds): drawn-flock shapes and size, flight rhythm, migration windows and hours, passing and landing flock sizes,
  calls, and for birds that land the entity they land as. The landed bird's behaviour tuning and its takeoff and splash
  sounds are in its entity file (flocks.waterfowl attributes, entity sounds), as vanilla does. Resident spawning,
  landing flocks and /flocks land work for every species that lands. Each side keeps its own species dictionary (client
  and server share a process in singleplayer; only the client has meshes).
- Phase 2, the roster (mallard, heron, eagle, gull, crow on top of goose, crane, hawk). Done in 0.15.0: the mallard,
  the first bird added as content on the data-driven AI: tools/make_mallard.py (greylag bones, so the greylag
  animations; drake and hen textures picked at random per bird), entities/mallard.json, config/flocks-species/
  mallard.json and sounds/duck/. New per-species data it needed: takeoffRunSeconds/takeoffRunSpeed (a dabbling duck
  springs straight up instead of running) and minLakeSize (ducks take ponds of 25 surface blocks).
- Done in 0.16.0: the grey heron, the first new behaviour type: wading. Vintage Story's still water is always a full
  block deep, so a true-size heron would stand chest-deep. At first it got stilt legs (~1 block on a 1.75-block bird);
  in 0.17.0 (Dright: big herons with normal legs rather than small ones on stilts) it became a bigger bird (size 1.05)
  whose only slightly long legs reach the bottom. It stands
  on the bottom of water one block deep (species habitat "wade"; a taller selection box while standing so the game's
  swimming check, water at 66% of the box, looks above the water), rests hunched, stalks and strikes, goes alert and
  backs off when approached, and flushes from further than geese. Landing spots are wadeable water a block away from
  any drop-off; a wader that ends up in deep water floats up and paddles back. It spawns on its own (only in water one
  block deep) and doesn't arrive in landing flocks (arriveInFlocks false). tools/make_heron.py: own bones (two-part
  neck for the S) and animations; ground poses lift the body by the leg length so the feet stand at the entity's feet.
- Done in 0.17.0: the golden eagle, the second new behaviour type: a raptor (species habitat "air", Raptor.cs, on the
  shared EntityBehaviorBird base with the waterfowl's flight helpers). Perches on the most prominent high point
  within 64 blocks, soars in circles on thermals 35-65 blocks up within its territory, glides between thermals, and
  hunts: every few seconds it looks for prey (preyCodes: hares and mallards) well below it in the open, closes in at
  height until it is over it (a long shallow dive would hit what's in between), stoops at up to 38 blocks/s,
  strikes and walks onto the kill to feed (flying to it if it fell off a ledge); waterfowl may spot it and flush,
  a duck caught on the water is carried off, and each attack counts toward the flock's lake wariness (Dright's idea:
  an eagle hunting a pond drives the ducks off it for a few days), or pulls up if the prey got under cover. Flushes from a perch like
  the geese; a missed shot startles it. Spawns alone on open high ground, at most two within 192 blocks; resident
  all year; no landing flocks and no drawn sky flocks.
- Removed in 0.19.1: the hawk (Dright). It was the only bird drawn with a vanilla asset (the game's unused hawk shape,
  referenced, not shipped), and real hawks and buzzards pass over an ordinary spot rarely and mostly alone; groups form
  only at migration bottlenecks. The golden eagle covers the raptor. flocks.json's Hawks switch is kept, unread.
- 0.19.1: the eagle gets vanilla predators' rest after a meal (Dright: model it on foxes, wolves and bears, which hunt
  whenever hungry within 15-30 blocks and are "saturated" for 1200 s after eating). It doesn't hunt for 1200 s after it
  starts feeding on a kill.
- 0.19.1: a thrown spear no longer visibly strikes a bird and then does nothing (Dright: it should look like it never
  connects). Each bird adds itself to every nearby thrown spear's hit list (EntityProjectileBase.entitiesHit), which
  vanilla's projectile checks before colliding, so the spear passes by.
- Done in 0.18.0: the herring gull, the first sea bird (Dright: gulls stay by the sea). Species habitat "sea" is the
  ducks' floating on salt water: every place the waterfowl look for or check water (lake search, spawn checks, /flocks
  land, paddling back to water) takes the species' liquid, so lakes and the sea never mix. yearRound: resident all
  year, and its passing groups come any day in any direction, drawn only when the player has sea within 96 blocks.
  Gulls are bold (short flee and takeoff distances; warinessFactor 0.5 halves what a flush or a kill adds to the
  flock's wariness) and fly like gulls through three new flocks.waterfowl attributes that are off for the other birds:
  after a takeoff the group wheels round its takeoff spot for 8-20 s before heading for its spot, now and then it goes
  up of its own accord (a loafing flight, no disturbance), and it glides half of its level flight. Not done (maybe
  later): gulls standing on beaches, and scavenging at kills.
- Handbook (0.18.0, Dright: the birds need handbook entries without clogging it): vanilla's way. Each creature item
  already has a handbook page; its text is the lang key "<domain>:item-handbooktext-<item code>" written out in full
  (keys with a colon are taken as they are). One guide page, "Birds" (config/handbook/birds.json, like vanilla's
  game-mechanic pages), covers what the birds share and links to each bird's page.
- Done in 0.19.0: the carrion crow, the third behaviour type: a land bird (species habitat "ground", flocks.corvid,
  Corvid.cs on EntityBehaviorBird). Dright: crows should act like real crows. A group feeds on open ground (soil, sand,
  gravel open to the sky) around a shared patch, walks off from people, flies up into trees (or onto prominent walls and
  crags) away from a threat and comes back down when nobody is near, and roosts in the trees from dusk to dawn.
  Vintage Story's leaves have no collision box, so a perched crow holds its own height. Flushes count toward the
  flock's wariness as for the waterfowl; the abandon check is by position, so it works for an area as for a lake.
- Crop raiding and the scarecrow (decided with Dright, 0.19.0): crows eat crops exactly as vanilla's hares and deer do
  (Dright: you can't farm without fences because of hares, so why would crows be gentler): the farmland's own
  IAnimalFoodSource.ConsumeOnePortion, so the crop becomes a dead crop that still gives its seed. Players fence hares
  out; crows fly over fences, so the scarecrow is the defence. Each crow eats a crop every 3-6 game hours. The
  scarecrow is vanilla's straw dummy (its recipe, 2 hay blocks over a stick, is what Dright had in mind): within 15
  blocks across the ground of a standing one, crows don't raid, feed or spawn, and crows feeding there when it goes up fly off (not a disturbance). It never stops working and doesn't need
  moving (Dright: realism that turns into a chore isn't wanted). Crows find crops through vanilla's POIRegistry, the
  food points of interest farmland registers for hares and deer. Server config: CropRaiding, CropRaidHoursMin/Max,
  ScarecrowRadius, MaxCrowsNearby. Not done (ideas for Dright): mobbing the eagle, gathering at carcasses, following
  players who till or harvest.
- Done in 0.20.0: the tawny owl, the fourth behaviour type (species habitat "wood", flocks.owl, Owl.cs on
  EntityBehaviorBird), chosen by Dright over the barn owl and the eagle owl: a woodland bird, and the mod's first night
  sounds. Roosts by day, hunts from branches at night, hoots. Decisions:
  - Prey: vanilla has no rodents, so the mod adds the wood mouse (Dright: mice yes, but no clutter). Passed over: mice
    that appear only at the hunt, and no mouse at all (a mouse in the owl's model). The mouse is rare: a low vanilla
    spawn cap (maxQuantity 4) and despawn after 10 minutes away from players, so mice never build up. It runs on
    vanilla's AI tasks, no code; it notices an owl only at 2.5 blocks, so most pounces land.
  - Branches (Dright asked what to do about trees having no branches): they do have them. Vanilla's tree generator
    grows branches through the crown out of branchy leaves, which keep the default collision box, while ordinary
    leaves have none. So a perch is the first solid block under a column's leaves (EntityBehaviorOwl.PerchBelowLeaves):
    a branch, or the trunk's top. Only leaves lie above it, so the owl can always drop onto it straight down; its
    hitbox (0.45) fits inside the column. A roost is such a branch with 2+ leaves over it, near the trunk (more cover
    scores higher). Branchy leaves are drawn as ordinary leaf blocks (Dright), so an owl on one is hidden. Dright chose:
    hidden by day (you find it when it bursts out of a tree you walk under, as with real tawny owls), visible at night.
    So a hunting perch is in the open over open ground: on top of a crown at its edge (a neighbouring column 2+ lower),
    2-10 blocks up, holding its height on the leaves as crows do; a fence post; a wall or crag top. Roost fallback: a
    tree top.
  - It sits tight by day (4 blocks, measured across the ground, since it is up a tree) and is warier at night (9).
  - Fed, it rests 1200 s like vanilla's predators (follow vanilla). It swallows the mouse whole, so the body goes.
  - Owl flight is silent: no wingbeat sounds at takeoff, unlike every other bird here.
  - No drawn passing groups (owls don't fly over in groups) and no flock: each owl keeps its own territory (48 blocks
    from where it appeared); no wariness either, like the eagle.
- Biomes support (after 0.20.0; a patch by Hydromancerx of the Biomes team, Dright asked for the flocks to follow it too). Biomes
  vets vanilla's spawning, so resident birds needed only their realm lists. Flocks this mod places itself bypass that:
  landing flocks ask Biomes (server) before coming down; drawn flocks are the client's, and Biomes runs only on the
  server, so the server tells each client which species are out of realm where its player stands (every 20 s, sent
  only on change) and the client leaves them out. Biomes is reached through its documented public API by reflection,
  so Flocks has no hard dependency; anything unexpected logs once and allows everything. Seasons are left to Flocks
  (its own migration windows), so the realm data passed to Biomes says every season.
- Butchering support (Dright chose: full carcasses for the big birds only). Butchering is opt-in per animal: a patch
  adds its "butcherable" behaviour, which on an empty-handed right click turns the dead animal into a carcass item
  carrying the animal's own drops and weight; Butchering scales those and adds blood and offal on the hook and table.
  Goose, heron, eagle and gull get it; mallard, crow, owl and mouse stay small game, plucked on the spot. Sizes follow
  Butchering's own animals: all "small" (chicken, hare, fox), mass 0.02 for goose and eagle (like its duck), 0.01 for
  heron and gull. The carcass is each bird's own model posed, as Butchering does for its chicken (its generic shape
  for modded animals turned an eagle into "a leg": Dright): lying dead, hanging head down by the feet, feathered until
  plucked, then bare skin (the model's texture repainted).
- Meat yields (Dright): by the real bird's weight at vanilla's chicken rate (about 0.9 poultry per kg), but never
  less than one whole strip for a small bird (no fractions that sometimes give nothing, no special small item).
- Localisation scope (Dright): everything a player sees goes through the lang file, including creature names alive and
  dead and the command help and replies; the long admin/debug reports stay English (technical, mostly numbers).

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
