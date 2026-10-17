# Handoff

Where Flocks stands and what comes next, for whoever picks it up (a new session, or Dright after a break).
Last updated 2026-10-16. Committed through 0.20.2: localisation (dead names, command texts), Butchering carcasses
for the goose, heron, eagle and gull (they work in game; Dright hasn't looked at every one), meat by real weight, the
eagle's perch landing. 0.20.1 was Biomes support (Hydromancerx's patch). Before that, 0.19.1 and
0.20.0 went in as one commit, both tried in game by Dright:
0.19.1 removed the hawk, made the eagle rest 1200 s after a meal like vanilla predators and let thrown spears fly past
birds; 0.20.0 added the tawny owl and the wood mouse and fixed crows, owls and mice spawning. DESIGN.md has the
reasoning behind each piece; CHANGELOG.md has the per-version detail.

Agreed next (Dright): prepare a formal release on the ModDB once the owl and mouse are properly tested: localisation
of everything, including corpses (dead entity names), then compatibility with popular mods such as Butchery. Dright is
fine with it being published as AI-assisted.

## State

Vintage Story 1.22.7 mod, C# (.NET), server and client. Phase 1 (the AI) is done; Phase 2, the roster, is under way.

| Bird | How it lives | Status |
|---|---|---|
| Greylag goose | floats on lakes; landing flocks on migration; flushes, lake wariness | done |
| Mallard | as the goose, smaller ponds; springs straight up | done (0.15.0) |
| Grey heron | wades water one block deep; stalks, strikes; alone | done (0.16.0) |
| Golden eagle | perches, soars on thermals, stoops on hares and mallards | done (0.17.0) |
| Herring gull | floats on the sea along the coast, all year; bold; wheels, loafing flights | done (0.18.0) |
| Common crane | drawn sky flocks only | done |
| Hawk | was a drawn sky flock with vanilla's unused hawk shape | removed (0.19.1) |
| Carrion crow | feeds on open ground, perches and roosts in trees, raids crops; the straw dummy is its scarecrow | done (0.19.0) |
| Tawny owl | roosts hidden in a crown by day; hunts mice at night from the open (crown edges, posts, walls); hoots | done (0.20.0) |
| Wood mouse (not a bird) | the owl's prey; vanilla AI; rare, despawns away from players | done (0.20.0) |


Every bird is about half again its real size, on purpose (Dright: true-size birds looked tiny next to the game's
animals). New birds should follow suit; if gameplay needs a dimension (wading depth), make the bird bigger rather than
stretch a body part. Current sizes: goose 0.98, mallard 0.75, heron 1.05, eagle 1.1, gull 0.8, crow 0.75, owl 0.75
(entity `client.size`). The mouse is about twice life size (0.4) so it shows in the grass; not yet seen by Dright.

Dright's general rules: birds should behave like the real thing, but not where realism turns into a chore for the
player (the scarecrow never stops working); and they should follow vanilla's mechanics where vanilla has them (crows
kill crops like hares do).

## How the code fits together

- **Species data** (`assets/flocks/config/flocks-species/*.json`, `Species.cs`): one file per bird. It holds sky
  drawing, migration windows, flock sizes and calls. It also has `landedEntity` for birds that exist as entities,
  `habitat` (`"water"`, `"sea"`, `"wade"`, `"air"`, `"ground"`, `"wood"`), `arriveInFlocks`, `yearRound` (no resident season; sky groups any
  day, any direction) and `warinessFactor` (scales what a flush or kill adds to the flock's wariness).
- **Entities** (`assets/flocks/entities/*.json`): the real birds. Their behaviour classes:
  - `flocks.waterfowl` (`Waterfowl.cs`): goose, mallard, heron and gull. It covers the water/wade states and a flight
    state machine with landing approaches and go-arounds. Gull-only extras are attributes, off by default:
    `wheelSecondsMin/Max`, `loafHoursMin/Max`, `soarShare`.
  - `flocks.raptor` (`Raptor.cs`): the eagle's perch/soar/stoop/feed.
  - `flocks.corvid` (`Corvid.cs`): the crow's forage/perch/roost, flights between them, and crop raiding. A crow eats
    a crop through the farmland's own `ConsumeOnePortion`, exactly as hares do: dead crop, seed kept (Dright). Crops are
    found through vanilla's POIRegistry (VSEssentials, referenced in the csproj); its WalkPois leaves out the last
    chunk column of its range, so the crow asks for a chunk more and filters. The scarecrow check is `Scarecrows.cs`
    (standing `game:strawdummy` entities, horizontal radius from the server config).
  - `flocks.owl` (`Owl.cs`): the owl's roost/perch/pounce/feed and its night calls. Trees: ordinary leaves have no
    collision box, but the branches inside a crown are branchy leaves, which do; they are drawn as leaf blocks though,
    so an owl on one is hidden. The roost is the first solid block under a column's leaves (PerchBelowLeaves), reached
    by dropping straight down through the foliage, and left by climbing straight up. Hunting perches are in the open
    (Dright: hidden by day, visible at night): on top of a crown's edge, holding its height on the leaves as crows do,
    fence posts, walls.
  - The mouse (`entities/mouse.json`) has no code: vanilla's taskai, a low runtime cap and the despawn behaviour.
  - All four bird behaviours sit on `EntityBehaviorBird` (`BirdBehavior.cs`), which has the shared flight pieces: terrain look-ahead,
    steering, dodging shots, and sensing players the way vanilla animals do.
- **Water**: every lake lookup takes the species' liquid (`Species.Liquid`: `"water"`, or `"saltwater"` for the sea),
  so lakes and the sea never mix (`LakeSearch.cs`, `Residents.cs`, `Landing.cs`).
- **Flocks and memory** (`FlockRegistry.cs`): saved with the world. Every bird carries a flock id. Deaths are counted,
  and lake wariness builds up per flock: a flush adds 1, a kill 0.5 (times the species' warinessFactor), and at 2.5
  the flock abandons the place for 2 days.
- **Spawning**: vanilla spawn conditions in the entity file, plus vetoes in `Residents.cs` (season, lake size,
  crowding, abandoned lakes). Landing flocks come in on the migration schedule (`LandingFlocks.cs`).
- **Sky** (`FlockRenderer.cs`, `Migration.cs`): flocks that only pass over are drawn by the client, at the same size
  as the entity. Sea birds' groups are drawn only when there is sea within 96 blocks of the player. Several flocks can
  be up at once, kept apart in height (FlockRenderer.Separation); only storms hold flocks back.
- **Handbook**: each creature item's page text is the lang key `flocks:item-handbooktext-flocks:creature-<bird>`,
  written out in full (a key with a colon is taken as it is). The "Birds" guide page is
  `assets/flocks/config/handbook/birds.json` with its text in `lang/en.json`. New birds get a paragraph and a link
  from the Birds page. Our lang file also overrides vanilla's straw dummy text (`game:item-handbooktext-strawdummy`);
  the engine test checks the override wins.
- **Models**: generated by Python, `tools/make_<bird>.py` (and `make_mouse.py`); `tools/preview_model.py` renders any
  of them in chosen animation frames, so poses can be checked without the game.
  A keyframe at or past an animation's quantityframes crashes the client (not the server); the generators refuse it
  and an engine-test check reads every shape for it. The shared pieces live in `make_greylag.py`. Pass
  `--texture` to repaint (this overwrites hand edits). Every animation keyframe must set all three axes of a group,
  or the game crashes; the generators handle that.
- **Creature items** (`assets/flocks/itemtypes/creature.json`): creative-tab items that place the birds. Add each new
  entity bird to its states, shapes and textures, and give it a name in `lang/en.json`.
- **Sounds**: recordings from Wikimedia Commons, xeno-canto and the British Library (CC licences), trimmed and
  converted to .ogg (mono 44.1 kHz, mean about -19 dB, peak under -1 dB). Credit every source in CREDITS.md and in
  modinfo.json's description. When downloading, use the User-Agent `FlocksModSoundFetch/1.0` and never personal
  details. Commons rate-limits quick runs of downloads (HTTP 429): pause between files.

## Building, testing, releasing

- Build:
  `dotnet build Flocks/Flocks.csproj -c Release '-p:VINTAGE_STORY=%USERPROFILE%\agent-tools\vsref1227' -warnaserror`
- Engine tests: `tools/run_engine_tests.ps1`. This is a headless server with no players, seed 424242, and takes about
  9 minutes. The results are in `%TEMP%\flocks-enginetest\Logs\server-main.log`; grep it for `FAIL` and `SUMMARY`.
  At 0.20.0: 179 checks, all passing (the last full run failed only the mouse catch, which then passed twice in owl-only runs once the test let the owl try again after a miss). To run only some steps, set FLOCKS_TEST_ONLY to part of their names first (e.g.
  `$env:FLOCKS_TEST_ONLY = 'owl'`; about 2 minutes); the site is built either way.
  - The test mod (`tools/FlocksTest/`) builds a site at spawn: a slab, pond A with a one-block-deep rim, pond B inside
    a ring of 8-high "trees", pond C of salt water (the sea), two soil fields with a 5x5 bed of flax each, a small oak, an owl
    wood (two oaks with branchy-leaf branches in a soil clearing), a 14-high wall and a
    12-high perch pillar. It then runs
    scripted steps, sampling every bird's flight for collisions, terrain, stalls and turning.
  - With no players, the game runs no physics, so the harness steps physics itself for the test birds.
  - The tests can't judge looks (sizes, models, animations). Ask Dright for in-game screenshots.
- Release: bump the version in `Flocks/modinfo.json` and update CHANGELOG, DESIGN and README. Build the zip with
  `./build_zip.ps1` (it lands in `dist/`). Dright tries it in game, and only then does it get committed: one commit
  per version on master. Don't push unless asked.
- Vanilla reference: game assets are in `C:\Steam\steamapps\common\Vintagestory\assets`. For engine internals,
  decompile `VintagestoryLib.dll`, `VintagestoryAPI.dll` or `Mods/VSSurvivalMod.dll` / `Mods/VSEssentials.dll` with
  ilspycmd (install it with `dotnet tool install ilspycmd --tool-path <scratch folder>`).

## Next

The roster planned so far is done. Open ideas, none agreed with Dright yet:
- Crows: mobbing the eagle (crows chase a soaring or perched eagle and it moves off); gathering at carcasses and the
  eagle's kills; following players who till or harvest. Rooks (bigger flocks, rookeries) as a variant.
  Passing crow groups fly in random directions (Dright: fine for now); an option is a fixed roost per area, with
  morning groups flying out from it and evening groups back.
- Gulls: standing on beaches and shores (a "standing ashore" state the waterfowl don't have yet); scavenging.
- Goslings and eggs; domestication.

## Compatibility

- Biomes (modid biomes, server only; tested against 2.3.1, zip kept in %USERPROFILE%\agent-tools\vsmods):
  patches/biomes.json gives each animal its realms (Biomes itself then vets natural spawning), and BiomesCompat.cs
  makes landing flocks and drawn passing flocks follow them, through Biomes' public API (Biomes.Api.ExternalData,
  EntityBiomeData) by reflection. Engine tests: `-ExtraMods <zip>` loads other mods; `FLOCKS_TEST_ONLY=biomes` runs
  the Biomes step. Not yet seen in game: the sky flocks staying away outside a species' realm.
- Butchering (modid butchering; tested against 1.14.3, zip in the same folder): the goose, heron, eagle and gull are
  butcherable (patches/butchering.json adds Butchering's "butcherable" behaviour, pointing at the carcass item
  itemtypes/butchering/deadbird.json, which is disabled unless Butchering is installed: its item class only exists
  then). Both written by tools/make_carcasses.py. The other birds and the mouse keep vanilla's field harvest (Dright).
  The carcass shapes are our models posed (dead, hanging head down by the feet with the feet at y 10, like
  Butchering's chicken) and a plucked texture each; the heron's has no legs. They work in game (Dright); not every
  carcass has been looked at on the hook, and the held/slot/ground transforms are guesses (tune with .tfedit).
- JSON patches can't reach a file whose first token is a comment: keep comments inside the braces.

## Loose ends

- Herons once (1 run in many) settled at the edge of pond A's one-block-deep rim with their middle over the deep
  part: supported by the rim, but the "heron flush" check wants the column under the middle wadeable. Harmless to
  look at; if it recurs, have a wading bird nudge itself onto the wadeable column after landing.
- Crows never spawned on their own in 0.19.0: their spawn block codes lacked the `game:` domain. Fixed in 0.20.0; any
  new entity's spawnconditions must name blocks as `game:...`. Watch the server log for "none of these blocks exists".
- The owl in game (Dright, 0.20.0): hard to see; the night hooting is nice. Left as it is for now. If it is taken up
  again, ideas were: out at dusk while there is still light, lower perches near eye level (fence posts, the lowest
  edge of a crown), a little bigger and paler in front; or roosting visibly by day now and then.
- Goose wings on the water sometimes sat at odd angles (Dright, 0.20.0); it stopped and couldn't be reproduced.
  Likely the folded swim pose and a flight pose (land, takeoff) averaging into an in-between wing angle while
  animations switch. The client takes each animation's blend settings from the entity file by code, so those do
  apply. If it recurs: a screenshot and what the goose was doing just before.

- Crows not fleeing a survival player was reported in 0.19.0 testing; in the engine test they flee a stand-in
  player (EntityBehaviorBird.TestPlayers) at 10 blocks. `/flocks near` shows in game what each bird senses; check it
  next to crows if it recurs.
- The creature-item icons and the handbook pages haven't been checked in game. The game's `.tfedit` adjusts icons
  live; then copy the numbers into `itemtypes/creature.json`.
- The eagle steps of the engine tests are flaky; nothing in 0.18.0 or 0.19.0 touches the eagle. Seen so far: the
  ducks all escaping (the checks allow it); the eagle no longer ticking after the "eagle hunts mallards" step teleports
  it (state time stuck at 0, it falls to the slab), so "the stoop ended" and "the flock was disturbed" fail; and once
  the eagle "perched" 2 blocks below and beside the wall top it was aiming for (Perch at -8.6,12.9 against a perch at
  -10,15), which may be a real bug: an eagle settling against the side of a wall. Worth a look in game and in
  Raptor.ToPerch.
