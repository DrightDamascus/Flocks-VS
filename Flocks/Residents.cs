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

        /// <summary>Crows raid crops: a crow near farmland now and then pecks at a growing crop and knocks it back one
        /// growth stage. Never within ScarecrowRadius blocks (horizontally) of a standing straw dummy, where crows don't
        /// come down to feed at all. Each crow raids at most once every CropRaidHoursMin to CropRaidHoursMax game hours.</summary>
        public bool CropRaiding = true;
        public double ScarecrowRadius = 15, CropRaidHoursMin = 3, CropRaidHoursMax = 6;
        /// <summary>Safety cap for land birds (crows): no new spawn when this many are already within NearbyRadius.</summary>
        public int MaxCrowsNearby = 16;

        /// <summary>Every animal of the mod, on or off. Off: it never spawns on its own, no landing flocks of it come in,
        /// /flocks land refuses it, and its passing flocks aren't drawn in any player's sky (the crane only ever passes
        /// over). Ones already in the world stay (a caged or kept bird isn't lost).</summary>
        public Dictionary<string, bool> Animals = new()
        {
            ["greylag"] = true, ["mallard"] = true, ["heron"] = true, ["eagle"] = true, ["gull"] = true,
            ["crow"] = true, ["owl"] = true, ["mouse"] = true, ["crane"] = true,
        };
        /// <summary>Golden eagles hunt chickens as well as hares and mallards. They only stoop on prey in the open, so a
        /// roofed run keeps chickens safe.</summary>
        public bool EaglesHuntChickens = false;

        /// <summary>Whether the animal (entity code path, e.g. "greylag"; "crane" for the passing cranes) is on.</summary>
        public bool AnimalEnabled(string code) => Animals == null || !Animals.TryGetValue(code, out bool on) || on;
        /// <summary>The same for a species: its landed entity, or its own code for a bird that only passes over.</summary>
        public bool SpeciesEnabled(Species sp) => AnimalEnabled(sp.Lands ? sp.LandedEntityCode.Path : sp.Code);
    }

    /// <summary>
    /// Resident birds of every species that lands (its landedEntity, e.g. flocks:greylag) spawn through vanilla's spawner
    /// (spawnconditions in the entity file: inside still water at the surface, climate limits, maxQuantity). Vanilla
    /// spawnconditions don't know seasons or lake size, so this vetoes spawns out of the species' resident season, on
    /// small ponds, far from shore, near an abandoned lake, or where they are already thick on the ground. Sea birds
    /// (habitat "sea") are judged the same way on salt water: near the shore, on enough sea.
    /// The autumn departure is in EntityBehaviorWaterfowl.
    /// </summary>
    public class ResidentGeese
    {
        private readonly ICoreServerAPI sapi;
        public readonly FlocksServerConfig Cfg;
        private readonly Dictionary<string, Species> species;
        public int Allowed, VetoSeason, VetoLake, VetoShore, VetoCrowded, VetoOff, VetoAbandoned, VetoDepth, VetoScarecrow, VetoGround, VetoNoRoost;
        /// <summary>Config ShoreDistance, for EntityBehaviorWaterfowl (server side).</summary>
        public static int ShoreDistanceCfg = 20;
        /// <summary>The server config, for the bird behaviours (crop raiding, the scarecrow radius).</summary>
        public static FlocksServerConfig Config = new();

        public ResidentGeese(ICoreServerAPI sapi, FlocksServerConfig cfg, Dictionary<string, Species> species)
        {
            this.sapi = sapi; Cfg = cfg; this.species = species;
            ShoreDistanceCfg = Math.Max(1, cfg.ShoreDistance);
            Config = cfg;
            sapi.Event.OnTrySpawnEntity += TrySpawn;
        }

        private bool TrySpawn(IBlockAccessor ba, ref EntityProperties properties, Vec3d pos, long herdId)
        {
            if (properties?.Code == null) return true;
            var code = properties.Code;
            if (code.Domain == "flocks" && !Cfg.AnimalEnabled(code.Path)) { VetoOff++; return false; }   // switched off (Animals)
            var sp = ByEntity(species, code);
            if (sp == null) return true;
            try
            {
                if (!Cfg.ResidentGeese) { VetoOff++; return false; }
                var bp = pos.AsBlockPos;
                if (sp.Soars)
                {
                    // birds of prey: resident all year, no water; a territory each, so at most two within 192 blocks
                    if (FlockRegistry.Instance?.IsAbandoned(pos.X, pos.Z) == true) { VetoAbandoned++; return false; }
                    if (sapi.World.GetEntitiesAround(pos, 192, 96, e => e.Alive && e.Code.Equals(code)).Length >= 2) { VetoCrowded++; return false; }
                    Allowed++;
                    return true;
                }
                if (sp.Woodland)
                {
                    // owls: all year, a territory each (at most two within 96 blocks), and a tree to roost in nearby
                    if (sapi.World.GetEntitiesAround(pos, 96, 64, e => e.Alive && e.Code.Equals(code)).Length >= 2) { VetoCrowded++; return false; }
                    if (!EntityBehaviorOwl.HasRoostNear(ba, pos)) { VetoNoRoost++; return false; }
                    Allowed++;
                    return true;
                }
                if (sp.Ground)
                {
                    // land birds (crows): all year, on open ground; never where a scarecrow stands, and not too many
                    if (FlockRegistry.Instance?.IsAbandoned(pos.X, pos.Z) == true) { VetoAbandoned++; return false; }
                    if (!EntityBehaviorCorvid.OpenGround(ba, bp.X, bp.Z, out _)) { VetoGround++; return false; }
                    if (Scarecrows.Protects(sapi.World, pos, Cfg.ScarecrowRadius)) { VetoScarecrow++; return false; }
                    if (sapi.World.GetEntitiesAround(pos, Cfg.NearbyRadius, 32, e => e.Alive && e.Code.Equals(code)).Length >= Cfg.MaxCrowsNearby) { VetoCrowded++; return false; }
                    Allowed++;
                    return true;
                }
                if (!InSeason(sapi.World, bp, sp)) { VetoSeason++; return false; }
                // a wading bird only where it can stand: water one block deep, the spawn on its bottom
                if (sp.Wades && !(LakeSearch.IsWadeable(ba, bp.X, bp.Z, out int wy) && wy == bp.Y)) { VetoDepth++; return false; }
                int minLake = sp.MinLake(Cfg.MinLakeSize);
                if (LakeSize(ba, bp, minLake, sp.Liquid) < minLake) { VetoLake++; return false; }
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

        /// <summary>The species' resident season here: from its spring window to its autumn window (MigrationScheduler);
        /// always, for a bird that stays all year.</summary>
        public static bool InSeason(IWorldAccessor world, BlockPos bp, Species sp)
        {
            if (sp.YearRound) return true;
            var cal = world.Calendar;
            return MigrationScheduler.ResidentSeason(sp.SpringStart, sp.AutumnStart, cal.GetSeasonRel(bp), cal.OnGetLatitude(bp.Z));
        }

        /// <summary>Surface blocks of the liquid (fresh water unless told otherwise) connected to bp's column at its water
        /// level, counted up to <paramref name="enough"/>.</summary>
        public static int LakeSize(IBlockAccessor ba, BlockPos bp, int enough, string liquid = "water")
        {
            int sx = bp.X, sz = bp.Z;
            if (!Surface(ba, sx, sz, out int y, liquid)) return 0;
            var seen = new System.Collections.Generic.HashSet<long> { ((long)sx << 32) ^ (uint)sz };
            var q = new System.Collections.Generic.Queue<(int, int)>(); q.Enqueue((sx, sz));
            int n = 0;
            while (q.Count > 0 && n < enough)
            {
                var (x, z) = q.Dequeue(); n++;
                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                    if (seen.Add(((long)nx << 32) ^ (uint)nz) && Surface(ba, nx, nz, out int ny, liquid) && ny == y) q.Enqueue((nx, nz));
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
            int y = Surface(ba, bp.X, bp.Z, out int sy, null) ? sy : bp.Y;
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

        /// <summary>The column's rain-map top is the liquid; null = fresh or salt water alike.</summary>
        private static bool Surface(IBlockAccessor ba, int x, int z, out int y, string liquid)
        {
            y = ba.GetRainMapHeightAt(x, z);
            if (y <= 0) return false;
            var b = ba.GetBlock(new BlockPos(x, y, z), BlockLayersAccess.Fluid);
            return b != null && b.IsLiquid() && (liquid == null ? b.LiquidCode == "water" || b.LiquidCode == "saltwater" : b.LiquidCode == liquid);
        }

        public string Report()
        {
            var sb = new StringBuilder($"Resident birds {(Cfg.ResidentGeese ? "on" : "OFF")} (vanilla spawning + season/lake veto). " +
                $"Spawn checks since start: {Allowed} allowed, vetoed {VetoSeason} out of season, {VetoLake} small lake, {VetoShore} too far from shore (>{Cfg.ShoreDistance}), {VetoCrowded} crowded, {VetoAbandoned} abandoned lake, {VetoDepth} too deep to wade, {VetoScarecrow} by a scarecrow, {VetoGround} not on open ground, {VetoNoRoost} no tree to roost in, {VetoOff} disabled.");
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
