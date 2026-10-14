# Changelog

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



