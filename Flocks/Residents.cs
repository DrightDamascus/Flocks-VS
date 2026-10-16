using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>Server config, ModConfig/flocks-server.json. Written with defaults on first run.</summary>
    public class FlocksServerConfig
    {
        /// <summary>Greylag geese spawn on lakes (vanilla runtime/worldgen spawning) during their resident season.</summary>
        public bool ResidentGeese = true;
        /// <summary>Smallest lake (fresh-water surface blocks around the spawn point) a goose spawn is allowed on.</summary>
        public int MinLakeSize = 36;
        /// <summary>No goose spawn further than this from shore (a solid non-water block at or just above the water
        /// surface, looked for in 8 directions). Floating geese more than 1.5x this far out steer back toward land.</summary>
        public int ShoreDistance = 20;
        /// <summary>Safety cap: no new goose spawn when this many geese are already within NearbyRadius of the spot.
        /// Vanilla's own per-player limit (spawnconditions.runtime.maxQuantity) normally applies long before this.</summary>
        public int MaxGeeseNearby = 30;
        public int NearbyRadius = 96;

        /// <summary>Lake wariness: disturbed flocks grow skittish and, disturbed often enough, abandon the lake for a while.
        /// Every flush adds 1 and a kill KillWariness more; it wears off by WarinessDecayPerDay per in-game day.</summary>
        public bool LakeWariness = true;
        /// <summary>From this wariness a flock flees and takes off from SkittishRangeFactor times further away.</summary>
        public double SkittishAt = 1, SkittishRangeFactor = 1.8;
        /// <summary>At this wariness the flock leaves the lake and rejoins the migration; no geese land or spawn within
        /// 64 blocks of it for AbandonDays in-game days. A careful hunter gets about two geese before they go.</summary>
        public double LeaveAt = 2.5, KillWariness = 0.5, WarinessDecayPerDay = 1, AbandonDays = 2;
    }

    /// <summary>
    /// Resident birds of every species that lands (its landedEntity, e.g. flocks:greylag) spawn through vanilla's spawner
    /// (spawnconditions in the entity file: inside still water at the surface, climate limits, maxQuantity). Vanilla
    /// spawnconditions don't know seasons or lake size, so this vetoes spawns out of the species' resident season, on
    /// small ponds, far from shore, near an abandoned lake, or where they are already thick on the ground.
    /// The autumn departure is in EntityBehaviorWaterfowl.
    /// </summary>
    public class ResidentGeese
    {
        private readonly ICoreServerAPI sapi;
        public readonly FlocksServerConfig Cfg;
        private readonly Dictionary<string, Species> species;
        public int Allowed, VetoSeason, VetoLake, VetoShore, VetoCrowded, VetoOff, VetoAbandoned;
        /// <summary>Config ShoreDistance, for EntityBehaviorWaterfowl (server side).</summary>
        public static int ShoreDistanceCfg = 20;

        public ResidentGeese(ICoreServerAPI sapi, FlocksServerConfig cfg, Dictionary<string, Species> species)
        {
            this.sapi = sapi; Cfg = cfg; this.species = species;
            ShoreDistanceCfg = Math.Max(1, cfg.ShoreDistance);
            sapi.Event.OnTrySpawnEntity += TrySpawn;
        }

        private bool TrySpawn(IBlockAccessor ba, ref EntityProperties properties, Vec3d pos, long herdId)
        {
            if (properties?.Code == null) return true;
            var code = properties.Code;
            var sp = ByEntity(species, code);
            if (sp == null) return true;
            try
            {
                if (!Cfg.ResidentGeese) { VetoOff++; return false; }
                var bp = pos.AsBlockPos;
                if (!InSeason(sapi.World, bp, sp)) { VetoSeason++; return false; }
                int minLake = sp.MinLake(Cfg.MinLakeSize);
                if (LakeSize(ba, bp, minLake) < minLake) { VetoLake++; return false; }
                if (ShoreDirection(ba, bp, ShoreDistanceCfg, out _) < 0) { VetoShore++; return false; }
                if (FlockRegistry.Instance?.IsAbandoned(pos.X, pos.Z) == true) { VetoAbandoned++; return false; }
                int near = sapi.World.GetEntitiesAround(pos, Cfg.NearbyRadius, 32, e => e.Alive && e.Code.Equals(code)).Length;
                if (near >= Cfg.MaxGeeseNearby) { VetoCrowded++; return false; }
                Allowed++;
                return true;
            }
            catch (Exception ex) { sapi.Logger.Error("[Flocks] {0} spawn check failed: {1}", code, ex); return false; }
        }

        /// <summary>The landing species whose landed entity this is, or null.</summary>
        public static Species ByEntity(Dictionary<string, Species> species, AssetLocation entityCode)
        {
            foreach (var sp in species.Values) if (sp.Lands && sp.LandedEntityCode.Equals(entityCode)) return sp;
            return null;
        }

        /// <summary>The species' resident season here: from its spring window to its autumn window (MigrationScheduler).</summary>
        public static bool InSeason(IWorldAccessor world, BlockPos bp, Species sp)
        {
            var cal = world.Calendar;
            return MigrationScheduler.ResidentSeason(sp.SpringStart, sp.AutumnStart, cal.GetSeasonRel(bp), cal.OnGetLatitude(bp.Z));
        }

        /// <summary>Fresh-water surface blocks connected to bp's column at its water level, counted up to <paramref name="enough"/>.</summary>
        public static int LakeSize(IBlockAccessor ba, BlockPos bp, int enough)
        {
            int sx = bp.X, sz = bp.Z;
            if (!Surface(ba, sx, sz, out int y)) return 0;
            var seen = new System.Collections.Generic.HashSet<long> { ((long)sx << 32) ^ (uint)sz };
            var q = new System.Collections.Generic.Queue<(int, int)>(); q.Enqueue((sx, sz));
            int n = 0;
            while (q.Count > 0 && n < enough)
            {
                var (x, z) = q.Dequeue(); n++;
                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                    if (seen.Add(((long)nx << 32) ^ (uint)nz) && Surface(ba, nx, nz, out int ny) && ny == y) q.Enqueue((nx, nz));
            }
            return n;
        }

        /// <summary>
        /// Distance to the nearest shore within maxDist along 8 directions (N, NE, ... from bp's water surface), with
        /// its heading (FlockRenderer convention, dir = (sin, cos)); -1 if none. Shore = a solid non-liquid block at the
        /// surface level or one above. Cost is at most 8 x maxDist x 2 block reads; it never floods the lake.
        /// </summary>
        public static int ShoreDirection(IBlockAccessor ba, BlockPos bp, int maxDist, out double heading)
        {
            heading = 0;
            int y = Surface(ba, bp.X, bp.Z, out int sy) ? sy : bp.Y;
            var tmp = new BlockPos(0, 0, 0);
            for (int d = 1; d <= maxDist; d++)
                for (int i = 0; i < 8; i++)
                {
                    double h = i * Math.PI / 4;
                    int x = bp.X + (int)Math.Round(Math.Sin(h) * d), z = bp.Z + (int)Math.Round(Math.Cos(h) * d);
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        tmp.Set(x, y + dy, z);
                        var b = ba.GetBlock(tmp);
                        if (b != null && b.Id != 0 && !b.IsLiquid() && b.CollisionBoxes != null && b.CollisionBoxes.Length > 0)
                        { heading = h; return d; }
                    }
                }
            return -1;
        }

        private static bool Surface(IBlockAccessor ba, int x, int z, out int y)
        {
            y = ba.GetRainMapHeightAt(x, z);
            if (y <= 0) return false;
            var b = ba.GetBlock(new BlockPos(x, y, z), BlockLayersAccess.Fluid);
            return b != null && b.IsLiquid() && b.LiquidCode == "water";
        }

        public string Report()
        {
            var sb = new StringBuilder($"Resident birds {(Cfg.ResidentGeese ? "on" : "OFF")} (vanilla spawning + season/lake veto). " +
                $"Spawn checks since start: {Allowed} allowed, vetoed {VetoSeason} out of season, {VetoLake} small lake, {VetoShore} too far from shore (>{Cfg.ShoreDistance}), {VetoCrowded} crowded, {VetoAbandoned} abandoned lake, {VetoOff} disabled.");
            foreach (var sp in species.Values.Where(s => s.Lands))
            {
                var code = sp.LandedEntityCode;
                var birds = sapi.World.LoadedEntities.Values.Where(e => e.Alive && e.Code.Equals(code)).ToList();
                sb.Append($"\nLoaded {sp.Name ?? sp.Code}: {birds.Count}");
                foreach (var p in sapi.World.AllOnlinePlayers)
                {
                    if (p.Entity == null) continue;
                    var bp = p.Entity.Pos.AsBlockPos;
                    int near = birds.Count(e => e.Pos.HorDistanceTo(p.Entity.Pos.XYZ) <= Cfg.NearbyRadius);
                    sb.Append($"\n  {p.PlayerName}: {near} within {Cfg.NearbyRadius} blocks, resident season here: {(InSeason(sapi.World, bp, sp) ? "yes" : "no")}");
                }
            }
            return sb.ToString();
        }
    }
}
