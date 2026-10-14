# Changelog

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



