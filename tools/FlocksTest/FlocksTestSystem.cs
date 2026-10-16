using System;
using System.Collections.Generic;
using System.Linq;
using Flocks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace FlocksTest
{
    /// <summary>
    /// In-engine tests for the Flocks goose AI, run on a scratch dedicated server with no players (see
    /// tools/run_engine_tests.ps1). Builds a test site in the sky above the spawn point: a 4-thick stone slab with
    /// two ponds (banks flush with the water, as in natural lakes) and a 14-high wall east of pond A. Every goose is
    /// AlwaysActive so its behaviours tick with nobody around, and since the server only runs physics for entities a
    /// player is tracking (PhysicsManager skips IsTracked == 0), the test runs the test geese's physics itself at the
    /// server's 30 ticks a second while nobody tracks them. Then: geese settle; a flush toward the wall; a departure; a
    /// landing flock from far out over natural terrain; repeated cycles with memory and entity counts. Each step logs
    /// PASS/FAIL lines with the measurements behind them, and the server shuts down at the end.
    /// </summary>
    public class FlocksTestSystem : ModSystem
    {
        private ICoreServerAPI sapi;
        private Vec3d c;                // site centre (x, slab top y, z)
        private int y0;                 // slab top = pond water surface block
        private Entity threat;
        private readonly List<Entity> geese = new();
        private readonly List<string> summary = new();
        private int pass, fail;

        // per-goose flight stats, sampled every 100 ms
        private readonly Dictionary<long, Stats> stats = new();
        private class Stats
        {
            public int Collided, InTerrain, Samples, Still, GoArounds;
            public double MaxAlt, MaxSpeed, MaxCommanded, LastT, FlightSeconds;
            public Vec3d Last; public string LastPhase = "Water";
            public bool LoggedHit, LoggedLow;
        }

        private string stepName;
        private double stepStart;
        private Func<bool> stepDone;
        private Action stepCheck;
        private double stepTimeout;
        private readonly Queue<(string name, Action start, Func<bool> done, Action check, double timeout)> steps = new();

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            // nobody is online, so keep every goose ticking (also the ones the mod spawns for a landing flock)
            sapi.Event.OnEntitySpawn += e =>
            {
                if (e.Code?.Domain != "flocks") return;
                e.AlwaysActive = true; e.State = EnumEntityState.Active;
            };
            sapi.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => sapi.Event.RegisterCallback(_ => LoadSite(), 2000));
        }

        private void Log(string msg) => sapi.Logger.Notification("[FlocksTest] " + msg);
        private void Check(bool ok, string what)
        {
            if (ok) pass++; else fail++;
            string line = (ok ? "PASS " : "FAIL ") + stepName + ": " + what;
            summary.Add(line); Log(line);
        }

        // ------------------------------------------------------------------------------------------------ site

        private void LoadSite()
        {
            var sp = sapi.World.DefaultSpawnPosition.XYZ;
            int cx = (int)sp.X / 32, cz = (int)sp.Z / 32, r = 11, left = 0;
            for (int x = cx - r; x <= cx + r; x++)
                for (int z = cz - r; z <= cz + r; z++)
                {
                    left++;
                    sapi.WorldManager.LoadChunkColumnPriority(x, z, new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => { if (--left == 0) BuildSite(sp); } });
                }
            Log($"loading {left} chunk columns around spawn {sp.X:0},{sp.Z:0}");
        }

        private void BuildSite(Vec3d sp)
        {
            var ba = sapi.World.BlockAccessor;
            int x0 = (int)sp.X, z0 = (int)sp.Z, half = 100, top = 0;
            for (int x = x0 - half; x <= x0 + half; x += 4)
                for (int z = z0 - half; z <= z0 + half; z += 4) top = Math.Max(top, ba.GetRainMapHeightAt(x, z));
            y0 = Math.Min(top + 12, ba.MapSizeY - 60);
            if (top + 12 > ba.MapSizeY - 60) Log($"WARNING: terrain under the test site reaches {top}, so the slab is buried; the world seed in run_engine_tests.ps1 should be flatter here");
            c = new Vec3d(x0 + 0.5, y0, z0 + 0.5);
            int stone = sapi.World.GetBlock(new AssetLocation("game", "rock-granite")).BlockId;
            int water = sapi.World.GetBlock(new AssetLocation("game", "water-still-7")).BlockId;
            var bulk = sapi.World.GetBlockAccessorBulkUpdate(true, false);
            var pos = new BlockPos(0, 0, 0);
            for (int x = x0 - half; x <= x0 + half; x++)
                for (int z = z0 - half; z <= z0 + half; z++)
                {
                    bool pond = InPondA(x, z) || InPondB(x, z);
                    for (int y = y0 - 3; y <= y0; y++)
                    {
                        pos.Set(x, y, z);
                        bool deep = !InPondARim(x, z) || y == y0;   // pond A's rim is one block deep (shallows for waders)
                        if (pond && y > y0 - 3 && deep) { bulk.SetBlock(0, pos); bulk.SetBlock(water, pos, BlockLayersAccess.Fluid); }
                        else bulk.SetBlock(stone, pos);
                    }
                    if (x >= x0 - 12 && x <= x0 - 10 && Math.Abs(z - z0) <= 30)
                        for (int y = y0 + 1; y <= y0 + 14; y++) { pos.Set(x, y, z); bulk.SetBlock(stone, pos); }
                    // a ring of 8-high "trees" two blocks outside pond B: every way in has to drop over them
                    int bx = Math.Abs(x - (x0 + 50)), bz = Math.Abs(z - (z0 + 30));
                    if (Math.Max(bx, bz) == 17)
                        for (int y = y0 + 1; y <= y0 + 8; y++) { pos.Set(x, y, z); bulk.SetBlock(stone, pos); }
                }
            bulk.Commit();
            Log($"site built: slab top y={y0} (terrain max {top}); rain map at pond A {ba.GetRainMapHeightAt(x0 - 40, z0)}, " +
                $"slab {ba.GetRainMapHeightAt(x0, z0 + 60)}, wall {ba.GetRainMapHeightAt(x0 - 11, z0)}");
            // the goose entity file parsed whole (a bad field makes the game drop most of it, silently but for one log line)
            var gt = sapi.World.GetEntityType(new AssetLocation("flocks", "greylag"));
            Check(gt?.Sounds != null && gt.Sounds.ContainsKey("takeoffwings") && gt.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.waterfowl") == true,
                "greylag entity file loaded whole (sounds and flocks.waterfowl behaviour present)");
            var ht = sapi.World.GetEntityType(new AssetLocation("flocks", "heron"));
            Check(ht?.Sounds != null && ht.Sounds.ContainsKey("takeoffcall") && ht.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.waterfowl") == true,
                "heron entity file loaded whole");
            var mt = sapi.World.GetEntityType(new AssetLocation("flocks", "mallard"));
            Check(mt?.Sounds != null && mt.Sounds.ContainsKey("takeoffwings") && mt.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.waterfowl") == true,
                "mallard entity file loaded whole (sounds and flocks.waterfowl behaviour present)");
            var spc = sapi.ModLoader.GetModSystem<FlocksModSystem>().SpeciesByCode;
            Check(spc.ContainsKey("goose") && spc["goose"].Lands && spc["goose"].LandingMin == 7 && spc.ContainsKey("crane") && spc.ContainsKey("hawk")
                  && spc.ContainsKey("mallard") && spc["mallard"].MinLakeSize == 25 && spc.ContainsKey("heron") && spc["heron"].Wades && !spc["heron"].ArriveInFlocks,
                $"species data loaded: {string.Join(", ", spc.Keys)}");
            Check(top + 12 <= ba.MapSizeY - 60, $"test site above the terrain (terrain max {top}, slab {y0})");
            Check(ba.GetRainMapHeightAt(x0 - 40, z0) == y0 && ba.GetRainMapHeightAt(x0 - 11, z0) == y0 + 14 && ba.GetRainMapHeightAt(x0 + 50 - 17, z0 + 30) == y0 + 8,
                "rain map follows the built site");

            var chickenType = sapi.World.GetEntityType(new AssetLocation("game", "chicken-hen"));
            threat = sapi.World.ClassRegistry.CreateEntity(chickenType);
            threat.Pos.SetPos(x0 - 62.5, y0 + 1, z0 + 0.5); // west shore of pond A
            sapi.World.SpawnEntity(threat);

            QueueSteps();
            sapi.Event.RegisterGameTickListener(Sample, 100);
            sapi.Event.RegisterGameTickListener(Physics, 20);
            sapi.Event.RegisterGameTickListener(_ => Advance(), 500);
            NextStep();
        }

        private bool InPondA(int x, int z) => Math.Abs(x - ((int)c.X - 40)) <= 18 && Math.Abs(z - (int)c.Z) <= 18;
        private bool InPondARim(int x, int z) => InPondA(x, z) && (Math.Abs(x - ((int)c.X - 40)) > 15 || Math.Abs(z - (int)c.Z) > 15);
        private bool InPondB(int x, int z) => Math.Abs(x - ((int)c.X + 50)) <= 15 && Math.Abs(z - ((int)c.Z + 30)) <= 15;
        private string Where(Entity e) => InPondA((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z)) ? "A" : InPondB((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z)) ? "B"
            : (e.FeetInLiquid || e.Swimming) ? "lake" : "LAND";

        // ------------------------------------------------------------------------------------------------ steps

        private void QueueSteps()
        {
            FlockRecord flock = null;
            steps.Enqueue(("settle", () =>
            {
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "greylag"));
                FlockRegistry.Instance.Calm();
                flock = FlockRegistry.Instance.Create("goose", 8, c);
                testFlock = flock.Id;
                for (int i = 0; i < 8; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, flock.Id);
                    e.Pos.SetPos(c.X - 40 + (i % 4) * 3 - 4.5, y0 + 0.75, c.Z + (i / 4) * 3 - 1.5);
                    sapi.World.SpawnEntity(e); geese.Add(e);
                }
            }, () => Elapsed > 8, () =>
            {
                Check(geese.All(e => e.Alive && Phase(e) == "Water"), "all 8 geese floating (phase Water)");
                Check(geese.All(e => e.FeetInLiquid || e.Swimming), "all in the water");
                double top = y0 + 1, worst = geese.Max(e => Math.Abs(e.Pos.Y - (top - 0.275)));
                Check(worst < 0.3, $"floating at the surface (worst {worst:0.00} from target)");
            }, 20));

            steps.Enqueue(("flush toward wall", () =>
            {
                ResetStats();
                geese[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
            }, () => Elapsed > 4 && geese.Where(e => e.Alive).All(e => Phase(e) == "Water"), () => ReportFlight("flush", 2), 90));

            FlockRecord treed = null;
            steps.Enqueue(("tree-ringed pond", () =>
            {
                ResetStats();
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "greylag"));
                treed = FlockRegistry.Instance.Create("goose", 6, c);
                testFlock = treed.Id;
                for (int i = 0; i < 6; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, treed.Id);
                    e.Pos.SetPos(c.X + 50 - 11 + (i % 3) * 2, y0 + 0.75, c.Z + 30 + (i / 3) * 3 - 1.5);
                    sapi.World.SpawnEntity(e);
                }
                // flushed from the west shore: the only spots far enough are on the east side, next to the trees
                var chick = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "chicken-hen")));
                chick.Pos.SetPos(c.X + 50 - 15, y0 + 1, c.Z + 30);
                sapi.World.SpawnEntity(chick);
                sapi.Event.RegisterCallback(_ => LoadedGeese()[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = chick, Type = EnumDamageType.BluntAttack }, 1), 3000);
            }, () => Elapsed > 7 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                ReportFlight("tree-ringed pond", 0);
                Log("tree-ringed pond: landed in " + string.Join(" ", LoadedGeese().Select(Where)));
                foreach (var e in LoadedGeese()) e.Die(EnumDespawnReason.Expire);
                testFlock = flock.Id;
            }, 90));

            // ---- lake wariness: a missed arrow, then disturbances until the flock abandons the lake
            steps.Enqueue(("missed arrow", () =>
            {
                ResetStats(); FlockRegistry.Instance.Calm();
                var g = LoadedGeese()[0];
                // an arrow flying east past the flock, 2 blocks to the north of a goose, starting 5 blocks west of it
                var arrow = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "arrow-flint")));
                arrow.Pos.SetPos(g.Pos.X - 5, g.Pos.Y + 0.8, g.Pos.Z - 2);
                arrow.Pos.Motion.Set(1.0, 0, 0); // 60 blocks/s
                sapi.World.SpawnEntity(arrow);
                sapi.Event.RegisterCallback(_ => arrow.Die(EnumDespawnReason.Expire), 1500);
            }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                int flew = stats.Values.Count(st => st.Samples > 0);
                Check(flew == LoadedGeese().Count && flew > 0, $"the whole flock took off at the near miss ({flew} of {LoadedGeese().Count} flew)");
                double w = FlockRegistry.Instance.Wariness(flock.Id);
                Check(Math.Abs(w - 1) < 0.05 && FlockRegistry.Instance.Skittish(flock.Id), $"one disturbance: wariness {w:0.00}, skittish");
                ReportFlight("missed arrow", 2);
            }, 90));

            steps.Enqueue(("second disturbance", () =>
            {
                ResetStats();
                LoadedGeese()[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
            }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                double w = FlockRegistry.Instance.Wariness(flock.Id);
                Check(Math.Abs(w - 2) < 0.05, $"two disturbances: wariness {w:0.00}, still on the lake");
                ReportFlight("second disturbance", 2);
            }, 90));

            Vec3d lakeLeft = null;
            steps.Enqueue(("third disturbance: leave", () =>
            {
                ResetStats();
                var g = LoadedGeese();
                lakeLeft = g[0].Pos.XYZ;
                g[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
            }, () => Elapsed > 2 && LoadedGeese().Count == 0, () =>
            {
                var rec = FlockRegistry.Instance.Get(flock.Id);
                Check(rec?.State == FlockState.Away, $"the flock left and rejoined the migration ({rec?.State})");
                Check(FlockRegistry.Instance.IsAbandoned(lakeLeft.X, lakeLeft.Z), "the lake it left is marked abandoned");
                var spots = LakeSearch.FindSpots(sapi.World.BlockAccessor, new Vec3d(lakeLeft.X + 30, y0 + 1, lakeLeft.Z), lakeLeft.X + 30, lakeLeft.Z, 36, new Random(1));
                Check(spots == null || spots.All(sp => sp.HorizontalSquareDistanceTo(lakeLeft.X, lakeLeft.Z) > FlockRegistry.AbandonRadius * FlockRegistry.AbandonRadius),
                    "the lake search no longer offers the abandoned lake");
                ReportFlight("leave", 0, landed: false);
            }, 60));

            steps.Enqueue(("landing flock", () =>
            {
                ResetStats(); geese.Clear();
                bool ok = FrameArrive(out string msg);
                Log("arrive: " + msg);
                Check(ok, "landing flock started: " + msg);
            }, () => Elapsed > 5 && LoadedGeese().Count > 0 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                var g = LoadedGeese();
                Check(g.Count == 8, $"the Away flock returned whole: {g.Count} geese");
                Check(g.All(e => e.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey) == flock.Id), $"same flock id #{flock.Id}");
                Check(FlockRegistry.Instance.Get(flock.Id)?.State == FlockState.Landed, "flock record Landed again");
                Check(g.All(e => e.Pos.HorDistanceTo(lakeLeft) > FlockRegistry.AbandonRadius), $"not on the abandoned lake: {string.Join(" ", g.Select(Where))}");
                geese.AddRange(g);
                ReportFlight("landing", 2);
            }, 150));

            steps.Enqueue(("a kill", () =>
            {
                ResetStats(); FlockRegistry.Instance.Calm();
                var arrow = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "arrow-flint")));
                var g = LoadedGeese()[0];
                arrow.Pos.SetPos(g.Pos.X, g.Pos.Y + 1, g.Pos.Z);
                sapi.World.SpawnEntity(arrow);
                g.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = arrow, CauseEntity = threat, Type = EnumDamageType.PiercingAttack }, 10);
                sapi.Event.RegisterCallback(_ => arrow.Die(EnumDespawnReason.Expire), 500);
            }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                var rec = FlockRegistry.Instance.Get(flock.Id);
                Check(rec.Count == 7 && rec.Killed == 1 && LoadedGeese().Count == 7, $"one goose shot: record {rec.Count} alive, {rec.Killed} killed, {LoadedGeese().Count} loaded");
                double w = FlockRegistry.Instance.Wariness(flock.Id);
                Check(Math.Abs(w - 1.5) < 0.05, $"a kill and the flush that follows: wariness {w:0.00}");
                ReportFlight("a kill", 2);
            }, 90));

            for (int cycle = 1; cycle <= 3; cycle++)
            {
                int n = cycle;
                steps.Enqueue(($"cycle {n}", () =>
                {
                    ResetStats(); FlockRegistry.Instance.Calm();
                    var alive = LoadedGeese();
                    if (alive.Count > 0) alive[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
                }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
                {
                    long mem = GC.GetTotalMemory(true) / (1024 * 1024);
                    Log($"cycle {n}: {LoadedGeese().Count} geese loaded, {sapi.World.LoadedEntities.Count} entities, managed memory {mem} MB, {FlockRegistry.Instance.Report()}");
                    memory.Add(mem);
                    ReportFlight($"cycle {n}", 2);
                }, 90));
            }
            // ---- mallards: the same AI from data alone (a smaller bird, a short takeoff, smaller ponds)
            FlockRecord ducks = null;
            steps.Enqueue(("mallard landing flock", () =>
            {
                ResetStats(); FlockRegistry.Instance.Calm();
                bool ok = FrameArrive(out string msg, "mallard");
                Log("arrive: " + msg);
                Check(ok, "mallard landing flock started: " + msg);
                // the landing flock by the id in the message: world generation also puts wild mallards on lakes nearby
                var m = System.Text.RegularExpressions.Regex.Match(msg ?? "", @"flock #(\d+)");
                ducks = m.Success ? FlockRegistry.Instance.Get(long.Parse(m.Groups[1].Value)) : null;
                testFlock = ducks?.Id ?? -2;
            }, () => Elapsed > 5 && LoadedGeese().Count > 0 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                var g = LoadedGeese();
                Check(ducks?.Species == "mallard" && g.Count >= 4 && g.Count <= 10 && g.All(e => e.Code.Path == "mallard"), $"a mallard flock of 4-10 came in ({g.Count}, species {ducks?.Species})");
                ReportFlight("mallard landing", 2);
            }, 150));
            steps.Enqueue(("mallard flush", () =>
            {
                ResetStats();
                LoadedGeese()[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
            }, () => Elapsed > 3 && LoadedGeese().All(e => Phase(e) == "Water"), () => ReportFlight("mallard flush", 2), 90));

            // ---- herons: wading (standing on the bottom of water one block deep), from data plus the wade habitat
            FlockRecord herons = null;
            int wadeOut = 0, wadeSamples = 0;
            steps.Enqueue(("heron settle", () =>
            {
                ResetStats(); FlockRegistry.Instance.Calm();
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "heron"));
                herons = FlockRegistry.Instance.Create("heron", 2, c);
                testFlock = herons.Id;
                for (int i = 0; i < 2; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, herons.Id);
                    e.Pos.SetPos(c.X - 40 + 16.5, y0 + 0.05, c.Z + i * 3 - 1);   // on the bottom of pond A's east shallows
                    sapi.World.SpawnEntity(e);
                }
            }, () => Elapsed > 10, () =>
            {
                var g = LoadedGeese();
                Check(g.Count == 2 && g.All(e => Phase(e) == "Water"), $"2 herons at rest ({string.Join(" ", g.Select(Phase))})");
                Check(g.All(e => !e.Swimming && e.FeetInLiquid && Math.Abs(e.Pos.Y - y0) < 0.25),
                    $"standing on the bottom, not swimming: {string.Join(" ", g.Select(e => $"y{e.Pos.Y - y0:0.00} swim={e.Swimming} feet={e.FeetInLiquid}"))}");
            }, 20));
            steps.Enqueue(("heron wading", () => { wadeOut = 0; wadeSamples = 0; }, () =>
            {
                foreach (var e in LoadedGeese())
                {
                    if (Phase(e) != "Water") continue;
                    wadeSamples++;
                    if (!LakeSearch.IsWadeable(sapi.World.BlockAccessor, (int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z), out _)) wadeOut++;
                }
                return Elapsed > 25;
            }, () =>
            {
                Check(wadeSamples > 0 && wadeOut == 0, $"stayed in wadeable water for 25 s ({wadeOut} of {wadeSamples} samples out of it)");
                Check(LoadedGeese().All(e => Phase(e) == "Water" && !e.Swimming), "still wading, not swimming");
            }, 40));
            steps.Enqueue(("heron flush", () =>
            {
                ResetStats();
                LoadedGeese()[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
            }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                var g = LoadedGeese();
                Check(stats.Values.Count(st => st.Samples > 0) == 2, "both herons flew");
                Check(g.All(e => !e.Swimming && LakeSearch.IsWadeable(sapi.World.BlockAccessor, (int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z), out int wy) && Math.Abs(e.Pos.Y - wy) < 0.3),
                    $"came down standing in wadeable water: {string.Join(" ", g.Select(e => $"{Where(e)} y{e.Pos.Y - y0:0.00} swim={e.Swimming}"))}");
                ReportFlight("heron flush", 2);
                foreach (var e in g) e.Die(EnumDespawnReason.Expire);
            }, 90));

            steps.Enqueue(("memory", () => { }, () => true, () =>
            {
                Check(memory.Count >= 3 && memory.Last() - memory[1] < 64, $"managed memory over cycles (MB): {string.Join(", ", memory)}");
                int left = sapi.World.LoadedEntities.Values.Count(e => e.Alive && e.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, -1) == flock.Id);
                Check(left == 7, $"still 7 geese after the cycles ({left})");
            }, 5));
        }

        private readonly List<long> memory = new();

        private bool FrameArrive(out string msg, string species = "goose")
        {
            var flocks = sapi.ModLoader.GetModSystem<FlocksModSystem>();
            // stand-in for the player: on the slab, 60 blocks north of the site centre
            return flocks.Landings.Arrive(new Vec3d(c.X, y0 + 1, c.Z - 60), flocks.SpeciesByCode[species], true, out msg);
        }

        private double Elapsed => sapi.World.ElapsedMilliseconds / 1000.0 - stepStart;

        private void NextStep()
        {
            if (steps.Count == 0) { Finish(); return; }
            var s = steps.Dequeue();
            stepName = s.name; stepDone = s.done; stepCheck = s.check; stepTimeout = s.timeout;
            stepStart = sapi.World.ElapsedMilliseconds / 1000.0;
            Log("---- step: " + stepName);
            try { s.start(); } catch (Exception ex) { Check(false, "start threw " + ex); }
        }

        private void Advance()
        {
            if (stepDone == null) return;
            bool done; try { done = stepDone(); } catch (Exception ex) { Check(false, "condition threw " + ex); done = true; }
            bool timedOut = !done && Elapsed > stepTimeout;
            if (!done && !timedOut) return;
            if (timedOut) Check(false, $"timed out after {stepTimeout:0} s; phases: {string.Join(" ", LoadedGeese().Select(e => Phase(e) + "@" + Where(e)))}");
            else Log($"step done in {Elapsed:0.0} s");
            try { stepCheck(); } catch (Exception ex) { Check(false, "check threw " + ex); }
            stepDone = null;
            NextStep();
        }

        private void Finish()
        {
            Log("==== SUMMARY: " + pass + " passed, " + fail + " failed");
            foreach (var line in summary) Log(line);
            sapi.Event.RegisterCallback(_ => sapi.Server.ShutDown(), 1000);
        }

        // ------------------------------------------------------------------------------------------------ measuring

        /// <summary>The test flock's geese (world generation puts natural geese on real lakes nearby too; those are left out).</summary>
        private List<Entity> LoadedGeese() => sapi.World.LoadedEntities.Values.Where(e => e.Alive && e.Code?.Domain == "flocks"
            && e.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, -1) == testFlock).ToList();
        private long testFlock = -2;

        private double physicsAcc;
        /// <summary>What PhysicsManager does for a tracked entity: OnPhysicsTick at 1/30 s steps, then AfterPhysicsTick.</summary>
        private void Physics(float dt)
        {
            physicsAcc = Math.Min(physicsAcc + dt, 0.2);
            const float step = 1 / 30f;
            int steps = 0;
            for (; physicsAcc >= step; physicsAcc -= step) steps++;
            if (steps == 0) return;
            foreach (var e in LoadedGeese())
            {
                if (e.IsTracked != 0) continue; // a player sees it: the server does it
                foreach (var b in e.SidedProperties.Behaviors)
                {
                    if (b is not IPhysicsTickable pt) continue;
                    for (int i = 0; i < steps; i++) pt.OnPhysicsTick(step);
                    pt.AfterPhysicsTick(step * steps);
                }
            }
        }
        private static string Phase(Entity e) => e.GetBehavior<EntityBehaviorWaterfowl>()?.PhaseName ?? "?";

        private void ResetStats() => stats.Clear();

        /// <summary>The block in front of a bird, n blocks along its yaw at its height (for the hit log).</summary>
        private string Ahead(IBlockAccessor ba, Entity e, double n)
        {
            var b = ba.GetBlock(new BlockPos((int)Math.Floor(e.Pos.X + Math.Sin(e.Pos.Yaw) * n), (int)Math.Floor(e.Pos.Y + 0.3), (int)Math.Floor(e.Pos.Z + Math.Cos(e.Pos.Yaw) * n)));
            return b?.Code?.Path ?? "?";
        }

        private void Sample(float dt)
        {
            var ba = sapi.World.BlockAccessor;
            foreach (var e in LoadedGeese())
            {
                double now = sapi.World.ElapsedMilliseconds / 1000.0;
                if (!stats.TryGetValue(e.EntityId, out var s)) stats[e.EntityId] = s = new Stats { Last = e.Pos.XYZ, LastT = now };
                string ph = Phase(e);
                if (s.LastPhase == "Glide" && ph == "Fly")
                {
                    s.GoArounds++;
                    var w = e.GetBehavior<EntityBehaviorWaterfowl>();
                    Log($"go-around goose {e.EntityId} at {e.Pos.X - c.X:0},{e.Pos.Y - y0:0},{e.Pos.Z - c.Z:0} (site coords): {w?.LastGoAround}; spot {(w?.Target == null ? "-" : $"{w.Target.X - c.X:0},{w.Target.Z - c.Z:0}")}");
                }
                s.LastPhase = ph;
                bool flying = ph == "Fly" || ph == "Glide" || ph == "Run";
                double sdt = Math.Max(1e-3, now - s.LastT);
                s.LastT = now;
                if (!flying) { s.Last = e.Pos.XYZ; continue; }
                s.Samples++; s.FlightSeconds += sdt;
                s.MaxCommanded = Math.Max(s.MaxCommanded, e.GetBehavior<EntityBehaviorWaterfowl>()?.FlySpeed ?? 0);
                int gx = (int)Math.Floor(e.Pos.X), gz = (int)Math.Floor(e.Pos.Z);
                int ground = ba.GetRainMapHeightAt(gx, gz);
                if (e.CollidedHorizontally)
                {
                    s.Collided++;
                    if (!s.LoggedHit) { s.LoggedHit = true; Log($"hit: {e.Code.Path} {e.EntityId} {ph} at site {e.Pos.X - c.X:0.0},{e.Pos.Y - y0:0.0},{e.Pos.Z - c.Z:0.0} heading {e.Pos.Yaw:0.00} speed {e.GetBehavior<EntityBehaviorWaterfowl>()?.FlySpeed:0.0}; ground here {ground - y0} ({ba.GetBlock(new BlockPos(gx, ground, gz))?.Code}); ahead 1 block {Ahead(ba, e, 1)}, 2 {Ahead(ba, e, 2)}"); }
                }
                if (ground > 0 && e.Pos.Y < ground + 1 - 0.3 && ph != "Run")
                {
                    var b = ba.GetBlock(new BlockPos(gx, ground, gz), BlockLayersAccess.Fluid);
                    if (b == null || !b.IsLiquid())
                    {
                        s.InTerrain++;
                        if (!s.LoggedLow) { s.LoggedLow = true; Log($"low: {e.Code.Path} {e.EntityId} {ph} at site {e.Pos.X - c.X:0.0},{e.Pos.Y - y0:0.0},{e.Pos.Z - c.Z:0.0}; top of the column {ground + 1 - y0} ({ba.GetBlock(new BlockPos(gx, ground, gz))?.Code}); feet {e.Pos.Y - ground - 1:0.00} below it"); }
                    }
                }
                s.MaxAlt = Math.Max(s.MaxAlt, e.Pos.Y - y0);
                double moved = e.Pos.XYZ.DistanceTo(s.Last);
                // over the real time since the last sample (listener intervals vary), horizontal only, like the cap
                double hmoved = Math.Sqrt((e.Pos.X - s.Last.X) * (e.Pos.X - s.Last.X) + (e.Pos.Z - s.Last.Z) * (e.Pos.Z - s.Last.Z));
                if (sdt > 0.05) s.MaxSpeed = Math.Max(s.MaxSpeed, hmoved / sdt);
                if (moved < 0.02) s.Still++;
                s.Last = e.Pos.XYZ;
            }
        }

        private void ReportFlight(string what, int expectInPond, bool landed = true)
        {
            int collided = stats.Values.Sum(s => s.Collided), inTerrain = stats.Values.Sum(s => s.InTerrain), samples = stats.Values.Sum(s => s.Samples);
            int still = stats.Values.Count == 0 ? 0 : stats.Values.Max(s => s.Still);
            Log($"{what}: {stats.Count} geese flew; samples {samples}; collided {collided}; below the ground or tree tops {inTerrain}; " +
                $"top altitude above the slab {(stats.Count > 0 ? stats.Values.Max(s => s.MaxAlt) : 0):0.0}; top horizontal speed {(stats.Count > 0 ? stats.Values.Max(s => s.MaxSpeed) : 0):0.0} b/s " +
                $"(commanded max {(stats.Count > 0 ? stats.Values.Max(s => s.MaxCommanded) : 0):0.0}); most samples stuck in flight {still}");
            Log($"{what}: per goose flight seconds {string.Join(" ", stats.Values.Select(s => s.FlightSeconds.ToString("0")))}; go-arounds {string.Join(" ", stats.Values.Select(s => s.GoArounds))}");
            Check(collided == 0, $"no wall collisions in flight ({collided} collided samples)");
            Check(inTerrain == 0, $"never inside ground or trees in flight ({inTerrain} samples)");
            Check(still < 10, $"no goose hung in mid-air (longest {still} samples without moving)");
            if (!landed) return;
            var g = LoadedGeese();
            Check(g.All(e => e.FeetInLiquid || e.Swimming), $"all landed in water (ponds A/B or a natural lake): {string.Join(" ", g.Select(Where))}");
        }
    }
}
