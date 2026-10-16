# Changelog

## 0.19.0
The carrion crow, the first land bird, and the straw dummy as a scarecrow:
- New model (tools/make_crow.py): all black with a blue-purple sheen on the back and wing coverts, a big head, a stout
  bill, a rounded tail, legs to walk on, and broad wings with five spread fingers. Its own animations: rowing flight
  and glides, takeoff, landing, standing and looking round (also on a perch), walking, pecking at the ground, death.
- flocks:crow (entities/crow.json, behaviour flocks.corvid, Corvid.cs): size 0.75 (1.5-block wingspan, half again a
  real crow's 1 m), 4 health; drops poultry and feathers. A group of about 2-8 feeds together on open ground (soil,
  sand or gravel open to the sky) around its patch: walking about, pecking, looking round, and walking off from anyone
  who comes within 18 blocks. Closer than 10, a shot flying past, or a hit, and the whole group flies up, cawing, into
  the nearest trees (or onto a wall or a crag) away from the threat, sits there for 25-70 s and comes back down to feed
  when nobody is near: on its old patch, or on new ground away from whoever is about. At dusk (19:30) the group goes up
  into the trees to roost and comes down after 6:00. Leaves have no collision box in this game, so a perched crow holds
  itself at the perch's height. Disturbances count toward the flock's wariness as for the waterfowl, for the place
  instead of a lake: flushed again and again, the flock leaves the area for a couple of days. Spears and melee are
  dodged; arrows kill.
- Crop raiding (server config CropRaiding, on by default): every 3-6 game hours (CropRaidHoursMin/Max) a crow looks
  for a crop within 24 blocks, flies over to it (fences don't stop it) and eats it the way vanilla's hares and deer do
  (Dright: no reason crows should be gentler): through the farmland's own food-source code, so the crop dies and still
  gives its seed back. Crows find crops as hares and deer do, through the game's food points of interest (farmland),
  and two crows never go for the same crop.
- The scarecrow is vanilla's straw dummy (2 hay blocks over a stick; Dright): crows don't raid crops, don't come down
  to feed and don't spawn within 15 blocks of a standing one (server config ScarecrowRadius, measured across the
  ground); crows already feeding there when it goes up fly off to ground out of its reach, and one coming in to land
  there goes elsewhere. They never get used to it, wherever it stands. One knocked down doesn't count. Its handbook page says
  so now.
- Spawns on its own on open land with trees about (forest edges, farmland, meadows), all year, in small groups; not
  under trees, not near a scarecrow, at most MaxCrowsNearby (16) within NearbyRadius. No landing flocks. Loose groups
  of 4-20 pass over in the early morning and the evening, out from the roost and back to it.
- Sounds (sounds/crow/): caws, caw series, alarm calls on takeoff, a feeding flock overhead, hurt and death, from
  recordings by Marie-Lan Taÿ Pamart (xeno-canto, CC BY-SA 4.0).
- Handbook: the crow's page, and a "Crows and crops" section on the Birds page.
- Air traffic: passing flocks no longer wait for the sky to be empty (Dright), so local gulls and crows don't hold up
  the migration. Several flocks can be up at once, kept apart in height: a new flock cruises at least 15 blocks clear
  of every flock already in the sky, and when two flocks come within about 120 blocks of each other without that
  separation, the newer one climbs over the older and settles back to its cruise once past. Only storms hold flocks
  back now.
- `/flocks near` lists the birds within 48 blocks: what each is doing, how close it lets you come, and whether it
  senses you (for checking in game). A landing crow whose spot has someone standing at it comes down elsewhere.
- `/flocks land crow` puts a group on open ground near you; `/flocks arrive` says which birds don't come in landing
  flocks. `/flocks residents` counts spawns vetoed by a scarecrow or for not being on open ground.
- The departure hand-off works for any bird behaviour (EntityBehaviorBird.DepartingInFlight), so a flock of crows that
  leaves the area flies off and is removed far from every player, like the waterfowl.
- Tests: two soil fields with flax and a small oak on the test site. Crows settle and feed on the soil, raid the bed
  (one dead crop per raid, the vanilla dead crop), take off as a stand-in player walks up (at about 10 blocks), flush up into the oak with the flock disturbed, come back down to feed, leave
  a straw dummy's field alone (and the feeding ground it covers), fly off when put down beside one, roost in the trees at dusk and come down at dawn.

## 0.18.0
The herring gull, the first sea bird, and the birds in the handbook:
- New model (tools/make_gull.py, on the greylag's bones, so the greylag's animations): white head and body, silver-grey
  back, a long stout yellow bill with the red spot near its tip, pink legs, a short square white tail, and long,
  narrow, pointed wings with a white trailing edge and black tips with white "mirrors".
- flocks:gull (entities/gull.json): size 0.8 (2.1-block wingspan, half again a real gull's 1.4 m, like the other
  birds), 4 health; drops poultry and feathers. Lives on the sea along the coast (species habitat "sea": everything
  the ducks do, on salt water), all year (species yearRound: no resident season, no autumn departure). Bold: flees
  from 9 blocks and takes off at 4.5 (geese 14 and 7), and a flush or a kill disturbs its flock half as much as a
  goose's (species warinessFactor 0.5), so it takes twice as long to drive gulls off a stretch of coast. Noisy: single
  calls while floating, the laughing long call on takeoff.
- Gulls fly like gulls (new flocks.waterfowl attributes, off for the other birds): after taking off the group wheels
  in circles over the water for 8-20 s, climbing, before heading for its new spot (wheelSecondsMin/Max); every 1.5-4
  game hours a group goes up of its own accord, wheels and settles further along, which disturbs nobody
  (loafHoursMin/Max); and they glide half the time they fly (soarShare).
- Spawns on its own on the sea near the shore (inside salt water at the surface, within the server's ShoreDistance of
  land, on at least 100 surface blocks of sea), in groups of about 4-14. No landing flocks. Loose groups of 3-12 pass
  along the coast, any day of the year in any direction, but only when there is sea within 96 blocks of you.
- Sounds (sounds/gull/): calls, the long call, hurt and death from a recording by the Sonothèque ADVL (xeno-canto,
  CC0); the calling flocks overhead from a colony recording by Marie-Lan Taÿ Pamart (xeno-canto, CC BY-SA 4.0).
- The handbook: every bird's creature page has a paragraph about it (where it lives, when it is around, how it
  behaves), and a new guide page, "Birds", covers migration, lake wariness and hunting, and links to each bird.
- Lakes and the sea are kept apart: the lake search, the spawn checks, /flocks land and the paddling-back-to-water
  check all take the species' water ("water" or "saltwater"), so geese and ducks never settle on the sea and gulls
  never on a lake. /flocks schedule says which birds are resident all year and whether there is sea nearby.
- Tests: pond C on the test site is salt water. Gulls settle on it, wheel at least a full circle after a flush and come
  back down on the sea with half a goose's wariness; a loafing flight disturbs nobody; the sea is offered to gulls only
  and the lakes to everyone else; the gull entity file, the handbook texts and the Birds page load.

## 0.17.0
The golden eagle, and bigger birds all round:
- Every bird is about half again its real size (Dright: true-size birds looked tiny next to the game's boars and deer,
  and a block is a metre only loosely anyway). Greylag size 0.98 (was 0.65; 2.6-block wingspan), mallard 0.75 (was
  0.47), heron 1.05 (was 0.7), cranes 3.3 and hawks 1.8 block wingspans (were 2.2 and 1.2). Hitboxes, eye heights and
  how deep they float grew with them, and so did the landing flocks' V spacing and the flight look-ahead's half-span.
- The heron is a big heron on normal legs instead of a small one on stilts (Dright): legs 15 model units instead of 22,
  which at its new size still reach the bottom of a full block of water with the belly clear. Texture repainted for
  the new layout; standing box 1.7 tall; step height 1.1 so one that ends up in deep water can climb back onto the
  shallows. It no longer skids off the shallows when it touches down, and one in deep water now finds the shallows
  above it.
- New model (tools/make_eagle.py): dark brown with a golden crown and nape, a big head on a short neck, a hooked bill
  with a yellow cere, a long square banded tail, legs feathered to the toes, and long broad wings with seven spread
  fingers. Perches upright; its own soaring, flapping, stoop (wings
  folded), takeoff, landing, feeding (mantling) and death animations.
- flocks:eagle (entities/eagle.json, behaviour flocks.raptor, Raptor.cs): perches on the most prominent high point
  within 64 blocks, soars in circles on thermals 35-65 blocks up within a 200-block territory, glides between
  thermals, and hunts: it looks for prey (preyCodes: hares and mallards) well below it in the open, closes in at
  height until the prey is below it, then stoops, accelerating to 38 blocks/s with the talons forward only at the
  end, strikes, and walks onto the kill to feed on it (flying down or up to it if it fell off a ledge), or pulls up
  if the prey gets under cover. Ducks often see it coming (60% once it is within 14 blocks) and flush, and it may
  still catch one in the air; a duck caught on the water is carried off to a perch and eaten there. Every attack on
  a flock is a disturbance like any other flush, so an eagle hunting a pond can drive the ducks off it for a couple
  of days (lake wariness). Perches are on the highest block it can stand on (not on a plant on top of a tree). Flushes from its perch when
  approached; dodges and startles at missed shots. 10 health; drops poultry and feathers.
- Spawns alone on open high ground (minY 1.15, maxForest 0.6), at most two within 192 blocks, all year (species
  habitat "air"; no landing flocks, no drawn sky flocks).
- Sounds (sounds/eagle/): calls, hurt and death from recordings by Bubulcus (CC BY 3.0) and Lawrence Shove (British
  Library, CC BY-SA 4.0).
- The waterfowl's flight helpers (terrain look-ahead, steering, shot and player sensing) moved into a shared base,
  EntityBehaviorBird (BirdBehavior.cs).
- Creature items for the geese, mallards, herons and eagles (itemtypes/creature.json, like the game's own): they
  are in the creative inventory's creatures tab and place the bird where you right-click. Mallards, herons and eagles
  have proper names now.
- Fix: a lake a flock abandoned could still be offered as a landing spot from just outside the abandon radius.
- Tests: the eagle perches, soars and circles, closes in on hares 25 blocks off, stoops fast, kills one and feeds
  on it, follows its kill down a ledge, returns to a perch, and stoops on mallards on a pond, disturbing the flock; the creature items load and each places an existing bird.

## 0.16.0
The grey heron, and wading (the first new behaviour type of the roster):
- New model (tools/make_heron.py): slim grey body with black shoulder patches and pale back plumes, white neck with a
  line of black streaks, white head with the black stripe to the crest, a long yellow dagger bill, broad rounded wings
  with dark flight feathers, and freakishly long legs (~1 block; Dright's call: Vintage Story's still water is always a
  full block deep). The neck is in two segments for the heron's S. Its own animations: flight with the neck drawn back
  and the legs trailing far behind, a hunched rest, an alert stretch, a stalking walk, the strike, takeoff, landing and
  death (the body drops the length of its legs).
- flocks:heron (entities/heron.json): size 0.7 (1.75-block wingspan, real 1.55-1.95 m), 6 health. Wades (species
  habitat "wade"): stands on the bottom of water one block deep, mostly resting hunched, now and then stalking slowly
  through the shallows or striking; goes alert and backs away when approached; flushes from 14 blocks (22 to start
  backing off), hops off the bottom instead of running, flies slowly with deep beats and croaks. Comes down in other
  wadeable water a block away from any drop-off; one that ends up in deep water floats up and paddles back.
- Spawns on its own, usually alone, only where the water is exactly one block deep (/flocks residents counts the rest
  as "too deep to wade"). Doesn't arrive in landing flocks (species arriveInFlocks false); passes over rarely.
- Sounds (sounds/heron/): croaks for calls and takeoff, hurt and death, from a recording by Sharad Apte (CC BY-SA 4.0).
- New species data: habitat ("water" or "wade") and arriveInFlocks. The landing approach now counts only what stands
  higher than the landing spot as in the way (needed for spots on the bottom of shallow water).
- Engine tests: the test site's pond A has a 3-block rim of water one block deep, like a natural lake's shallows. New:
  heron settle (standing on the bottom, not swimming), 25 s of wading without leaving wadeable water, and a flush
  ending standing in wadeable water.

## 0.15.0
The mallard, the first bird of the roster (Phase 2):
- New model (tools/make_mallard.py, built from the greylag generator's pieces with duck proportions: plump body, short
  neck, round head, broad flat bill, short pointed tail with the drake's black curl, shorter pointed wings). Same bones
  as the greylag, so the same animations. Drake (green head, white collar, chestnut breast, grey flanks, black stern,
  blue speculum) and hen (mottled brown, dark eye stripe, orange bill with a dark saddle) textures on one layout; each
  mallard is one or the other at random.
- flocks:mallard (entities/mallard.json): size 0.47 (0.9-block wingspan; real mallards 0.8-1 m), 3 health, 1 poultry
  and ~7 feathers. Lets you a little closer than a goose (flee 12, takeoff 6), springs almost straight up off the water
  (0.15 s instead of a 0.55 s run), beats its wings twice as fast and cruises faster. Spawns on lakes and ponds of 25+
  surface blocks in a wider climate than the greylag, in groups of 3-9; resident most of the year.
- Passing mallard flocks (config/flocks-species/mallard.json): loose fast groups of 4-14, 30-80 blocks up, at dawn and
  dusk in an early spring and a late autumn passage. Landing flocks of 4-10.
- Sounds (sounds/duck/): single quacks and feeding mutters while idle, quack series as flight and alarm calls, hurt and
  death; cut from a female mallard (Aubrey John Williams, British Library, CC BY-SA 4.0), XC112828 (Jonathon Jongsma,
  CC BY-SA 3.0) and XC518422 (Marie-Lan Tay Pamart, CC BY-SA 4.0). See CREDITS.md. Wingbeats reuse the goose's,
  pitched up.
- New per-species settings: takeoffRunSeconds / takeoffRunSpeed (waterfowl behaviour) and minLakeSize (species data),
  used by spawning, landing flocks, the post-flush lake search and /flocks land.
- A goose or duck that finds an obstacle too close to climb over in time (a go-round low inside a ring of trees) now
  turns hard toward the most open side while climbing, instead of only pulling up and scraping it.
- Engine tests: mallard entity and species files load whole; a mallard landing flock and a mallard flush.

## 0.14.0
Birds are data (Phase 1, last part). No change in how the geese, cranes and hawks behave.
- Each bird is a file in config/flocks-species/ (any mod's domain, so other mods can add birds): drawn-flock shapes and
  size, speed and wingbeat rhythm, migration windows and hours, passing and landing flock sizes, calls, and for birds
  that land the entity they land as. goose.json, crane.json and hawk.json carry exactly the values that were in code.
- A landing bird's tuning stays in its entity file, as with vanilla animals: flocks.waterfowl attributes (now also
  species, takeoffWingsChance, takeoffCallChance) and entity sounds for takeoff and landing (takeoffsplash,
  takeoffwings, takeoffcall, landsplash).
- Resident spawning, landing flocks, `/flocks land [count] [species]` and `/flocks arrive [species]` work for any
  species that lands; each uses its own resident season and landing flock size.
- flocks.json: DisabledSpecies, a list of species codes that never migrate on their own (goose, crane and hawk keep
  their own switches).
- README rewritten for the current mod.
- Engine tests: 61 passed, 0 failed. New checks: the species files load, and the goose entity file loads whole (a bad
  field makes the game drop most of an entity file with only one log line, which is how a first attempt at this
  version broke: sound volume must be { avg: ... }, not a number). The test world now has a fixed seed, so the test
  site is always built on the same, low enough terrain.

## 0.13.0
Phase 1 AI: missed shots, lake wariness, vanilla-style player sensing.
- A missed arrow flushes the group like a hit. Ten times a second each goose looks for fast non-creatures (arrows,
  spears, thrown stones, other mods' shot) within 24 blocks whose path passes within 4 blocks of it in the next 0.3 s
  (an arrow covers several blocks between checks, so the path is tested, not the distance). They fly on away from the
  shooter (vanilla's EntityProjectile.FiredBy when present).
- Lake wariness (flocks-server.json: LakeWariness and its numbers): every flush disturbs the flock (+1), a kill a
  little more (+0.5). From 1 it is skittish: it flees and takes off from 1.8x further. At 2.5 it abandons the lake and
  rejoins the migration (it comes back next spring like any flock away), and no geese land on or spawn within 64
  blocks of that lake for 2 in-game days. It wears off at 1 per in-game day. A careful hunter gets about two geese
  from a flock before it leaves. `/flocks calm` (admin) calms every flock and forgets abandoned lakes;
  `/flocks residents` shows skittish flocks, abandoned lakes and spawns vetoed for them.
- Players are sensed the way vanilla animals sense them: the animalSeekingRange stat scales detection, sneaking on
  the ground cuts it to 60% (and the flush distance with it), and creative and spectator players go unnoticed, as with
  deer. Test flushes in survival.
- Fixed geese clipping trees on a steep glide-in: the obstacle check sampled every block (a thin wall crossed at an
  angle could fall between samples) and let a goose sit 0.1 below the line it needed, most of its clearance 10 blocks
  out. Now every half block, and a glide is aborted as soon as it is under the line.
- Engine tests: 58 passed, 0 failed (new: missed arrow, second disturbance, leaving and the abandoned lake, a
  landing flock avoiding it, a kill counted and its wariness).

## 0.12.1
Fixed geese circling forever after being flushed (found and verified with new in-engine tests):
- The "give up on a spot after 40 s" rule never worked: its timer reset every time a goose switched between gliding
  and flying, and if it did fire, the next spot was thrown away on the very next tick, so the goose searched for a
  lake every tick and never landed.
- Geese came in from wherever they happened to be and cruised at a height based only on distance, so at lakes with
  trees on the shore the line down was blocked and they never began to glide. They now pick a clear lane into their
  spot (the gentlest of 16 directions), fly out to it, follow it in and glide down at the angle it needs, up to 45 deg,
  slowing down to hold a steep one.
- Glides were aborted for trees the goose had already passed (the check measured the whole lane, not what was still
  ahead). In the test's tree-ringed pond, 3 of 6 geese aborted 3-6 glides each and two gave up and flew to another
  lake; now all 6 land in the pond within 19 s with no aborted glides.
- A spot is given up after 40 s, 3 aborted glides, or when there is no way in under 45 deg; after 3 given-up spots
  the flock rejoins the migration instead of circling.
- New tools/run_engine_tests.ps1 and tools/FlocksTest: runs the goose AI on a scratch dedicated server against a
  built test site (see DESIGN.md). Current result: 39 passed, 0 failed; longest flight 27 s (a landing flock from
  320 blocks), no wall hits, never inside terrain, managed memory flat over repeated cycles.

## 0.12.0
Landing and departing flocks are real geese the whole way (the drawn-flock swaps are gone):
- A landing flock appears as a V of geese up to 320 blocks out along its arrival heading, all flying at 13 blocks/s,
  and each goose peels off to its own spot and glides in. Needs loaded chunks out there and the lake 40-160 blocks
  from you, as before.
- A departing flock flies off as geese and is removed once 360 blocks from every player (3-4 px by then), and the
  flock is Away, ready to return next spring.
- While flying, a goose is simulated and sent to players out to 400 blocks instead of the vanilla 128 (per-entity
  SimulationRange, which the server also uses for tracking); clients draw it out to their view distance. On the
  water it goes back to 128, so resting geese cost no more than before.
- Geese are bigger: size 0.65, a 1.7-block wingspan (was 0.55, 1.4; real greylags are 1.5-1.8 m), with the hitbox
  scaled to match (0.7 x 0.6). The sky geese of passing flocks read the same size.
- Removed: the hand-off and landing-flock packets and the client code that drew them.

## 0.11.1
- Sky birds are drawn at one fixed, true size, so drawn and real geese match at a swap. The sky goose takes the landed
  goose's size from entities/greylag.json client.size (0.55, a 1.4-block wingspan; real greylags are 1.5-1.8 m):
  raise that one number to make both bigger. Cranes 2.2 and hawks 1.2 block wingspans (were all ~4.5x real size).
- Flocks now appear and fade at ~540 blocks (where a true-size goose is 2 px on a 1080p screen; was up to 1500).
- At a swap the birds' small drift around their slots fades out, so each drawn bird is exactly where its goose
  appears; after a departure swap it fades back in.

## 0.11.0
Flocks land as flocks and become geese (second half of flocks landing and leaving; new LandingFlocks.cs):
- On spring migration days each player gets up to one landing flock a day (scaled by the day's migration strength,
  at a goose flying hour). `/flocks arrive` sends one now, in any season, for testing.
- A landing needs open water 40-160 blocks from the player with no geese on it yet, in the resident season.
- The flock is the one away longest (it returns with whoever survived), else a replacement for a flock that was
  killed off, else a new one of 7-12 (FlockRegistry).
- Every client within 1600 blocks sees the same flock: the server fixes its path up front (from ~800 blocks out,
  cruising clear of all loaded terrain along the way, descending steeply enough to clear any hill before the lake)
  and clients draw it from the path and the time alone.
- 60 blocks short of the lake the drawn birds are swapped for real geese at the same spots, already flying at
  13 blocks/s toward their own spots on the water; they glide in, splash down and stay for the season.
- The drawn formation closes up to the real geese's spacing over the last 8 s; the birds stay sky-sized until the
  swap, so the size jump is visible (on purpose, for now).
- Autumn has no landings: geese out of their resident season would leave again at once.

## 0.10.1
- Natural geese actually spawn now. Vanilla's spawner rolls a type's runtime `chance` once every 0.5 s to decide
  whether that type is considered at all (read from the decompiled spawner), not per chunk. At 0.0003 geese were
  considered about once every 28 minutes, and then needed a full group's worth of water in one pass, so they almost
  never appeared (`/flocks residents` showed 0 spawn checks). Now 0.01, about every 50 s.
- No light limit any more: tryOnlySurface places geese on the lake bed (worldgen terrain height + 1), where deeper
  water was too dark for the old minLightLevel 6. tryOnlySurface already keeps them out of caves.
- Comments corrected: maxQuantity caps all loaded geese on the server (scaled per player), not per area.

## 0.10.0
Departing geese become a migrating flock instead of vanishing (first half of flocks landing and leaving):
- New FlockRegistry (server, saved with the world): every landed group of geese is a flock with an id, a living
  count, deaths, and a state (Landed, Away on migration, Lost when all are killed). Geese carry their flock id;
  vanilla group spawns, old saves and /flocks land groups are all picked up. `/flocks residents` shows the totals.
- Departure hand-off: departing geese (the resident season ends, a /flocks land goose's lifeHours, or nowhere to land
  after being flushed) climb away. Once their centre is 80 blocks from every player, the entities are removed and
  every client within 1600 blocks continues them as a drawn flock from exactly where they were, at their speed and
  heading, closing into a V and climbing to cruising height and species speed over ~10 s. The flock is marked Away.
- This also fixes departing geese freezing in mid-air: they were meant to despawn once no player was within 128
  blocks, but the server stops simulating entities at exactly that distance.
- Flushed geese with nowhere to land now rejoin the migration (hand-off) instead of searching on.
- Group sizes: natural resident groups and /flocks land are now 7-12 (were ~3-9 and 5-12). maxQuantity per player
  area 12 -> 24, so an area still holds about two groups.
- The drawn flock is ~4.5x the size of the real geese at the swap; left as is for now, on purpose.

## 0.9.1
Flying geese avoid terrain instead of flying into it (Waterfowl.cs):
- Every tick a flying goose scans the ground and tree tops 3 s ahead (36-40 blocks at cruise), down the middle and
  at each wingtip, and climbs early enough to keep its feet 1.5 blocks clear. Open water never counts.
- A climb too steep for its speed makes it pull up hard: up to 7 blocks/s climb, shedding speed (down to 1.2) so the
  climb angle is enough, beating hard (takeoff animation) all the way up.
- The body pitches nose-up with the climb angle (up to 63 deg), nose-down a little when descending (Pos.Roll, which
  for this model turns about the wing axis; synced and interpolated by the game).
- Each goose takes off along the clearest heading within 120 deg of "away", so it doesn't run straight at a bank.
- Under an overhanging crown it turns hard toward the most open side instead of climbing into it. If it still
  clips something, it stops pushing forward and climbs straight up.
- Glide-ins only start along a clear line, and break off into a go-around if a tree comes into the path. An approach
  that can't get in for 40 s picks another spot.
- A side-on simulation of these rules against tree lines and cliffs 6-24 blocks tall cleared them by at least 1.2
  blocks whenever the wall was 4+ blocks past the water's edge. Closer walls are what the heading choice avoids.

## 0.9.0
Landed geese fly properly (Waterfowl.cs, new LakeSearch.cs, greylag shape and entity):
- Takeoff is hurried: the group flushes a moment apart (0.1-0.6 s), each goose runs ~0.5 s across the water with a
  splash, deep fast wingbeats, body pitched up and feet pattering (new "takeoff" animation), then climbs out.
- Faster flight: 11-13.5 blocks/s cruise (entity attributes cruiseSpeedMin/Max), was 4.2.
- They don't vanish any more. The group picks somewhere to land: open water on the same lake at least 26 blocks from
  whatever scared them (the shooter, not the arrow) and 14 from the goose, near shore; else the nearest lake within
  160 blocks with 36+ surface blocks, preferring lakes that don't lie toward the threat; else they fly on and search
  again every 4 s. They glide in (fly-soar, then a new "land" flare with wings cupped and feet forward), splash down
  and skid to a stop. Spots keep 2 blocks of open water all round; a flight simulation put 90% of touchdowns within
  1.3 blocks of the spot (worst 3.6), and every spot 14-110 blocks off at any bearing was reached in under 23 s.
- Flight state is saved with the entity, so a goose that flies out of the loaded area carries on when it reloads.
- Geese leave the world only when departing for good (autumn, or a /flocks land test goose's lifeHours) or after
  ~1200 blocks without finding a lake, and only once no player is within 128 blocks.
- Death animation: the goose rolls onto its side, neck limp, legs loose (vanilla "die" on the dead control).
- flySeconds is gone from the waterfowl behaviour.

## 0.8.4
- Goose spawns are vetoed more than ShoreDistance (server config, default 20) blocks from shore. Shore means a solid
  non-water block at the water surface or one above. The check scans outward in 8 directions up to the limit and
  allows the spawn as soon as any direction hits land (at most 8 x ShoreDistance x 2 block reads; it never floods
  the lake).
- Floating geese more than 1.5 x ShoreDistance from shore (checked every 3 s, same 8-direction scan) are gently
  steered back toward the nearest land seen within 3 x ShoreDistance while drifting. Fleeing is unchanged.
- `/flocks residents` shows the new "too far from shore" veto count.

## 0.8.3
Landed geese (Waterfowl.cs), from a review of 0.4.2-0.8.0:
- Fleeing now works. The nearest player was looked up every 0.25 s but dropped on every tick in between, so a fleeing
  goose drifted and wandered on most ticks and paddled away only now and then. The lookup is now kept between checks,
  so the 7-block takeoff check also runs every tick.
- Geese stay on the water. A bank flush with the lake is only ~0.3 blocks above a floating goose (stepHeight 0.6), so
  the "turn away from the shore" collision check never fired and geese could paddle ashore and wander inland. They now
  look 0.8 blocks ahead and turn along the shore, and a goose already ashore heads back to water within 6 blocks.
- Taking-off geese no longer vanish mid-air in plain view (8 s after takeoff, ~40 blocks away). They keep flying
  until no player is within 48 blocks, or 40 s at most.
- Spectators no longer scare geese.

## 0.8.2
- /flocks land no longer puts geese right next to the player. It used the water nearest the player (from 6 blocks
  out), well inside the goose's fleeRange (14) and often its takeoffRange (7), so the group paddled off or took off
  at once. It now lands a compact group on the nearest water at least 20 blocks away (Landing.Clear), skipping
  ponds that have no such water, and searches up to 2000 columns per lake (was 600).

## 0.8.1
- Fixed a client crash ("Nullable object must have a value" in Animation.lerpKeyFrameElement) the first time a
  landed goose was animated. The game reads all three axes of any offset, rotation or stretch a keyframe element
  sets, and the greylag, crane and amphithere shapes set only some (e.g. rotationX alone). The generators now fill
  in the missing axes (0, or 1 for stretch) via complete_axes() in tools/make_greylag.py, and the shipped shapes are
  regenerated to match. The animations look the same.

## 0.8.0
- Sounds (assets/flocks/sounds; sources, authors and licenses in CREDITS.md, attribution in modinfo.json):
  - goose/honk1-6: greylag calls (xeno-canto XC436358, Joost van Bruggen, CC BY-SA 4.0).
  - goose/hurt and goose/death: greylag calls from the same recording, pitched up / down.
  - goose/flight1-3: Canada geese flyover (Extemporalist, CC0).
  - wings/flap1-2: mute swan wingbeats (XC434964, Joost van Bruggen, CC BY-SA 4.0).
  - crane/call1-5: common crane (Wikimalte, CC BY-SA 4.0); crane/call6: common crane (V. P. Volkov, CC0).
  - 19 short mono Ogg clips, about 390 KB in total.
- Landed geese (entities/greylag.json): idle honks (idleSoundChance 0.02, range 32, slight pitch variation), plus
  hurt and death sounds. On takeoff about half the birds play a wingbeat burst and about a third a flight call
  (Waterfowl.cs), so a group doesn't play one sound per bird.
- Flying visual flocks: every 6-15 s (geese) or 8-20 s (cranes) one random bird calls, positioned at that bird.
  Volume is 0.6 times the config value, fading out over 220 blocks, so high or distant flocks are faint or silent.
  Hawks stay silent. No landing sound: since 0.7.1 geese appear through vanilla spawning and don't land.
- Client config: FlockCallVolume (default 1, 0 = silent) for the flying flocks' calls.
- New CREDITS.md.

## 0.7.1
- Resident geese now spawn through vanilla's spawner, like wolves and fish. The 0.7.0 lake manager, the 40-goose
  server cap, the lake cooldowns and the fly-in arrivals are gone.
  - entities/greylag.json gets spawnconditions modelled on freshwater fish and wolves:
    - Both: inside still water at the surface (game:water-still-7, tryOnlySurface), groups of about 6
      (verynarrowgaussian avg 6 var 3), climate minTemp -8, maxTemp 26, minRain 0.3.
    - Runtime: group "passive", chance 0.0003, maxQuantity 12 (vanilla's per-player-area limit),
      MinDistanceToPlayer 32.
    - Worldgen: TriesPerChunk 0.03.
  - Vanilla spawnconditions don't know seasons or lake size. A server OnTrySpawnEntity handler vetoes goose
    spawns when ResidentGeese is off, outside the resident season (spring window to autumn window, all year near
    the equator), on lakes under MinLakeSize surface blocks, or with MaxGeeseNearby+ geese within NearbyRadius.
  - Autumn departure unchanged: residents check the season every 30 s and the group takes off toward the equator.
- Every goose is a resident except `/flocks land` test geese, which are now tagged and still leave after 6 game
  hours. Geese spawned by 0.7.0 (and untagged 0.6.0 test geese) count as residents.
- Server config flocks-server.json is now ResidentGeese, MinLakeSize (36), MaxGeeseNearby (30), NearbyRadius (96).
  Dropped: LakeChance, MinPerLake, MaxPerLake, MaxResidents, RespawnDelayDays, CheckSeconds, ScanRadius,
  ArriveByAir, MinPlayerDistance. Old keys left in an existing file are ignored.
- `/flocks residents` now shows spawn checks allowed and vetoed (by reason), loaded geese, and geese and season
  near each player. The `check` option is gone.

## 0.7.0
- Resident geese (server). Real flocks:greylag groups settle on some lakes near players from the start of the goose's
  spring window (seasonRel 0.03) until the start of its autumn window (0.62), with the same latitude shift as the
  migration planner. Within ~10 deg of the equator (no migration) they stay all year.
  - Lake sites: world-aligned chunk-centre columns (every 32 blocks) that are fresh water, in loaded chunks, within
    ScanRadius of a player, in a lake of MinLakeSize+ surface blocks, and that pass a chance seeded from the world seed
    and the site position (LakeChance). One site per lake: the lowest-keyed passing centre owns it.
  - Each check (CheckSeconds) counts a site's geese where they are, since they carry the site key. An empty in-season
    site gets MinPerLake-MaxPerLake geese. That happens only if no player is within MinPlayerDistance, the server cap
    (MaxResidents) allows it, and the site has been loaded for two checks in a row, so geese saved in the chunk get
    counted before any new ones are added. Geese stay saved with their chunk and come back when it reloads.
  - A group that disappears while its lake stays loaded (killed or scared off) rests the lake for RespawnDelayDays
    game days. After that it gets a new group. Cooldowns are saved with the world.
  - Arrival: new residents glide in from ~70 blocks out and ~30 blocks up (fly-flap, about 10 s), from the equator
    side, then settle on the water. If the approach is not loaded they appear on the water instead.
  - Residents don't expire after 6 hours (the /flocks land test geese still do). In autumn every resident group takes
    off toward the equator and despawns. Takeoff, flee and the spear/melee dodge are unchanged from 0.6.0.
- Server config ModConfig/flocks-server.json: ResidentGeese, LakeChance, MinPerLake, MaxPerLake, MaxResidents,
  RespawnDelayDays, CheckSeconds, ScanRadius, MinLakeSize, ArriveByAir, MinPlayerDistance.
- `/flocks residents [check]` (controlserver, works from the server console): known lake sites with size, status,
  goose count, season and cooldown; `check` runs a check immediately.
- `/flocks send`, `schedule` and `land` still need a player; the root command no longer does.
- Client-only visual migration flocks are unchanged.

## 0.6.0
- **The mod must now be installed on the server as well as every client** (modinfo side Universal, required on both).
  Landed geese are real server entities. Flying migration flocks are still client-only visuals.
- New entity `flocks:greylag` (assets/flocks/entities/greylag.json): a huntable greylag goose using the goose shape
  and its swim, idle-stand, walk and fly-flap animations. 4 HP. When killed it leaves a carcass to harvest (vanilla
  harvestable) for 1.5-2.5 raw poultry (game:poultry-raw) and 12-16 feathers (game:feather), so geese are a reliable
  wild feather source. The carcass decays after 48 h. No breeding, no natural spawning yet.
- New behaviour `flocks.waterfowl` (Waterfowl.cs, server side):
  - Floats at the water surface and drifts slowly. It turns away when it bumps the shore.
  - Paddles away from a player within 14 blocks.
  - Takes off within 7 blocks, together with the rest of its group: a fly-flap climb away, then it despawns after
    8 s. Without a player nearby it does the same after 6 game hours on the water.
  - Damage: arrows and other ranged projectiles (thrown stones, modded shot) hurt it normally. Thrown spears and
    any melee hit (spear, sword, fist) do no damage; the goose dodges and the group takes off, much as fish slip
    spears. Being hit by anything scares the rest of the group into the air.
  - Vanilla's land-creature AI tasks are not used: the pathfinder avoids water and getoutofwater would walk the
    birds ashore. Movement is set directly and vanilla controlledphysics does the rest.
- Goose shape gains a walk animation (waddle). Flight poses are unchanged.
- `/flocks land [count]` (server, controlserver privilege): finds the nearest fresh-water surface patch of at least
  36 blocks within 64 blocks (6+ blocks away) and spawns 5-12 geese on it (or [count], max 30). Not tied to the
  migration planner yet.
- Not yet checked in game; server load smoke-tested only.

## 0.5.1
- Ground poses for a later landing feature. They are art only: nothing in the mod plays them yet, and the flying
  birds look and animate exactly as in 0.5.0.
  - Crane: idle-stand (wings folded along the back, neck up in a shallow S, legs straight down, toes forward) and a
    simple walk (legs swing alternately about the hip, small head bob).
  - Goose: swim (wings folded, body low, neck upright, legs left tucked so the feet stay under the waterline; slow
    bob) and idle-stand (legs swung straight down).
  - Wings fold by rotating the existing wing planes, so the crane needs no new elements. The goose gets two leg
    elements (LegL/LegR) that borrow the feet's UVs; in flight they lie tucked inside the body. The goose texture
    and its UV layout are unchanged (make_greylag.py leaves the borrowed legs out of the layout stamp).
- Previews: tools/preview_crane.py also writes crane_ground_preview.png (stand and walk on grass);
  tools/preview_greylag.py writes greylag_float_preview.png (swimming on water) and greylag_stand_preview.png.
- DESIGN.md: landing design (not implemented).

## 0.5.0
- New species: common crane (Grus grus), our own low-poly model in the goose's style, generated by tools/make_crane.py
  into shapes/entity/bird/crane.json and textures/entity/bird/crane.png (64x112, 2 px per model unit like the goose).
  Neck held straight out, long legs trailing past a short tail, long broad wings with six spread finger primaries,
  bushy dark tertials over the tail. Grey body, black-and-white head and neck stripe, small red crown, black flight
  feathers. fly-flap is slower and deeper than the goose's (arm +44/-40 deg); fly-soar is a flat glide.
  tools/preview_crane.py renders tools/crane_preview.png plus crane_preview_top/side/34.png. Not yet checked in game.
- Crane flight: V's of 7-25, 15-19 blocks/s, 0.8 s wingbeats (goose 0.5 s), 6-12 beats between 1-2 glides of 2.4 s,
  75-140 blocks above ground (geese 60-120). Migrates spring 0.02-0.18 and autumn 0.55-0.75 (earlier than geese),
  08:00-17:00, up to 2 flocks a day.
- Species now carry their own altitude range (AltitudeMin/Max); goose and hawk keep 60-120.
- Config: Geese, Cranes, Hawks (default true). Off stops that species migrating on its own; /flocks send still works,
  and /flocks schedule shows it as off.
- `/flocks send [count] [goose|crane|hawk]`; `/flocks schedule` lists the crane's migration strength and flocks.

## 0.4.2
- Amphithere model assets (no gameplay yet): shapes/entity/amphithere.json (74 cuboid elements, fly-flap animation,
  48 frames) and textures/entity/amphithere.png. Generated by tools/make_amphithere.py; tools/preview_amphithere.py
  renders previews. Not used by any entity, spawn or renderer yet, and not checked in game.
- Config: FantasyCreatures (default false), reserved for the amphithere; currently unused.

## 0.4.1
- Terrain clearance: every 0.3 s a flock samples the ground (rain map, so trees count) from the tail of its formation to
  320 blocks ahead, across its full width. It climbs (up to 6 blocks/s) to keep the lowest bird 35 blocks above it,
  starting just early enough for the climb rate, then sinks back to cruising height gently (1.5 blocks/s). A flock that
  starts over known high ground starts above it. Unloaded chunks are skipped (too far away to be drawn anyway).
- Gentle turns: the heading drifts on two slow sines (~12 deg over ~60 s, ~5 deg over ~23 s) around the migration
  heading; the formation turns with it. Birds bank into turns and lift their nose while climbing (both slightly
  exaggerated to read from the ground), and each weaves +-3 deg off the flock heading.

## 0.4.0
- Automatic migration. Each in-game day is planned from the world seed and day number (same plan on every reload):
  per species, a migration strength 0..1 from the season (sine over each window, peak mid-window), times flocks per peak
  day, gives that day's flocks, each with a time in the species' flying hours, a size, an altitude, and a heading
  (spring poleward, autumn toward the equator, +-20 deg). None within ~10 deg of the equator; further from it autumn
  starts earlier and spring later (up to ~3% of a year).
  - Goose: spring 0.03-0.22, autumn 0.62-0.85, 06:30-10:00 and 15:30-19:00, up to 3 V's a day of 7-25 birds.
  - Hawk: autumn 0.55-0.72, lighter spring 0.08-0.22, 10:00-15:00, up to 2 loose groups a day of 3-10.
- Storms hold flocks back: storm-strength wind (>= 0.85) or a downpour (precipitation >= 0.8) at the player. Ordinary
  rain and breezes don't. A held flock (storm, or another flock still overhead) goes when it clears, or is dropped after
  3 game hours. Flocks whose time passed while you were away or asleep don't fly.
- `/flocks schedule` shows the day, season, each species' migration strength, and today's flocks with their status.
- Hawks fly as a loose scattered group instead of a V.
- Client config ModConfig/flocks.json: AutoMigration, FlocksPerDayMultiplier, StormWind, StormPrecipitation,
  MaxDelayHours, ForceMigration (testing: every day is peak autumn), LogSchedule. Read at game start.
- The per-day season log line is replaced by the day's plan.

## 0.3.1
- Goose remodelled in the vanilla hawk's style: wings, tail and wingtips are flat single planes with cut-out feather edges
  (notched secondaries, pointed hand with slightly separated primaries, rounded tail feathers), a thin leading-edge strip
  on each wing, and a second plane tilted 5 deg under the inner wing for camber. Thicker neck and head.
- New 64x80 texture at 2 px per model unit (was 32x32 at 1), painted per face from the bird's model coordinates with
  edge shading baked in: scalloped brown back, barred flanks, pale belly, white vent, pale grey forewing, dark primaries.
- Renderer now draws double-sided with alpha cut-out (culling off, AlphaTest 0.05), as vanilla's entity pass does;
  without it single-plane wings vanish from one side and feather cut-outs draw solid. Applies to the hawk too.
- tools/make_greylag.py keeps an existing texture unless run with --texture, and warns if the UV layout changed;
  tools/greylag_uv_guide.png labels every face for repainting by hand; tools/preview_greylag.py renders the bird.

## 0.3.0
- New species: greylag goose, our own low-poly model (shape, 32x32 texture, fly-flap and fly-soar animations), generated
  by tools/make_greylag.py into Flocks/assets/flocks/. Grey-brown body, pale grey forewing (the greylag field mark),
  dark primaries, white vent, orange bill. Four-key wingbeat with the hand lagging the arm; slight dihedral when gliding.
- Species are now data (Species.cs): model candidates, scale, speed, wingbeat and soar timing, flap/glide counts.
  Goose: scale 2.5, 16-21 blocks/s, 0.5 s wingbeats, 8-16 beats between short glides. Hawk keeps 0.2.6 behaviour.
- `/flocks send [count] [goose|hawk]`; goose is the default now.
- V spacing scales with each species' wingspan (back 0.55 span, out 0.8 span per rank) so wingtips don't overlap.
- The release zip now carries an assets/ folder.

## 0.2.6
- Fix: flocks vanished while still clearly visible. They were removed a fixed ~210 blocks past you, where a hawk is still
  ~20-30 px wide (the game draws out to 3000 blocks; nothing else culls them). Flocks now enter and leave at the distance
  where one bird is ~2 px tall, worked out from the baked wingspan, BirdScale, window height and FOV
  (clamped 300-1500 blocks), so they shrink to specks instead of popping. The chat reply shows that distance.

## 0.2.5
- Smoother wings. 0.2.4 baked 10 flap poses shown at 10 fps with straight-line motion between the hawk's three keyframes,
  and switched flap/soar on a single frame. Now: 48 flap poses and 32 soar poses (soar plays as a loop instead of one
  frozen pose), keyframes joined by a cyclic Catmull-Rom spline, and 12 blend poses so each bird eases between flapping
  and gliding over 0.4 s. Each bird flaps 3-7 beats (0.8 s each), glides 1-3 soar loops (1.6 s each), then repeats.

## 0.2.4
- Fix: birds rendered white. The atlas prefixes `textures/` itself, so passing `game:textures/...png` became
  `game:textures/textures/...` (client-main.log 23:14:23 "Texture asset ... not found") and the unknown texture was used.
  The atlas now gets the bare shape path; only the existence check uses the full file path.
- Fix: birds faced sideways. The hawk shape's head points to -X, so the yaw is now atan2(dir) + 90 deg.
- Fix: flocks kept moving while the game was paused (render frames still tick); movement freezes when paused.
- Faster flight: 18-24 blocks/s (was 10-14). Flocks now enter ~190 blocks out and despawn ~210 past to keep similar screen time.

## 0.2.3
- Fix: `/flocks send` threw NullReferenceException on the server (server-main.log 23:07:41, FlocksModSystem.cs:54).
  A 0.2.1 edit put the client spawn code into the server command handler, so the server touched client-only meshes.
  The server handler now only relays a packet to the caller's client; `.flocks send` runs the spawn locally.

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



