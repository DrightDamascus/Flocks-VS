using System;
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
        /// <summary>Safety cap: no new goose spawn when this many geese are already within NearbyRadius of the spot.
        /// Vanilla's own per-player limit (spawnconditions.runtime.maxQuantity) normally applies long before this.</summary>
        public int MaxGeeseNearby = 30;
        public int NearbyRadius = 96;
    }

    /// <summary>
    /// Resident geese spawn through vanilla's spawner (spawnconditions in entities/greylag.json: inside still water at
    /// the surface, climate limits, per-player maxQuantity). Vanilla spawnconditions don't know seasons or lake size,
    /// so this vetoes greylag spawns out of season, on small ponds, or where geese are already thick on the ground.
    /// The autumn departure is in EntityBehaviorWaterfowl.
    /// </summary>
    public class ResidentGeese
    {
        private readonly ICoreServerAPI sapi;
        public readonly FlocksServerConfig Cfg;
        public static readonly AssetLocation Code = new("flocks", "greylag");
        public int Allowed, VetoSeason, VetoLake, VetoCrowded, VetoOff;

        public ResidentGeese(ICoreServerAPI sapi, FlocksServerConfig cfg)
        {
            this.sapi = sapi; Cfg = cfg;
            sapi.Event.OnTrySpawnEntity += TrySpawn;
        }

        private bool TrySpawn(IBlockAccessor ba, ref EntityProperties properties, Vec3d pos, long herdId)
        {
            if (properties?.Code == null || !properties.Code.Equals(Code)) return true;
            try
            {
                if (!Cfg.ResidentGeese) { VetoOff++; return false; }
                var bp = pos.AsBlockPos;
                if (!InSeason(sapi.World, bp)) { VetoSeason++; return false; }
                if (LakeSize(ba, bp, Cfg.MinLakeSize) < Cfg.MinLakeSize) { VetoLake++; return false; }
                int near = sapi.World.GetEntitiesAround(pos, Cfg.NearbyRadius, 32, e => e.Alive && e.Code.Equals(Code)).Length;
                if (near >= Cfg.MaxGeeseNearby) { VetoCrowded++; return false; }
                Allowed++;
                return true;
            }
            catch (Exception ex) { sapi.Logger.Error("[Flocks] goose spawn check failed: {0}", ex); return false; }
        }

        public static bool InSeason(IWorldAccessor world, BlockPos bp)
        {
            var cal = world.Calendar;
            return MigrationScheduler.ResidentSeason(Species.GooseSpringStart, Species.GooseAutumnStart, cal.GetSeasonRel(bp), cal.OnGetLatitude(bp.Z));
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

        private static bool Surface(IBlockAccessor ba, int x, int z, out int y)
        {
            y = ba.GetRainMapHeightAt(x, z);
            if (y <= 0) return false;
            var b = ba.GetBlock(new BlockPos(x, y, z), BlockLayersAccess.Fluid);
            return b != null && b.IsLiquid() && b.LiquidCode == "water";
        }

        public string Report()
        {
            var sb = new StringBuilder($"Resident geese {(Cfg.ResidentGeese ? "on" : "OFF")} (vanilla spawning + season/lake veto). " +
                $"Spawn checks since start: {Allowed} allowed, vetoed {VetoSeason} out of season, {VetoLake} small lake, {VetoCrowded} crowded, {VetoOff} disabled.");
            var geese = sapi.World.LoadedEntities.Values.Where(e => e.Alive && e.Code.Equals(Code)).ToList();
            sb.Append($"\nLoaded geese: {geese.Count}");
            foreach (var p in sapi.World.AllOnlinePlayers)
            {
                if (p.Entity == null) continue;
                var bp = p.Entity.Pos.AsBlockPos;
                int near = geese.Count(e => e.Pos.HorDistanceTo(p.Entity.Pos.XYZ) <= Cfg.NearbyRadius);
                sb.Append($"\n  {p.PlayerName}: {near} geese within {Cfg.NearbyRadius} blocks, resident season here: {(InSeason(sapi.World, bp) ? "yes" : "no")}");
            }
            return sb.ToString();
        }
    }
}
