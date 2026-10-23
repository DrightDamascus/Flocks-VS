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
    /// <summary>The server's settings, in ModConfig/flocks-server.json; the file is created with these defaults the
    /// first time the server runs, and rewritten (keeping its values) on each start so new settings show up.</summary>
    public class FlocksServerConfig
    {
        /// <summary>Master switch for resident birds: off, none of the mod's birds spawn on their own (worldgen or
        /// runtime). The name is from when the goose was the only one.</summary>
        public bool ResidentGeese = true;
        /// <summary>How many surface blocks the water around a spawn must have for a waterbird to spawn there (a species
        /// can ask for more or less with its own minLakeSize).</summary>
        public int MinLakeSize = 36;
        /// <summary>No goose spawn further than this from shore (a solid non-water block at or just above the water
        /// surface, looked for in 8 directions). Floating geese more than 1.5x this far out steer back toward land.</summary>
        public int ShoreDistance = 20;
        /// <summary>A backstop for waterbirds: a spawn is refused once this many of the same bird are within NearbyRadius.
        /// The game's own cap per player (spawnconditions.runtime.maxQuantity) normally stops them well before.</summary>
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
        /// <summary>How often the birds call while at rest (1 = default, 0.5 = half as often, 0 = only when flushed).</summary>
        public double CallRate = 1;
        /// <summary>How many of the mod's animals spawn on their own (1 = default, 0.5 = half): scales each one's spawn
        /// cap (spawnconditions.runtime.maxQuantity) and its worldgen spawning. Landing flocks are not affected.</summary>
        public double Numbers = 1;

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
    /// Leaving again in autumn is up to the birds themselves (EntityBehaviorWaterfowl.SeasonOver).
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
                bool ok = !Cfg.ResidentGeese ? Refuse(ref VetoOff)
                    : sp.Soars ? RaptorMaySpawn(code, pos)
                    : sp.Woodland ? OwlMaySpawn(ba, code, pos)
                    : sp.Ground ? CrowMaySpawn(ba, code, pos)
                    : WaterbirdMaySpawn(ba, sp, code, pos);
                if (ok) Allowed++;
                return ok;
            }
            catch (Exception ex) { sapi.Logger.Error("[Flocks] {0} spawn check failed: {1}", code, ex); return false; }
        }

        /// <summary>Counts a refused spawn under its reason (for /flocks residents) and refuses it.</summary>
        private static bool Refuse(ref int reason) { reason++; return false; }

        private int Around(Vec3d pos, double range, double height, AssetLocation code)
            => sapi.World.GetEntitiesAround(pos, (float)range, (float)height, e => e.Alive && e.Code.Equals(code)
                && e.GetBehavior<EntityBehaviorBird>()?.Passing != true).Length;   // not the birds of a flock passing over

        private static bool Abandoned(Vec3d pos) => FlockRegistry.Instance?.IsAbandoned(pos.X, pos.Z) == true;

        /// <summary>Birds of prey: resident all year, no water; a territory each, so at most two within 192 blocks.</summary>
        private bool RaptorMaySpawn(AssetLocation code, Vec3d pos)
        {
            if (Abandoned(pos)) return Refuse(ref VetoAbandoned);
            if (Around(pos, 192, 96, code) >= 2) return Refuse(ref VetoCrowded);
            return true;
        }

        /// <summary>Owls: all year, a territory each (at most two within 96 blocks), and a tree to roost in nearby.</summary>
        private bool OwlMaySpawn(IBlockAccessor ba, AssetLocation code, Vec3d pos)
        {
            if (Around(pos, 96, 64, code) >= 2) return Refuse(ref VetoCrowded);
            if (!EntityBehaviorOwl.HasRoostNear(ba, pos)) return Refuse(ref VetoNoRoost);
            return true;
        }

        /// <summary>Land birds (crows): all year, on open ground; never where a scarecrow stands, and not too many.</summary>
        private bool CrowMaySpawn(IBlockAccessor ba, AssetLocation code, Vec3d pos)
        {
            if (Abandoned(pos)) return Refuse(ref VetoAbandoned);
            BlockPos at = pos.AsBlockPos;
            if (!EntityBehaviorCorvid.OpenGround(ba, at.X, at.Z, out _)) return Refuse(ref VetoGround);
            if (Scarecrows.Protects(sapi.World, pos, Cfg.ScarecrowRadius)) return Refuse(ref VetoScarecrow);
            if (Around(pos, Cfg.NearbyRadius, 32, code) >= Cfg.MaxCrowsNearby) return Refuse(ref VetoCrowded);
            return true;
        }

        /// <summary>Waterbirds: in their season, on a big enough lake (or enough sea) within reach of the shore, not where
        /// a flock was driven off, and not where there are already plenty. Waders only where they can stand.</summary>
        private bool WaterbirdMaySpawn(IBlockAccessor ba, Species sp, AssetLocation code, Vec3d pos)
        {
            BlockPos at = pos.AsBlockPos;
            if (!InSeason(sapi.World, at, sp)) return Refuse(ref VetoSeason);
            // a wading bird only where it can stand: water one block deep, the spawn on its bottom
            if (sp.Wades && !(LakeSearch.IsWadeable(ba, at.X, at.Z, out int bottom) && bottom == at.Y)) return Refuse(ref VetoDepth);
            int minLake = sp.MinLake(Cfg.MinLakeSize);
            if (LakeSize(ba, at, minLake, sp.Liquid) < minLake) return Refuse(ref VetoLake);
            if (ShoreDirection(ba, at, ShoreDistanceCfg, out _) < 0) return Refuse(ref VetoShore);
            if (Abandoned(pos)) return Refuse(ref VetoAbandoned);
            if (Around(pos, Cfg.NearbyRadius, 32, code) >= Cfg.MaxGeeseNearby) return Refuse(ref VetoCrowded);
            return true;
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
            return sp.YearRound || MigrationScheduler.ResidentSeason(sp.SpringStart, sp.AutumnStart,
                world.Calendar.GetSeasonRel(bp), world.Calendar.OnGetLatitude(bp.Z));
        }

        /// <summary>How big the lake (or stretch of sea) at bp's column is, in surface blocks of the liquid, counting no
        /// further than <paramref name="enough"/>; 0 when the column isn't that liquid at all.</summary>
        public static int LakeSize(IBlockAccessor ba, BlockPos bp, int enough, string liquid = "water")
        {
            if (!LakeSearch.Surface(ba, bp.X, bp.Z, out int level, liquid)) return 0;
            return LakeSearch.WalkSurface(ba, bp.X, bp.Z, level, enough, new HashSet<long>(), liquid, null);
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
            => liquid != null ? LakeSearch.Surface(ba, x, z, out y, liquid)
                : LakeSearch.Surface(ba, x, z, out y, "water") || LakeSearch.Surface(ba, x, z, out y, "saltwater");

        public string Report()
        {
            var sb = new StringBuilder($"Resident birds {(Cfg.ResidentGeese ? "on" : "OFF")} (vanilla spawning + season/lake veto). " +
                $"Spawn checks since start: {Allowed} allowed, vetoed {VetoSeason} out of season, {VetoLake} small lake, {VetoShore} too far from shore (>{Cfg.ShoreDistance}), {VetoCrowded} crowded, {VetoAbandoned} abandoned lake, {VetoDepth} too deep to wade, {VetoScarecrow} by a scarecrow, {VetoGround} not on open ground, {VetoNoRoost} no tree to roost in, {VetoOff} disabled.");
            foreach (var sp in species.Values.Where(s => s.Lands))
            {
                var code = sp.LandedEntityCode;
                var birds = sapi.World.LoadedEntities.Values.Where(e => e.Alive && e.Code.Equals(code)).ToList();
                sb.Append($"\nLoaded {sp.Name ?? sp.Code}: {birds.Count}");
                foreach (var plr in sapi.World.AllOnlinePlayers.Where(p => p.Entity != null))
                {
                    var where = plr.Entity.Pos;
                    int near = birds.Count(e => e.Pos.HorDistanceTo(where.XYZ) <= Cfg.NearbyRadius);
                    string season = InSeason(sapi.World, where.AsBlockPos, sp) ? "yes" : "no";
                    sb.Append($"\n  {plr.PlayerName}: {near} within {Cfg.NearbyRadius} blocks, resident season here: {season}");
                }
            }
            return sb.ToString();
        }
    }
}
