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
            /// <summary>How far it turned in flight (radians): gulls wheel before they settle.</summary>
            public double Turned, LastYaw = double.NaN;
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
            sapi.Event.OnEntityDeath += (e, ds) =>
            {
                if (e.Code?.Domain == "flocks" && e.Code.Path == "mouse")
                    Log($"mouse {e.EntityId} died: {ds?.Source} {ds?.Type} by {ds?.SourceEntity?.Code} at {e.Pos.X - c.X:0.0},{e.Pos.Y - y0:0.0},{e.Pos.Z - c.Z:0.0}");
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
            int salt = sapi.World.GetBlock(new AssetLocation("game", "saltwater-still-7")).BlockId;
            var bulk = sapi.World.GetBlockAccessorBulkUpdate(true, false);
            var pos = new BlockPos(0, 0, 0);
            for (int x = x0 - half; x <= x0 + half; x++)
                for (int z = z0 - half; z <= z0 + half; z++)
                {
                    bool pond = InPondA(x, z) || InPondB(x, z), sea = InPondC(x, z);
                    for (int y = y0 - 3; y <= y0; y++)
                    {
                        pos.Set(x, y, z);
                        bool deep = !InPondARim(x, z) || y == y0;   // pond A's rim is one block deep (shallows for waders)
                        if ((pond || sea) && y > y0 - 3 && deep) { bulk.SetBlock(0, pos); bulk.SetBlock(sea ? salt : water, pos, BlockLayersAccess.Fluid); }
                        else bulk.SetBlock(stone, pos);
                    }
                    if (x >= x0 - 12 && x <= x0 - 10 && Math.Abs(z - z0) <= 30)
                        for (int y = y0 + 1; y <= y0 + 14; y++) { pos.Set(x, y, z); bulk.SetBlock(stone, pos); }
                    // a 12-high stone pillar: the eagle's perch, the most prominent point on the site
                    if (x >= x0 + 20 && x <= x0 + 22 && z >= z0 - 42 && z <= z0 - 40)
                        for (int y = y0 + 1; y <= y0 + 12; y++) { pos.Set(x, y, z); bulk.SetBlock(stone, pos); }
                    // a ring of 8-high "trees" two blocks outside pond B: every way in has to drop over them
                    int bx = Math.Abs(x - (x0 + 50)), bz = Math.Abs(z - (z0 + 30));
                    if (Math.Max(bx, bz) == 17)
                        for (int y = y0 + 1; y <= y0 + 8; y++) { pos.Set(x, y, z); bulk.SetBlock(stone, pos); }
                }
            bulk.Commit();
            // crow country: two soil fields with a 5x5 bed of flax on farmland (field 2 gets a straw dummy later), and a
            // little oak to perch in. Farmland is set one block at a time so its block entity registers as food.
            BuildField(x0 - 2, z0 + 60); BuildField(x0 - 52, z0 + 62);
            int log = sapi.World.GetBlock(new AssetLocation("game", "log-placed-oak-ud"))?.BlockId ?? stone;
            int leaves = sapi.World.GetBlock(new AssetLocation("game", "leaves-placed-oak"))?.BlockId ?? stone;
            for (int y = y0 + 1; y <= y0 + 6; y++) ba.SetBlock(log, new BlockPos(x0 + 15, y, z0 + 66));
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) for (int y = y0 + 5; y <= y0 + 7; y++)
                if (dx != 0 || dz != 0 || y == y0 + 7) ba.SetBlock(leaves, new BlockPos(x0 + 15 + dx, y, z0 + 66 + dz));
            // owl wood: two oaks with real branches (branchy leaves, which have a collision box, under the crown's leaves,
            // which don't), 20 blocks apart in a soil clearing
            for (int x = x0 + 30; x <= x0 + 72; x++) for (int z = z0 + 78; z <= z0 + 98; z++) ba.SetBlock(sapi.World.GetBlock(new AssetLocation("game", "soil-medium-normal")).BlockId, new BlockPos(x, y0, z));
            BuildOwlTree(OwlTree1X, OwlTreeZ, log, leaves); BuildOwlTree(OwlTree2X, OwlTreeZ, log, leaves);
            Log($"site built: slab top y={y0} (terrain max {top}); rain map at pond A {ba.GetRainMapHeightAt(x0 - 40, z0)}, " +
                $"slab {ba.GetRainMapHeightAt(x0, z0 + 60)}, wall {ba.GetRainMapHeightAt(x0 - 11, z0)}");
            // the goose entity file parsed whole (a bad field makes the game drop most of it, silently but for one log line)
            var gt = sapi.World.GetEntityType(new AssetLocation("flocks", "greylag"));
            Check(gt?.Sounds != null && gt.Sounds.ContainsKey("takeoffwings") && gt.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.waterfowl") == true,
                "greylag entity file loaded whole (sounds and flocks.waterfowl behaviour present)");
            var items = new[] { "greylag", "mallard", "heron", "eagle", "gull", "crow", "owl", "mouse" };
            Check(items.All(b => sapi.World.GetItem(new AssetLocation("flocks", "creature-" + b))?.GetType().Name == "ItemCreature"
                                 && sapi.World.GetEntityType(new AssetLocation("flocks", b)) != null),
                "creature items (creative inventory) loaded, each placing an existing bird");
            var et = sapi.World.GetEntityType(new AssetLocation("flocks", "eagle"));
            Check(et?.Sounds != null && et.Sounds.ContainsKey("call") && et.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.raptor") == true,
                "eagle entity file loaded whole");
            var ht = sapi.World.GetEntityType(new AssetLocation("flocks", "heron"));
            Check(ht?.Sounds != null && ht.Sounds.ContainsKey("takeoffcall") && ht.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.waterfowl") == true,
                "heron entity file loaded whole");
            var gu = sapi.World.GetEntityType(new AssetLocation("flocks", "gull"));
            Check(gu?.Sounds != null && gu.Sounds.ContainsKey("takeoffcall") && gu.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.waterfowl" && b["wheelSecondsMax"].AsDouble() > 0) == true,
                "gull entity file loaded whole (sounds and a wheeling flocks.waterfowl)");
            Check(Lang.HasTranslation("flocks:item-handbooktext-flocks:creature-gull") && Lang.HasTranslation("flocks:gamemechanicinfo-birds-text")
                  && sapi.Assets.Exists(new AssetLocation("flocks", "config/handbook/birds.json")),
                "handbook texts and the Birds guide page loaded");
            var ct = sapi.World.GetEntityType(new AssetLocation("flocks", "crow"));
            Check(ct?.Sounds != null && ct.Sounds.ContainsKey("takeoffcall") && ct.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.corvid") == true,
                "crow entity file loaded whole (sounds and flocks.corvid)");
            Check(Lang.HasTranslation("flocks:item-handbooktext-flocks:creature-crow") && Lang.Get("game:item-handbooktext-strawdummy").Contains("crows"),
                $"the crow's handbook text, and the straw dummy's mentions the crows ({Lang.Get("game:item-handbooktext-strawdummy")})");
            var ot = sapi.World.GetEntityType(new AssetLocation("flocks", "owl"));
            Check(ot?.Sounds != null && ot.Sounds.ContainsKey("hoot") && ot.Sounds.ContainsKey("kewick") && ot.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.owl") == true,
                "owl entity file loaded whole (sounds and flocks.owl)");
            var mo = sapi.World.GetEntityType(new AssetLocation("flocks", "mouse"));
            Check(mo?.Sounds != null && mo.Sounds.ContainsKey("hurt") && mo.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "taskai") == true
                  && mo.Server.SpawnConditions?.Runtime?.MaxQuantity == 4,
                "mouse entity file loaded whole (sounds, vanilla AI, a low spawn cap)");
            Check(Lang.HasTranslation("flocks:item-handbooktext-flocks:creature-owl") && Lang.HasTranslation("flocks:item-handbooktext-flocks:creature-mouse")
                  && Lang.Get("flocks:gamemechanicinfo-birds-text").Contains("tawny owl"), "the owl's and the mouse's handbook texts, and the owl on the Birds page");
            // every shape's animations as the client will build them: a keyframe at or past quantityframes, or an element
            // that sets one axis of a group but not all three, crashes the game when the animation first plays (the server
            // never builds animation frames, so nothing else here would notice)
            var badAnims = new List<string>();
            int shapes = 0, anims = 0;
            foreach (var asset in sapi.Assets.GetMany("shapes/", "flocks"))
            {
                shapes++;
                Shape sh;
                try { sh = asset.ToObject<Shape>(); } catch (Exception ex) { badAnims.Add($"{asset.Location}: unreadable ({ex.Message})"); continue; }
                foreach (var an in sh?.Animations ?? Array.Empty<Animation>())
                {
                    anims++;
                    foreach (var kf in an.KeyFrames ?? Array.Empty<AnimationKeyFrame>())
                    {
                        if (kf.Frame >= an.QuantityFrames) badAnims.Add($"{asset.Location.Path} {an.Code}: key at {kf.Frame} of {an.QuantityFrames} frames");
                        foreach (var (el, ke) in kf.Elements ?? new Dictionary<string, AnimationKeyFrameElement>())
                            if (ke.AnySet && ((ke.RotationX != null) != (ke.RotationY != null) || (ke.RotationX != null) != (ke.RotationZ != null)
                                || (ke.OffsetX != null) != (ke.OffsetY != null) || (ke.OffsetX != null) != (ke.OffsetZ != null)))
                                badAnims.Add($"{asset.Location.Path} {an.Code} frame {kf.Frame} {el}: an axis group only partly set");
                    }
                }
            }
            Check(shapes >= 9 && anims > 40 && badAnims.Count == 0, $"every shape's animations will build on the client ({shapes} shapes, {anims} animations; {(badAnims.Count == 0 ? "ok" : string.Join("; ", badAnims.Take(6)))})");
            var branchy = sapi.World.GetBlock(new AssetLocation("game", "leavesbranchy-placed-oak"));
            Check(branchy?.CollisionBoxes?.Length > 0 && (sapi.World.GetBlock(new AssetLocation("game", "leaves-placed-oak"))?.CollisionBoxes?.Length ?? 0) == 0,
                $"branchy leaves (tree branches) are solid, ordinary leaves aren't ({branchy?.Code}: {branchy?.CollisionBoxes?.Length ?? -1} boxes)");
            var mt = sapi.World.GetEntityType(new AssetLocation("flocks", "mallard"));
            Check(mt?.Sounds != null && mt.Sounds.ContainsKey("takeoffwings") && mt.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == "flocks.waterfowl") == true,
                "mallard entity file loaded whole (sounds and flocks.waterfowl behaviour present)");
            var spc = sapi.ModLoader.GetModSystem<FlocksModSystem>().SpeciesByCode;
            Check(spc.ContainsKey("goose") && spc["goose"].Lands && spc["goose"].LandingMin == 7 && spc.ContainsKey("crane") && !spc.ContainsKey("hawk")
                  && spc.ContainsKey("mallard") && spc["mallard"].MinLakeSize == 25 && spc.ContainsKey("heron") && spc["heron"].Wades && !spc["heron"].ArriveInFlocks
                  && spc.ContainsKey("gull") && spc["gull"].Sea && spc["gull"].Liquid == "saltwater" && spc["gull"].YearRound && spc["gull"].WarinessFactor == 0.5
                  && spc.ContainsKey("crow") && spc["crow"].Ground && spc["crow"].YearRound && !spc["crow"].ArriveInFlocks
                  && spc.ContainsKey("owl") && spc["owl"].Woodland && spc["owl"].YearRound && !spc["owl"].ArriveInFlocks,
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

        /// <summary>A soil field, with a 5x5 bed of flax (stage 5) on farmland from (fx, fz).</summary>
        private void BuildField(int fx, int fz)
        {
            var ba = sapi.World.BlockAccessor;
            int soil = sapi.World.GetBlock(new AssetLocation("game", "soil-medium-normal")).BlockId;
            int farmland = sapi.World.GetBlock(new AssetLocation("game", "farmland-moist-medium")).BlockId;
            int flax = sapi.World.GetBlock(new AssetLocation("game", "crop-flax-5")).BlockId;
            for (int x = fx - 10; x <= fx + 14; x++)
                for (int z = fz - 6; z <= fz + 16; z++) ba.SetBlock(soil, new BlockPos(x, y0, z));
            for (int x = fx; x < fx + 5; x++)
                for (int z = fz; z < fz + 5; z++)
                {
                    ba.SetBlock(farmland, new BlockPos(x, y0, z));
                    ba.SetBlock(flax, new BlockPos(x, y0 + 1, z));
                }
        }

        private int OwlTree1X => (int)c.X + 60;
        private int OwlTree2X => (int)c.X + 40;
        private int OwlTreeZ => (int)c.Z + 88;

        /// <summary>An oak for owls: a trunk 8 high, four branches of branchy leaves 3 blocks out at 5 up, and a crown of
        /// leaves over them from 6 to 10 up. The branch ends have leaves over them and air under them.</summary>
        private void BuildOwlTree(int tx, int tz, int log, int leaves)
        {
            var ba = sapi.World.BlockAccessor;
            int branch = sapi.World.GetBlock(new AssetLocation("game", "leavesbranchy-placed-oak"))?.BlockId ?? leaves;
            for (int dx = -3; dx <= 3; dx++)
                for (int dz = -3; dz <= 3; dz++)
                    for (int y = y0 + 6; y <= y0 + 10; y++)
                    {
                        int r2 = dx * dx + dz * dz;
                        if (r2 > (y == y0 + 10 ? 2 : 10)) continue;
                        ba.SetBlock(leaves, new BlockPos(tx + dx, y, tz + dz));
                    }
            for (int y = y0 + 1; y <= y0 + 8; y++) ba.SetBlock(log, new BlockPos(tx, y, tz));
            for (int d = 1; d <= 3; d++)
                foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    ba.SetBlock(branch, new BlockPos(tx + ox * d, y0 + 5, tz + oz * d));
        }

        /// <summary>Growth stages of a field's 25 crops.</summary>
        private List<int> CropStages(int fx, int fz)
        {
            var ba = sapi.World.BlockAccessor; var list = new List<int>();
            for (int x = fx; x < fx + 5; x++)
                for (int z = fz; z < fz + 5; z++)
                {
                    var b = ba.GetBlock(new BlockPos(x, y0 + 1, z));
                    list.Add(b?.CropProps != null && int.TryParse(b.LastCodePart(), out int s) ? s : 0);
                }
            return list;
        }

        private bool InPondA(int x, int z) => Math.Abs(x - ((int)c.X - 40)) <= 18 && Math.Abs(z - (int)c.Z) <= 18;
        private bool InPondARim(int x, int z) => InPondA(x, z) && (Math.Abs(x - ((int)c.X - 40)) > 15 || Math.Abs(z - (int)c.Z) > 15);
        private bool InPondB(int x, int z) => Math.Abs(x - ((int)c.X + 50)) <= 15 && Math.Abs(z - ((int)c.Z + 30)) <= 15;
        /// <summary>Pond C is salt water: the sea, for gulls.</summary>
        private bool InPondC(int x, int z) => Math.Abs(x - ((int)c.X + 55)) <= 14 && Math.Abs(z - ((int)c.Z - 45)) <= 14;
        private string Where(Entity e) => InPondA((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z)) ? "A" : InPondB((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z)) ? "B"
            : InPondC((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z)) ? "C" : (e.FeetInLiquid || e.Swimming) ? "lake" : "LAND";
        /// <summary>Floating on salt water.</summary>
        private bool OnSea(Entity e) => sapi.World.BlockAccessor.GetBlock(new BlockPos((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Y + 0.1), (int)Math.Floor(e.Pos.Z)), BlockLayersAccess.Fluid)?.LiquidCode == "saltwater";

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

            Entity spear = null; Vec3d spearFrom = null; float gooseHp = 0; Entity speared = null;
            steps.Enqueue(("a spear never connects", () =>
            {
                ResetStats(); FlockRegistry.Instance.Calm();
                speared = LoadedGeese()[0];
                gooseHp = speared.WatchedAttributes.GetTreeAttribute("health")?.GetFloat("currenthealth") ?? 0;
                spear = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "spear-generic-flint")));
                spearFrom = new Vec3d(speared.Pos.X - 6, speared.Pos.Y + 0.4, speared.Pos.Z);
                spear.Pos.SetPos(spearFrom);
                spear.Pos.Motion.Set(0.5, 0, 0);                                     // 30 blocks/s, straight at it
                spear.AlwaysActive = true;
                sapi.World.SpawnEntity(spear);
                spear.State = EnumEntityState.Active;
                extraPhysics.Add(spear);
            }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                float hp = speared.WatchedAttributes.GetTreeAttribute("health")?.GetFloat("currenthealth") ?? 0;
                bool hitIt = (bool)(spear.GetType().GetProperty("EntityHit")?.GetValue(spear) ?? true);
                Check(!hitIt, $"the spear didn't strike the goose (EntityHit {hitIt}; it ended {spear.Pos.X - spearFrom.X:0.0} blocks on)");
                Check(speared.Alive && hp >= gooseHp, $"the goose is unhurt ({hp} of {gooseHp})");
                Check(spear.Pos.X - spearFrom.X > 7, $"it flew on past the goose ({spear.Pos.X - spearFrom.X:0.0} blocks; the goose was 6 off, its far side about 6.5)");
                Check(stats.Values.Count(st => st.Samples > 0) > 0, "and the flock flushed at the near miss");
                extraPhysics.Remove(spear);
                spear.Die(EnumDespawnReason.Expire);
            }, 60));
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

            // ---- the golden eagle: perch, soar on a thermal, stoop on a hare, feed, back to the perch
            steps.Enqueue(("eagle perch", () =>
            {
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "eagle"));
                eagle = sapi.World.ClassRegistry.CreateEntity(type);
                eagle.Pos.SetPos(c.X + 21.5, y0 + 13.05, c.Z - 40.5);   // on top of the pillar
                sapi.World.SpawnEntity(eagle);
            }, () => Elapsed > 5, () =>
            {
                Check(RState(eagle) == "Perch" && eagle.OnGround && Math.Abs(eagle.Pos.Y - (y0 + 13)) < 0.3,
                    $"perched on the pillar ({RState(eagle)}, y {eagle.Pos.Y - y0:0.00}, on ground {eagle.OnGround})");
            }, 20));
            steps.Enqueue(("eagle soar", () =>
            {
                eagleHits = eagleLow = eagleSamples = 0; eagleTurned = 0; soarSince = 0; eagleLastYaw = double.NaN; eagleMaxAlt = 0; eagleMinAlt = double.MaxValue; eagleStates.Clear();
                eagle.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);  // dodged: a startle
            }, () => Elapsed > 45, () =>
            {
                Check(eagleStates.Contains("Takeoff") && eagleStates.Contains("Soar"), $"took off and soared (states seen: {string.Join(", ", eagleStates)})");
                Check(eagleTurned > 2 * Math.PI, $"circled on the thermal (turned {eagleTurned / (2 * Math.PI):0.0} full circles)");
                Check(eagleMinAlt > 5 && eagleMaxAlt < 90, $"soared at a sensible height above the ground ({eagleMinAlt:0}-{eagleMaxAlt:0} blocks)");
                Check(eagleHits == 0 && eagleLow == 0, $"no collisions or terrain in flight ({eagleHits} hits, {eagleLow} low, of {eagleSamples} samples)");
            }, 60));
            var hares = new List<Entity>();
            Vec3d perchAim = null;
            steps.Enqueue(("eagle hunt", () =>
            {
                eagleStates.Clear(); eagleMaxStoop = 0; eagleMealDist = double.NaN; eagleFeedSince = -1;
                var b = eagle.GetBehavior<EntityBehaviorRaptor>();
                var hareType = sapi.World.GetEntityType(new AssetLocation("game", "hare-european-adult-male"));
                // ~25 blocks from where the eagle is circling, toward the middle of the slab: it has to close in first
                double ex = c.X - eagle.Pos.X, ez = c.Z - eagle.Pos.Z, el = Math.Max(1, Math.Sqrt(ex * ex + ez * ez));
                if (el < 2) { ex = 1; ez = 0; el = 1; }
                double hx = GameMath.Clamp(eagle.Pos.X + ex / el * 25, c.X - 30, c.X + 30), hz = GameMath.Clamp(eagle.Pos.Z + ez / el * 25, c.Z - 30, c.Z + 30);
                for (int i = 0; i < 3; i++)
                {
                    var h = sapi.World.ClassRegistry.CreateEntity(hareType);
                    h.Pos.SetPos(hx + 5 * i - 5, y0 + 1, hz);
                    sapi.World.SpawnEntity(h); hares.Add(h);
                }
            }, () => (eagleFeedSince >= 0 && sapi.World.ElapsedMilliseconds / 1000.0 - eagleFeedSince > 6) || Elapsed > 100, () =>
            {
                var rb = eagle.GetBehavior<EntityBehaviorRaptor>();
                Log($"hunt: eagle at site {eagle.Pos.X - c.X:0},{eagle.Pos.Y - y0:0},{eagle.Pos.Z - c.Z:0}; last look: {rb?.LastHunt}; hares: " +
                    string.Join("; ", hares.Select(h => $"{h.Code?.ToShortString()} alive={h.Alive} at {h.Pos.X - c.X:0},{h.Pos.Y - y0:0.0},{h.Pos.Z - c.Z:0} d={h.Pos.HorDistanceTo(eagle.Pos.XYZ):0}")));
                Check(eagleStates.Contains("Stoop"), $"stooped on a hare (states seen: {string.Join(", ", eagleStates)})");
                Check(hares.Any(h => !h.Alive), $"caught one ({hares.Count(h => !h.Alive)} of 3 hares killed)");
                Check(eagleStates.Contains("Feed"), "mantled over the kill");
                Check(eagleMaxStoop >= 25, $"a fast stoop (top speed {eagleMaxStoop:0.0} blocks/s)");
                Check(eagleMealDist <= 0.9, $"feeding on its kill, not beside it (distance to it {eagleMealDist:0.00})");
            }, 110));
            // the kill falls off a ledge: put the feeding eagle on top of a 5-high block right beside its kill
            BlockPos ledgeAt = null;
            steps.Enqueue(("eagle follows its kill down a ledge", () =>
            {
                var meal = eagle.GetBehavior<EntityBehaviorRaptor>()?.Meal;
                if (meal == null) { Check(false, "had a kill to follow"); return; }
                int mx = (int)Math.Floor(meal.Pos.X), mz = (int)Math.Floor(meal.Pos.Z);
                ledgeAt = new BlockPos(mx + 2, y0 + 1, mz - 1);
                int granite = sapi.World.GetBlock(new AssetLocation("game", "rock-granite")).BlockId;
                var ba = sapi.World.BlockAccessor;
                for (int dx = 0; dx < 3; dx++) for (int dz = 0; dz < 3; dz++) for (int dy = 0; dy < 5; dy++)
                    ba.SetBlock(granite, ledgeAt.AddCopy(dx, dy, dz));
                eagle.TeleportToDouble(mx + 2.5, y0 + 6, mz + 0.5);
                eagleStates.Clear();
            }, () => Elapsed > 15, () =>
            {
                var meal = eagle.GetBehavior<EntityBehaviorRaptor>()?.Meal;
                Check(RState(eagle) == "Feed" && meal != null && Math.Abs(eagle.Pos.Y - meal.Pos.Y) < 0.5 && eagle.Pos.HorDistanceTo(meal.Pos) <= 1.0,
                    $"flew down and fed on it ({RState(eagle)}, {(meal == null ? "no kill" : $"{eagle.Pos.Y - meal.Pos.Y:0.0} above it, {eagle.Pos.HorDistanceTo(meal.Pos):0.00} from it")}; states {string.Join(", ", eagleStates)})");
                var ba = sapi.World.BlockAccessor;
                if (ledgeAt != null)
                    for (int dx = 0; dx < 3; dx++) for (int dz = 0; dz < 3; dz++) for (int dy = 0; dy < 5; dy++)
                        ba.SetBlock(0, ledgeAt.AddCopy(dx, dy, dz));
            }, 25));
            steps.Enqueue(("eagle feeds and leaves", () => { eagleStates.Clear(); }, () => eagleStates.Contains("Soar") || Elapsed > 60, () =>
            {
                Check(eagleStates.Contains("Soar"), $"back up after feeding (states seen: {string.Join(", ", eagleStates)})");
                Check(eagle.GetBehavior<EntityBehaviorRaptor>().Saturated, "fed, so it isn't hunting (saturated, as vanilla predators are)");
            }, 70));
            steps.Enqueue(("eagle back to the perch", () =>
            {
                eagleStates.Clear(); eagleHits = eagleLow = 0;
                bool ok = eagle.GetBehavior<EntityBehaviorRaptor>().GoPerch();
                var spot = eagle.GetBehavior<EntityBehaviorRaptor>().PerchSpot;
                perchAim = spot;
                Check(ok && spot != null, $"found a perch within 64 blocks ({(spot == null ? "none" : $"{spot.X - c.X:0},{spot.Y - y0:0},{spot.Z - c.Z:0}")})");
            }, () => RState(eagle) == "Perch" || Elapsed > 60, () =>
            {
                // on the perch it chose (it may have picked again, if the first was out of reach)
                var aim = eagle.GetBehavior<EntityBehaviorRaptor>().PerchSpot ?? perchAim;
                Check(RState(eagle) == "Perch" && aim != null && Math.Abs(eagle.Pos.Y - aim.Y) < 0.4 && eagle.Pos.HorDistanceTo(aim) < 1.5,
                    $"landed on it ({RState(eagle)} at {eagle.Pos.X - c.X:0.0},{eagle.Pos.Y - y0:0.0},{eagle.Pos.Z - c.Z:0.0}; " +
                    $"perch {aim?.X - c.X:0.0},{aim?.Y - y0:0.0},{aim?.Z - c.Z:0.0}{(aim == perchAim ? "" : ", chosen again")})");
                Check(eagleHits == 0, $"no collisions on the way ({eagleHits})");
            }, 70));

            // ---- the eagle hunts mallards on pond A: they may see it coming; either way the flock is disturbed
            FlockRecord hunted = null;
            var huntedDucks = new List<Entity>();
            steps.Enqueue(("eagle hunts mallards", () =>
            {
                FlockRegistry.Instance.Calm(); huntedDucks.Clear(); eagleStates.Clear();
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "mallard"));
                hunted = FlockRegistry.Instance.Create("mallard", 6, c);
                testFlock = hunted.Id;
                for (int i = 0; i < 6; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, hunted.Id);
                    e.Pos.SetPos(c.X - 40 + (i % 3) * 2, y0 + 0.8, c.Z + (i / 3) * 2);   // the middle of pond A
                    sapi.World.SpawnEntity(e); huntedDucks.Add(e);
                }
                eagle.TeleportToDouble(c.X - 15, y0 + 36, c.Z);   // soaring 25 blocks off, 35 up
            }, () =>
            {
                if (Elapsed < 3) return false;
                if (Elapsed < 3.2) Check(eagle.GetBehavior<EntityBehaviorRaptor>().Hunt(huntedDucks[0]), "eagle stoops on a mallard");
                return (eagleStates.Contains("Stoop") && RState(eagle) != "Stoop") || Elapsed > 40;
            }, () =>
            {
                int dead = huntedDucks.Count(e => !e.Alive), flew = huntedDucks.Count(e => e.Alive && Phase(e) != "Water");
                double w = FlockRegistry.Instance.Wariness(hunted.Id);
                var rb = eagle.GetBehavior<EntityBehaviorRaptor>();
                Log($"mallard hunt: {dead} caught, {flew} flying, eagle now {RState(eagle)} for {rb.StateTime:0.0} s, diving {rb.Diving}, at site " +
                    $"{eagle.Pos.X - c.X:0.0},{eagle.Pos.Y - y0:0.0},{eagle.Pos.Z - c.Z:0.0}, motion {eagle.Pos.Motion.Length() * 60:0.0} b/s, tracked {eagle.IsTracked}, " +
                    $"prey {(rb.Prey == null ? "none" : $"{rb.Prey.Pos.X - c.X:0.0},{rb.Prey.Pos.Y - y0:0.0},{rb.Prey.Pos.Z - c.Z:0.0}")} " +
                    $"(states {string.Join(", ", eagleStates)}), flock wariness {w:0.0}");
                Check(eagleStates.Contains("Stoop") && RState(eagle) != "Stoop", $"the stoop ended ({RState(eagle)})");
                Check(w >= 1, $"the attack disturbed the flock (wariness {w:0.0}; they leave the lake at {new FlocksServerConfig().LeaveAt})");
                Check(dead == 0 || RState(eagle) == "ToPerch" || RState(eagle) == "Feed" || RState(eagle) == "Takeoff",
                    "a duck caught on the water is carried off, not left floating");
            }, 50));

            // ---- herring gulls: the waterfowl AI on the sea (pond C is salt water): bold, wheeling, loafing
            FlockRecord gulls = null;
            double gullWary = 0;
            steps.Enqueue(("gull settle", () =>
            {
                FlockRegistry.Instance.Calm();
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "gull"));
                gulls = FlockRegistry.Instance.Create("gull", 6, c);
                testFlock = gulls.Id;
                for (int i = 0; i < 6; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, gulls.Id);
                    e.Pos.SetPos(c.X + 55 - 3 + (i % 3) * 3, y0 + 0.8, c.Z - 45 + (i / 3) * 3);
                    sapi.World.SpawnEntity(e);
                }
            }, () => Elapsed > 8, () =>
            {
                var g = LoadedGeese();
                Check(g.Count == 6 && g.All(e => Phase(e) == "Water" && (e.FeetInLiquid || e.Swimming) && OnSea(e)),
                    $"6 gulls floating on the sea ({string.Join(" ", g.Select(e => Phase(e) + "@" + Where(e)))})");
            }, 20));
            steps.Enqueue(("sea and lakes kept apart", () => { }, () => true, () =>
            {
                var ba = sapi.World.BlockAccessor;
                var fromC = new Vec3d(c.X + 55.5, y0 + 1, c.Z - 44.5);
                var sea = LakeSearch.FindSpots(ba, fromC, c.X + 55, c.Z - 70, 100, new Random(2), "saltwater");
                Check(sea != null && sea.Count > 0 && sea.All(p => InPondC((int)Math.Floor(p.X), (int)Math.Floor(p.Z))), $"gulls find landing spots on the sea ({sea?.Count ?? 0})");
                var fresh = LakeSearch.FindSpots(ba, fromC, c.X + 55, c.Z - 70, 36, new Random(2));
                Check(fresh == null || fresh.All(p => !InPondC((int)Math.Floor(p.X), (int)Math.Floor(p.Z))), "geese and ducks never land on the sea");
                var at = new BlockPos((int)Math.Floor(fromC.X), y0, (int)Math.Floor(fromC.Z));
                Check(ResidentGeese.LakeSize(ba, at, 100, "saltwater") >= 100 && ResidentGeese.LakeSize(ba, at, 100) == 0, "the sea counts as water for gulls only");
                Check(ResidentGeese.InSeason(sapi.World, at, sapi.ModLoader.GetModSystem<FlocksModSystem>().SpeciesByCode["gull"]), "gulls are in season (all year)");
            }, 5));
            steps.Enqueue(("gull flush", () =>
            {
                ResetStats();
                LoadedGeese()[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
            }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                var g = LoadedGeese();
                Check(stats.Values.Count(st => st.Samples > 0) == 6, $"the whole group flew ({stats.Values.Count(st => st.Samples > 0)} of 6)");
                double turned = stats.Count == 0 ? 0 : stats.Values.Max(st => st.Turned) / (2 * Math.PI);
                Check(turned >= 1, $"wheeled over the water before settling (most turning: {turned:0.0} circles; per gull {string.Join(" ", stats.Values.Select(st => (st.Turned / (2 * Math.PI)).ToString("0.0")))})");
                Check(g.All(OnSea), $"came down on the sea again ({string.Join(" ", g.Select(Where))})");
                gullWary = FlockRegistry.Instance.Wariness(gulls.Id);
                Check(Math.Abs(gullWary - 0.5) < 0.05, $"a flush disturbs bold gulls half as much as geese (wariness {gullWary:0.00})");
                ReportFlight("gull flush", 0);
            }, 120));
            steps.Enqueue(("gull loafing flight", () =>
            {
                ResetStats();
                gullWary = FlockRegistry.Instance.Wariness(gulls.Id);
                Check(LoadedGeese()[0].GetBehavior<EntityBehaviorWaterfowl>().Loaf(), "a gull goes up of its own accord");
            }, () => Elapsed > 4 && LoadedGeese().All(e => Phase(e) == "Water"), () =>
            {
                var g = LoadedGeese();
                Check(stats.Values.Count(st => st.Samples > 0) == 6, $"the whole group went up ({stats.Values.Count(st => st.Samples > 0)} of 6)");
                double w = FlockRegistry.Instance.Wariness(gulls.Id);
                Check(w <= gullWary + 0.01, $"a loafing flight is no disturbance (wariness {gullWary:0.00} -> {w:0.00})");
                Check(g.All(OnSea), $"back on the sea ({string.Join(" ", g.Select(Where))})");
                ReportFlight("gull loaf", 0);
                foreach (var e in g) e.Die(EnumDespawnReason.Expire);
            }, 120));

            // ---- crows: feeding on open ground, raiding crops, the scarecrow, up into the trees and back down
            FlockRecord crows = null, guarded = null;
            var crowList = new List<Entity>();
            int f1x = (int)c.X - 2, f1z = (int)c.Z + 60, f2x = (int)c.X - 52, f2z = (int)c.Z + 62;
            Entity dummy = null;
            System.Func<Entity, EntityBehaviorCorvid> CB = e => e.GetBehavior<EntityBehaviorCorvid>();
            System.Func<FlockRecord, int, int, List<Entity>> spawnCrows = (rec, x, z) =>
            {
                var type = sapi.World.GetEntityType(new AssetLocation("flocks", "crow"));
                var list = new List<Entity>();
                for (int i = 0; i < 5; i++)
                {
                    var e = sapi.World.ClassRegistry.CreateEntity(type);
                    e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, rec.Id);
                    e.Pos.SetPos(x + 0.5 + (i % 3) * 1.5 - 1.5, y0 + 1, z + 0.5 + (i / 3) * 1.5);
                    sapi.World.SpawnEntity(e); list.Add(e);
                }
                return list;
            };
            steps.Enqueue(("crow settle", () =>
            {
                FlockRegistry.Instance.Calm();
                crows = FlockRegistry.Instance.Create("crow", 5, c);
                testFlock = crows.Id;
                crowList = spawnCrows(crows, (int)c.X, (int)c.Z + 72);
            }, () => Elapsed > 6, () =>
            {
                Check(crowList.All(e => e.Alive && CB(e)?.StateName == "Forage" && e.OnGround),
                    $"5 crows feeding on the ground ({string.Join(" ", crowList.Select(e => $"{CB(e)?.StateName}{(e.OnGround ? "" : "(air)")}"))})");
                var ba = sapi.World.BlockAccessor;
                Check(EntityBehaviorCorvid.OpenGround(ba, (int)c.X, (int)c.Z + 72, out _) && !EntityBehaviorCorvid.OpenGround(ba, (int)c.X + 15, (int)c.Z + 66, out _)
                      && !EntityBehaviorCorvid.OpenGround(ba, (int)c.X + 30, (int)c.Z, out _), "open ground is the soil field, not under the oak nor bare stone");
            }, 15));
            steps.Enqueue(("crow raid", () =>
            {
                foreach (var e in crowList) CB(e)?.RaidNow();
            }, () => CropStages(f1x, f1z).Any(s => s < 5) || Elapsed > 60, () =>
            {
                var st = CropStages(f1x, f1z);
                var ba = sapi.World.BlockAccessor;
                var dead = new List<string>();
                for (int x = f1x; x < f1x + 5; x++)
                    for (int z = f1z; z < f1z + 5; z++)
                    {
                        var b = ba.GetBlock(new BlockPos(x, y0 + 1, z));
                        if (b?.CropProps == null) dead.Add(b?.Code?.Path ?? "air");
                    }
                Check(dead.Count >= 1 && dead.All(p => p.StartsWith("deadcrop")) && st.All(s => s == 5 || s == 0),
                    $"crows ate crops the vanilla way: dead crops ({dead.Count}: {string.Join(" ", dead)}), the rest untouched");
                int raids = crowList.Sum(e => CB(e)?.Raids ?? 0);
                Check(raids == dead.Count, $"one crop per raid ({raids} raids, {dead.Count} dead crops)");
            }, 70));
            Entity walker = null;
            double walkedFrom = 0, flewAt = -1;
            steps.Enqueue(("crows flee a player", () =>
            {
                // a stand-in player, sensed as one (EntityBehaviorBird.TestPlayers), walking in from 30 blocks
                walker = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "chicken-hen")));
                double cx = crowList.Average(e => e.Pos.X), cz = crowList.Average(e => e.Pos.Z);
                walker.Pos.SetPos(cx - 30, y0 + 1, cz);
                sapi.World.SpawnEntity(walker);
                EntityBehaviorBird.TestPlayers.Add(walker);
                walkedFrom = crowList.Average(e => e.Pos.X);
            }, () =>
            {
                double cx = crowList.Average(e => e.Pos.X), cz = crowList.Average(e => e.Pos.Z);
                double d = Math.Sqrt((cx - walker.Pos.X) * (cx - walker.Pos.X) + (cz - walker.Pos.Z) * (cz - walker.Pos.Z));
                if (flewAt < 0 && crowList.Any(e => CB(e)?.StateName != "Forage")) flewAt = d;
                if (flewAt < 0) walker.Pos.SetPos(walker.Pos.X + 1, walker.Pos.Y, walker.Pos.Z);   // 2 blocks a second
                return crowList.All(e => CB(e)?.StateName == "Perch") || Elapsed > 40;
            }, () =>
            {
                Check(crowList.All(e => CB(e)?.StateName == "Perch"), $"the crows flew up to perches as the player came near ({string.Join(" ", crowList.Select(e => CB(e)?.StateName))})");
                Check(flewAt >= 6 && flewAt <= 14, $"they went up at about 10 blocks ({flewAt:0.0})");
                EntityBehaviorBird.TestPlayers.Remove(walker);
                walker.Die(EnumDespawnReason.Expire);
            }, 50));
            steps.Enqueue(("crow flush", () =>
            {
                FlockRegistry.Instance.Calm();
                crowList[0].ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = threat, Type = EnumDamageType.BluntAttack }, 1);
            }, () => crowList.All(e => CB(e)?.StateName == "Perch") || Elapsed > 40, () =>
            {
                Check(crowList.All(e => CB(e)?.StateName == "Perch"), $"the group flew up to perches ({string.Join(" ", crowList.Select(e => CB(e)?.StateName))})");
                Check(crowList.All(e => e.Pos.Y > y0 + 5), $"up off the ground ({string.Join(" ", crowList.Select(e => (e.Pos.Y - y0).ToString("0.0")))} above the slab)");
                Check(crowList.Count(e => Math.Abs(e.Pos.X - (c.X + 15)) < 2.5 && Math.Abs(e.Pos.Z - (c.Z + 66)) < 2.5) >= 3,
                    $"most in the oak ({string.Join(" ", crowList.Select(e => $"{e.Pos.X - c.X:0},{e.Pos.Y - y0:0},{e.Pos.Z - c.Z:0}"))})");
                double w = FlockRegistry.Instance.Wariness(crows.Id);
                Check(Math.Abs(w - 1) < 0.05, $"a flush disturbs the flock (wariness {w:0.00})");
            }, 50));
            steps.Enqueue(("crow back down", () => { }, () => crowList.All(e => CB(e)?.StateName == "Forage" && e.OnGround) || Elapsed > 120, () =>
            {
                var ba = sapi.World.BlockAccessor;
                Check(crowList.All(e => CB(e)?.StateName == "Forage" && e.OnGround), $"back down feeding ({string.Join(" ", crowList.Select(e => CB(e)?.StateName))})");
                Check(crowList.All(e => EntityBehaviorCorvid.OpenGround(ba, (int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z), out _) || CropStages(f1x, f1z).Count > 0 && Math.Abs(e.Pos.Z - c.Z - 62) < 4),
                    $"on open ground ({string.Join(" ", crowList.Select(e => $"{e.Pos.X - c.X:0},{e.Pos.Z - c.Z:0}"))})");
                foreach (var e in crowList) e.Die(EnumDespawnReason.Expire);
            }, 130));
            steps.Enqueue(("scarecrow", () =>
            {
                FlockRegistry.Instance.Calm();
                dummy = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "strawdummy")));
                dummy.Pos.SetPos(f2x + 2.5, y0 + 1, f2z - 3.5);
                sapi.World.SpawnEntity(dummy);
                guarded = FlockRegistry.Instance.Create("crow", 5, c);
                testFlock = guarded.Id;
                crowList = spawnCrows(guarded, f2x + 2, f2z + 22);    // 22 blocks from the bed, 25 from the dummy
            }, () =>
            {
                if (Elapsed > 2 && Elapsed < 2.6) foreach (var e in crowList) CB(e)?.RaidNow();
                return Elapsed > 30;
            }, () =>
            {
                var st = CropStages(f2x, f2z);
                Check(dummy.Alive && st.All(s => s == 5), $"no crop within the straw dummy's 15 blocks was touched (stages {string.Join("", st)})");
                Check(crowList.Sum(e => CB(e)?.Raids ?? 0) == 0 && crowList.All(e => CB(e)?.Crop == null), "no crow went for them");
                var spots = EntityBehaviorCorvid.GroundSpots(sapi.World, new Vec3d(f2x + 2.5, y0 + 1, f2z + 2.5), c.X, c.Z - 200, 0, 0, new Random(3));
                Check(spots != null && spots.All(p => p.HorizontalSquareDistanceTo(dummy.Pos.X, dummy.Pos.Z) > 15 * 15),
                    $"crows don't come down to feed near it ({spots?.Count ?? 0} spots offered, nearest {(spots == null || spots.Count == 0 ? 0 : spots.Min(p => Math.Sqrt(p.HorizontalSquareDistanceTo(dummy.Pos.X, dummy.Pos.Z)))):0.0} blocks from it)");
                Check(Scarecrows.Protects(sapi.World, new Vec3d(f2x + 2.5, y0 + 1, f2z + 2.5), 15) && !Scarecrows.Protects(sapi.World, new Vec3d(f1x, y0 + 1, f1z), 15),
                    "the straw dummy protects its own field and not the other");
            }, 40));
            steps.Enqueue(("crows leave a scarecrow", () =>
            {
                foreach (var e in crowList) e.Die(EnumDespawnReason.Expire);
                crowList = spawnCrows(guarded, f2x + 2, f2z + 4);      // about 8 blocks from the straw dummy
            }, () => Elapsed > 3 && crowList.All(e => CB(e)?.StateName == "Forage" && e.Pos.HorDistanceTo(dummy.Pos.XYZ) > 15) || Elapsed > 40, () =>
            {
                Check(crowList.All(e => CB(e)?.StateName == "Forage" && e.Pos.HorDistanceTo(dummy.Pos.XYZ) > 15),
                    $"crows put down by a straw dummy fly off and feed out of its reach ({string.Join(" ", crowList.Select(e => $"{CB(e)?.StateName} {e.Pos.HorDistanceTo(dummy.Pos.XYZ):0}"))} blocks from it)");
                Check(FlockRegistry.Instance.Wariness(guarded.Id) < 0.01, "the scarecrow isn't counted as a disturbance");
                Check(CropStages(f2x, f2z).All(s => s == 5), "and they leave its crops alone");
            }, 50));
            steps.Enqueue(("crows roost at dusk", () =>
            {
                // the clock on to 20:00
                double h = sapi.World.Calendar.HourOfDay;
                sapi.World.Calendar.Add((float)((20.2 - h + 24) % 24));
            }, () => crowList.All(e => CB(e)?.StateName == "Perch" && CB(e).Roosting) || Elapsed > 45, () =>
            {
                Check(crowList.All(e => CB(e)?.StateName == "Perch" && CB(e).Roosting),
                    $"up in the trees for the night at {sapi.World.Calendar.HourOfDay:0.0} h ({string.Join(" ", crowList.Select(e => CB(e)?.StateName + (CB(e)?.Roosting == true ? "(roost)" : "")))})");
            }, 50));
            steps.Enqueue(("crows down at dawn", () =>
            {
                double h = sapi.World.Calendar.HourOfDay;
                sapi.World.Calendar.Add((float)((7 - h + 24) % 24));
            }, () => crowList.All(e => CB(e)?.StateName == "Forage") || Elapsed > 60, () =>
            {
                Check(crowList.All(e => CB(e)?.StateName == "Forage" && !CB(e).Roosting), $"down again in the morning ({string.Join(" ", crowList.Select(e => CB(e)?.StateName))})");
                foreach (var e in crowList) e.Die(EnumDespawnReason.Expire);
            }, 70));

            QueueOwlSteps();

            steps.Enqueue(("memory", () => { }, () => true, () =>
            {
                Check(memory.Count >= 3 && memory.Last() - memory[1] < 64, $"managed memory over cycles (MB): {string.Join(", ", memory)}");
                int left = sapi.World.LoadedEntities.Values.Count(e => e.Alive && e.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, -1) == flock.Id);
                Check(left == 7, $"still 7 geese after the cycles ({left})");
            }, 5));
        }

        // ------------------------------------------------------------------------------------------------ owls

        private Entity owl;
        private int owlHits, owlSamples;
        private string owlLastState;
        private readonly HashSet<string> owlStates = new();
        private static EntityBehaviorOwl OB(Entity e) => e?.GetBehavior<EntityBehaviorOwl>();

        /// <summary>Which owl tree a spot is in (1 or 2), 0 if neither.</summary>
        private int InOwlTree(Vec3d p) => Math.Abs(p.Z - OwlTreeZ - 0.5) <= 3.6 ? (Math.Abs(p.X - OwlTree1X - 0.5) <= 3.6 ? 1 : Math.Abs(p.X - OwlTree2X - 0.5) <= 3.6 ? 2 : 0) : 0;
        private string Site(Vec3d p) => p == null ? "-" : $"{p.X - c.X:0.0},{p.Y - y0:0.0},{p.Z - c.Z:0.0}";

        /// <summary>Standing on a branch (branchy leaves or wood) with leaves over it.</summary>
        private bool OnBranch(Entity e, out string what)
        {
            var ba = sapi.World.BlockAccessor;
            int x = (int)Math.Floor(e.Pos.X), z = (int)Math.Floor(e.Pos.Z);
            bool ok = EntityBehaviorOwl.PerchBelowLeaves(ba, x, z, new BlockPos(0, 0, 0), out int y, out int top, out bool branch);
            what = $"{ba.GetBlock(new BlockPos(x, y, z))?.Code?.Path} at {y - y0} under leaves to {top - y0}, owl feet {e.Pos.Y - y0:0.00}";
            return ok && branch && top > y && Math.Abs(e.Pos.Y - (y + 1)) < 0.35;
        }

        /// <summary>Hunting in the open, where it can be seen: nothing at its head (no leaves), standing on the top of the
        /// column (leaves, a branch, a post).</summary>
        private bool InTheOpen(Entity e, out string what)
        {
            var ba = sapi.World.BlockAccessor;
            int x = (int)Math.Floor(e.Pos.X), z = (int)Math.Floor(e.Pos.Z), top = ba.GetRainMapHeightAt(x, z);
            var head = ba.GetBlock(new BlockPos(x, (int)Math.Floor(e.Pos.Y + 0.3), z));
            what = $"on {ba.GetBlock(new BlockPos(x, top, z))?.Code?.Path} at {top - y0}, at its head {head?.Code?.Path}, owl feet {e.Pos.Y - y0:0.00}";
            return (head == null || head.Id == 0) && Math.Abs(e.Pos.Y - (top + 1)) < 0.35;
        }

        private void SetHour(double hour)
        {
            double h = sapi.World.Calendar.HourOfDay;
            sapi.World.Calendar.Add((float)((hour - h + 24) % 24));
        }

        private void QueueOwlSteps()
        {
            var mice = new List<Entity>();
            Entity walker = null;
            double flushedAt = -1;
            steps.Enqueue(("owl roosts by day", () =>
            {
                SetHour(12);
                owl = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("flocks", "owl")));
                owl.Pos.SetPos(OwlTree1X + 8.5, y0 + 1, OwlTreeZ + 6.5);
                sapi.World.SpawnEntity(owl);
                owlHits = 0; owlSamples = 0; owlStates.Clear();
            }, () => OB(owl)?.StateName == "Roost" || Elapsed > 40, () =>
            {
                bool branch = OnBranch(owl, out string what);
                Check(OB(owl)?.StateName == "Roost" && branch && InOwlTree(owl.Pos.XYZ) > 0,
                    $"by day it flew up to roost on a branch in a tree, under the leaves ({OB(owl)?.StateName}, tree {InOwlTree(owl.Pos.XYZ)}; {what})");
                Check(owl.Pos.HorDistanceTo(new Vec3d(InOwlTree(owl.Pos.XYZ) == 2 ? OwlTree2X + 0.5 : OwlTree1X + 0.5, 0, OwlTreeZ + 0.5)) <= 2.3,
                    $"close to the trunk ({Site(owl.Pos.XYZ)})");
                Check(owlHits == 0, $"no collisions in flight ({owlHits} of {owlSamples} samples)");
            }, 50));
            steps.Enqueue(("owl stays on its roost", () => { }, () => Elapsed > 8, () =>
            {
                bool branch = OnBranch(owl, out string what);
                Check(OB(owl)?.StateName == "Roost" && branch, $"still roosting 8 s later, standing on the branch ({OB(owl)?.StateName}; {what})");
            }, 15));
            steps.Enqueue(("owl sits tight", () =>
            {
                walker = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game", "chicken-hen")));
                walker.Pos.SetPos(owl.Pos.X + 20, y0 + 1, owl.Pos.Z);
                sapi.World.SpawnEntity(walker);
                EntityBehaviorBird.TestPlayers.Add(walker);
                flushedAt = -1; owlHits = 0; owlSamples = 0;
            }, () =>
            {
                double d = walker.Pos.HorDistanceTo(owl.Pos.XYZ);
                if (flushedAt < 0 && OB(owl)?.StateName != "Roost") flushedAt = d;
                if (flushedAt < 0 && walker.Pos.X > owl.Pos.X - 1) walker.Pos.SetPos(walker.Pos.X - 1, walker.Pos.Y, walker.Pos.Z);   // 2 blocks a second
                return flushedAt >= 0 && OB(owl)?.StateName == "Roost" || Elapsed > 60;
            }, () =>
            {
                Check(flushedAt >= 0 && flushedAt <= 5.5, $"it let the player come nearly under it before flying ({flushedAt:0.0} blocks)");
                bool branch = OnBranch(owl, out string what);
                Check(OB(owl)?.StateName == "Roost" && branch && owl.Pos.HorDistanceTo(walker.Pos.XYZ) >= 14,
                    $"and roosted again on a branch away from them ({OB(owl)?.StateName}, {owl.Pos.HorDistanceTo(walker.Pos.XYZ):0} blocks off, tree {InOwlTree(owl.Pos.XYZ)}; {what})");
                Check(owlHits == 0, $"no collisions in flight ({owlHits} of {owlSamples} samples)");
                EntityBehaviorBird.TestPlayers.Remove(walker);
                walker.Die(EnumDespawnReason.Expire);
            }, 70));
            steps.Enqueue(("owl goes hunting at dusk", () => { SetHour(20.5); owlHits = 0; owlSamples = 0; },
                () => OB(owl)?.StateName == "Perch" || Elapsed > 40, () =>
            {
                bool open = InTheOpen(owl, out string what);
                Check(OB(owl)?.StateName == "Perch" && open && InOwlTree(owl.Pos.XYZ) > 0,
                    $"after dusk it hunts from the outside of a crown, in the open where it can be seen ({OB(owl)?.StateName}, tree {InOwlTree(owl.Pos.XYZ)}; {what})");
                Check(!OnBranch(owl, out _), "not hidden inside the leaves");
                Check(owlHits == 0, $"no collisions in flight ({owlHits} of {owlSamples} samples)");
            }, 50));
            // Two mice out on the grass, 7 blocks out from the owl's perch, away from the trunk. A mouse sees the owl at the
            // last moment and may get away (as it should); whenever the owl is back on a perch with no mouse within reach,
            // two more come out near it, up to 6 in all, as on a real night.
            System.Action putMice = () =>
            {
                double tx = (InOwlTree(owl.Pos.XYZ) == 2 ? OwlTree2X : OwlTree1X) + 0.5, tz = OwlTreeZ + 0.5;
                double ax = owl.Pos.X - tx, az = owl.Pos.Z - tz, al = Math.Sqrt(ax * ax + az * az);
                if (al < 0.5) { ax = 1; az = 0; al = 1; }
                var mt = sapi.World.GetEntityType(new AssetLocation("flocks", "mouse"));
                var added = new List<Entity>();
                for (int i = 0; i < 2; i++)
                {
                    var m = sapi.World.ClassRegistry.CreateEntity(mt);
                    double side = i == 0 ? -1.5 : 1.5;
                    // kept inside the soil clearing (the slab ends at 100 blocks out, with a long drop beyond)
                    double mx = GameMath.Clamp(owl.Pos.X + ax / al * 7 - az / al * side, c.X + 31, c.X + 71);
                    double mz = GameMath.Clamp(owl.Pos.Z + az / al * 7 + ax / al * side, c.Z + 79, c.Z + 97);
                    m.Pos.SetPos(mx, y0 + 1, mz);
                    sapi.World.SpawnEntity(m); mice.Add(m); added.Add(m); extraPhysics.Add(m);
                }
                Log($"mice at {string.Join(" ", added.Select(m => Site(m.Pos.XYZ)))}, owl at {Site(owl.Pos.XYZ)}");
            };
            steps.Enqueue(("owl catches a mouse", () =>
            {
                putMice();
                owlStates.Clear();
            }, () =>
            {
                if (OB(owl)?.StateName == "Perch" && OB(owl)?.Catches == 0 && mice.Count < 6
                    && !mice.Any(m => m.Alive && m.Pos.HorDistanceTo(owl.Pos.XYZ) < 16)) putMice();
                return OB(owl)?.Catches > 0 && OB(owl)?.StateName != "Feed" || Elapsed > 170;
            }, () =>
            {
                Check(OB(owl)?.Catches > 0, $"it dropped onto a mouse and caught it ({OB(owl)?.Catches} catches of {OB(owl)?.Pounces} pounces, {mice.Count} mice out; states {string.Join(", ", owlStates)})");
                Check(owlStates.Contains("Feed"), "and ate it on the ground");
                int inWorld = mice.Count(m => sapi.World.GetEntityById(m.EntityId) != null);
                Check(inWorld == mice.Count - (OB(owl)?.Catches ?? 0),
                    $"swallowed whole: the caught mouse is gone ({mice.Count(m => m.Alive)} alive, {inWorld} still in the world)");
                Check(OB(owl)?.Saturated == true, "fed, so it isn't hunting for a while (saturated, as vanilla predators are)");
            }, 180));
            steps.Enqueue(("owl back on a perch", () => { }, () => OB(owl)?.StateName == "Perch" || Elapsed > 40, () =>
            {
                bool open = InTheOpen(owl, out string what);
                Check(OB(owl)?.StateName == "Perch" && open, $"back up on a perch in the open after eating ({OB(owl)?.StateName}; {what})");
            }, 50));
            steps.Enqueue(("owl calls at night", () => { }, () => OB(owl)?.CallsMade > 0 || Elapsed > 90, () =>
            {
                Check(OB(owl)?.CallsMade > 0, $"it hooted or called during the night ({OB(owl)?.CallsMade} calls)");
            }, 100));
            steps.Enqueue(("owl roosts at dawn", () => { SetHour(6.5); owlHits = 0; owlSamples = 0; }, () => OB(owl)?.StateName == "Roost" || Elapsed > 50, () =>
            {
                bool branch = OnBranch(owl, out string what);
                Check(OB(owl)?.StateName == "Roost" && branch && InOwlTree(owl.Pos.XYZ) > 0,
                    $"back to roost in a tree in the morning ({OB(owl)?.StateName}, tree {InOwlTree(owl.Pos.XYZ)}; {what})");
                Check(owlHits == 0, $"no collisions in flight ({owlHits} of {owlSamples} samples)");
                var ba = sapi.World.BlockAccessor;
                Check(EntityBehaviorOwl.HasRoostNear(ba, new Vec3d(OwlTree1X + 10, y0 + 1, OwlTreeZ)) && !EntityBehaviorOwl.HasRoostNear(ba, c),
                    "owls only spawn with a tree to roost in nearby");
                owl.Die(EnumDespawnReason.Expire);
                foreach (var m in mice) if (m.Alive) m.Die(EnumDespawnReason.Expire);
            }, 60));
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
            // FLOCKS_TEST_ONLY=owl runs only the steps whose names contain it (the site is built either way)
            string only = Environment.GetEnvironmentVariable("FLOCKS_TEST_ONLY");
            if (!string.IsNullOrEmpty(only) && !s.name.Contains(only) && s.name != "memory") { NextStep(); return; }
            if (!string.IsNullOrEmpty(only) && s.name == "memory") { Finish(); return; }
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
        /// <summary>Other entities the test needs moving with nobody around (a thrown spear).</summary>
        private readonly List<Entity> extraPhysics = new();
        /// <summary>What PhysicsManager does for a tracked entity: OnPhysicsTick at 1/30 s steps, then AfterPhysicsTick.</summary>
        private void Physics(float dt)
        {
            physicsAcc = Math.Min(physicsAcc + dt, 0.2);
            const float step = 1 / 30f;
            int steps = 0;
            for (; physicsAcc >= step; physicsAcc -= step) steps++;
            if (steps == 0) return;
            var tick = LoadedGeese();
            if (eagle != null && eagle.Alive) tick.Add(eagle);
            if (owl != null && owl.Alive) tick.Add(owl);
            tick.AddRange(extraPhysics.Where(e => e.Alive));
            foreach (var e in tick)
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

        private Entity eagle;
        private int eagleHits, eagleLow, eagleSamples;
        private double eagleTurned, eagleLastYaw = double.NaN, eagleMaxAlt, eagleMinAlt = double.MaxValue;
        private double eagleMaxStoop, eagleMealDist = double.NaN, eagleFeedSince = -1;
        private readonly HashSet<string> eagleStates = new();
        private int soarSince;
        private static string RState(Entity e) => e?.GetBehavior<EntityBehaviorRaptor>()?.StateName ?? "?";

        private void Sample(float dt)
        {
            var ba = sapi.World.BlockAccessor;
            if (owl != null && owl.Alive)
            {
                string st = OB(owl)?.StateName;
                owlStates.Add(st);
                if (st != owlLastState)
                {
                    owlLastState = st;
                    Log($"owl -> {st} at {Site(owl.Pos.XYZ)}, target {Site(OB(owl)?.Target)}{(owl.OnGround ? ", on ground" : "")}");
                }
                if (st == "Fly" || st == "Takeoff")
                {
                    owlSamples++;
                    if (owl.CollidedHorizontally) { owlHits++; if (owlHits == 1) Log($"owl hit: {st} at site {owl.Pos.X - c.X:0.0},{owl.Pos.Y - y0:0.0},{owl.Pos.Z - c.Z:0.0}, ahead {Ahead(ba, owl, 1)}"); }
                }
            }
            if (eagle != null && eagle.Alive)
            {
                string st = RState(eagle);
                eagleStates.Add(st);
                if (st == "Stoop") eagleMaxStoop = Math.Max(eagleMaxStoop, eagle.Pos.Motion.Length() * 60);
                var meal = eagle.GetBehavior<EntityBehaviorRaptor>()?.Meal;
                if (st == "Feed")
                {
                    if (eagleFeedSince < 0) eagleFeedSince = sapi.World.ElapsedMilliseconds / 1000.0;
                    if (meal != null) eagleMealDist = eagle.Pos.HorDistanceTo(meal.Pos);
                }
                if (st != "Perch" && st != "Feed")
                {
                    eagleSamples++;
                    if (eagle.CollidedHorizontally) eagleHits++;
                    int gx = (int)Math.Floor(eagle.Pos.X), gz = (int)Math.Floor(eagle.Pos.Z), top = ba.GetRainMapHeightAt(gx, gz);
                    if (top > 0 && eagle.Pos.Y < top + 1 - 0.3 && st != "Stoop" && st != "ToPerch")
                    {
                        eagleLow++;
                        if (eagleLow == 1) Log($"eagle low: {st} at site {eagle.Pos.X - c.X:0.0},{eagle.Pos.Y - y0:0.0},{eagle.Pos.Z - c.Z:0.0}; top {top + 1 - y0}");
                    }
                    if (st == "Soar" && soarSince++ > 100)
                    {
                        if (!double.IsNaN(eagleLastYaw)) eagleTurned += Math.Abs(GameMath.AngleRadDistance((float)eagleLastYaw, eagle.Pos.Yaw));
                        eagleLastYaw = eagle.Pos.Yaw;
                        double alt = eagle.Pos.Y - Math.Max(top + 1, 0);
                        eagleMaxAlt = Math.Max(eagleMaxAlt, alt); eagleMinAlt = Math.Min(eagleMinAlt, alt);
                    }
                }
            }
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
                if (ph == "Fly")
                {
                    if (!double.IsNaN(s.LastYaw)) s.Turned += Math.Abs(GameMath.AngleRadDistance((float)s.LastYaw, e.Pos.Yaw));
                    s.LastYaw = e.Pos.Yaw;
                }
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
