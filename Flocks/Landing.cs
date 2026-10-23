using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>/flocks land: finds open water of the species' kind (fresh, or the sea for sea birds) near the player and
    /// spawns a group of a landing species on it.</summary>
    public static class Landing
    {
        public const int Radius = 64, MinPatch = 36, MaxFill = 2000;
        /// <summary>Geese land at least this far from the player: past the goose's fleeRange (14 in entities/greylag.json),
        /// otherwise the group paddles off or takes off on the first tick.</summary>
        public const double Clear = 20;

        public static TextCommandResult Land(ICoreServerAPI sapi, Entity caller, int count, Species sp)
        {
            if (caller == null) return TextCommandResult.Error(Lang.Get("flocks:land-needs-player"));
            if (!ResidentGeese.Config.SpeciesEnabled(sp)) return TextCommandResult.Error(Lang.Get("flocks:animal-off", sp.DisplayName));
            var type = sapi.World.GetEntityType(sp.LandedEntityCode);
            if (type == null) return TextCommandResult.Error(Lang.Get("flocks:land-not-loaded", sp.LandedEntity));
            Random dice = sapi.World.Rand;
            // the species' landing flock size, like a landing flock or a natural group
            int n = count > 0 ? GameMath.Clamp(count, 1, 30) : sp.LandingMin + dice.Next(Math.Max(1, sp.LandingMax - sp.LandingMin + 1));
            if (sp.Ground || sp.Woodland) return LandOnGround(sapi, caller, n, sp);
            int minPatch = sp.MinLake(MinPatch);
            var patch = FindPatch(sapi.World.BlockAccessor, caller.Pos.AsBlockPos, minPatch, sp.Liquid);
            if (patch == null) return TextCommandResult.Error(Lang.Get("flocks:land-no-water", Lang.Get(sp.Sea ? "flocks:land-sea" : "flocks:land-water"), minPatch, Clear.ToString("0"), Radius));

            // a compact group on the nearest water past Clear: the columns closest to the first such column
            double px = caller.Pos.X, pz = caller.Pos.Z;
            double Dist(BlockPos c) => Math.Sqrt((c.X + 0.5 - px) * (c.X + 0.5 - px) + (c.Z + 0.5 - pz) * (c.Z + 0.5 - pz));
            var spots = patch.Where(c => Dist(c) >= Clear).ToList();
            var anchor = spots.OrderBy(Dist).First();
            spots = spots.OrderBy(c => (c.X - anchor.X) * (c.X - anchor.X) + (c.Z - anchor.Z) * (c.Z - anchor.Z)).Take(Math.Max(minPatch, n * 4)).ToList();
            // one flock record for the whole group, so they leave together and are tracked as one
            var flock = FlockRegistry.Instance?.Create(sp.Code, n, new Vec3d(anchor.X + 0.5, anchor.Y + 1, anchor.Z + 0.5));
            for (int left = n; left > 0; left--)
            {
                // anywhere in the middle of one of the chosen columns, afloat
                BlockPos col = spots[dice.Next(spots.Count)];
                Place(sapi, type, col.X + 0.2 + dice.NextDouble() * 0.6, col.Y + 0.75, col.Z + 0.2 + dice.NextDouble() * 0.6, flock, test: true);
            }
            return TextCommandResult.Success(Lang.Get("flocks:land-landed-water", n, sp.DisplayName, anchor.X, anchor.Y, anchor.Z, Dist(anchor).ToString("0")));
        }

        /// <summary>Land birds (crows): a group on open ground at least Clear from the player. Owls too: each takes the place
        /// it is put down as its territory and flies off to roost (or to hunt, at night); they keep no flock.</summary>
        private static TextCommandResult LandOnGround(ICoreServerAPI sapi, Entity caller, int n, Species sp)
        {
            var type = sapi.World.GetEntityType(sp.LandedEntityCode);
            var spots = EntityBehaviorCorvid.GroundSpots(sapi.World, caller.Pos.XYZ, caller.Pos.X, caller.Pos.Z, Clear, Clear, sapi.World.Rand);
            if (spots == null || spots.Count == 0) return TextCommandResult.Error(Lang.Get("flocks:land-no-ground", Clear.ToString("0")));
            var flock = sp.Woodland ? null : FlockRegistry.Instance?.Create(sp.Code, n, spots[0]);
            Random dice = sapi.World.Rand;
            for (int left = n; left > 0; left--)
            {
                Vec3d s = spots[dice.Next(spots.Count)];
                Place(sapi, type, s.X + (dice.NextDouble() - 0.5) * 0.6, s.Y, s.Z + (dice.NextDouble() - 0.5) * 0.6, flock, test: false);
            }
            var at = spots[0];
            return TextCommandResult.Success(Lang.Get("flocks:land-landed-ground", n, sp.DisplayName, at.X.ToString("0"), at.Y.ToString("0"), at.Z.ToString("0"), caller.Pos.HorDistanceTo(at).ToString("0")));
        }

        /// <summary>One bird of the type put down at (x, y, z), facing any way, in the flock if there is one. A test bird
        /// (/flocks land on water) leaves again after its lifeHours.</summary>
        private static void Place(ICoreServerAPI sapi, EntityProperties type, double x, double y, double z, FlockRecord flock, bool test)
        {
            Entity bird = sapi.World.ClassRegistry.CreateEntity(type);
            if (test) bird.WatchedAttributes.SetBool(EntityBehaviorWaterfowl.TestKey, true);
            if (flock != null) bird.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, flock.Id);
            bird.Pos.SetPos(x, y, z);
            bird.Pos.Yaw = (float)(sapi.World.Rand.NextDouble() * 2 * Math.PI);
            sapi.World.SpawnEntity(bird);
        }

        /// <summary>The nearest stretch of the liquid's surface (still water, or the sea) that has at least minPatch
        /// columns Clear or more blocks from <paramref name="center"/>. Starting points are tried on a 3-block grid
        /// from 6 blocks out to Radius, nearest first; each lake found is walked once (up to MaxFill columns).</summary>
        private static List<BlockPos> FindPatch(IBlockAccessor ba, BlockPos center, int minPatch, string liquid)
        {
            var starts = new List<(double d, int x, int z)>();
            for (int ox = -Radius; ox <= Radius; ox += 3)
                for (int oz = -Radius; oz <= Radius; oz += 3)
                {
                    double d = Math.Sqrt(ox * ox + oz * oz);
                    if (d >= 6 && d <= Radius) starts.Add((d, center.X + ox, center.Z + oz));
                }
            starts.Sort((a, b) => a.d.CompareTo(b.d));
            var walked = new HashSet<long>();
            foreach (var s in starts)
            {
                if (walked.Contains(LakeSearch.Key(s.x, s.z)) || !LakeSearch.Surface(ba, s.x, s.z, out int y, liquid)) continue;
                var patch = new List<BlockPos>();
                LakeSearch.WalkSurface(ba, s.x, s.z, y, MaxFill, walked, liquid, (x, z) => patch.Add(new BlockPos(x, y, z)));
                int clearOfPlayer = patch.Count(c => FromCenter(c, center) >= Clear);
                if (clearOfPlayer >= minPatch) return patch;
            }
            return null;
        }

        private static double FromCenter(BlockPos c, BlockPos center)
        {
            double dx = c.X - center.X, dz = c.Z - center.Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
