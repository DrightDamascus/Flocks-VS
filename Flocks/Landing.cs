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
            var rand = sapi.World.Rand;
            // the species' landing flock size, like a landing flock or a natural group
            int n = count > 0 ? GameMath.Clamp(count, 1, 30) : sp.LandingMin + rand.Next(Math.Max(1, sp.LandingMax - sp.LandingMin + 1));
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
            int spawned = 0;
            // one flock record for the whole group, so they leave together and are tracked as one
            var flock = FlockRegistry.Instance?.Create(sp.Code, n, new Vec3d(anchor.X + 0.5, anchor.Y + 1, anchor.Z + 0.5));
            for (int i = 0; i < n; i++)
            {
                var c = spots[rand.Next(spots.Count)];
                var e = sapi.World.ClassRegistry.CreateEntity(type);
                e.WatchedAttributes.SetBool(EntityBehaviorWaterfowl.TestKey, true); // test geese leave after lifeHours
                if (flock != null) e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, flock.Id);
                e.Pos.SetPos(c.X + 0.2 + rand.NextDouble() * 0.6, c.Y + 0.75, c.Z + 0.2 + rand.NextDouble() * 0.6);
                e.Pos.Yaw = (float)(rand.NextDouble() * Math.PI * 2);
                sapi.World.SpawnEntity(e);
                spawned++;
            }
            return TextCommandResult.Success(Lang.Get("flocks:land-landed-water", spawned, sp.DisplayName, anchor.X, anchor.Y, anchor.Z, Dist(anchor).ToString("0")));
        }

        /// <summary>Land birds (crows): a group on open ground at least Clear from the player. Owls too: each takes the place
        /// it is put down as its territory and flies off to roost (or to hunt, at night); they keep no flock.</summary>
        private static TextCommandResult LandOnGround(ICoreServerAPI sapi, Entity caller, int n, Species sp)
        {
            var type = sapi.World.GetEntityType(sp.LandedEntityCode);
            var spots = EntityBehaviorCorvid.GroundSpots(sapi.World, caller.Pos.XYZ, caller.Pos.X, caller.Pos.Z, Clear, Clear, sapi.World.Rand);
            if (spots == null || spots.Count == 0) return TextCommandResult.Error(Lang.Get("flocks:land-no-ground", Clear.ToString("0")));
            var flock = sp.Woodland ? null : FlockRegistry.Instance?.Create(sp.Code, n, spots[0]);
            for (int i = 0; i < n; i++)
            {
                var e = sapi.World.ClassRegistry.CreateEntity(type);
                if (flock != null) e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, flock.Id);
                var s = spots[sapi.World.Rand.Next(spots.Count)];
                e.Pos.SetPos(s.X + (sapi.World.Rand.NextDouble() - 0.5) * 0.6, s.Y, s.Z + (sapi.World.Rand.NextDouble() - 0.5) * 0.6);
                e.Pos.Yaw = (float)(sapi.World.Rand.NextDouble() * Math.PI * 2);
                sapi.World.SpawnEntity(e);
            }
            var at = spots[0];
            return TextCommandResult.Success(Lang.Get("flocks:land-landed-ground", n, sp.DisplayName, at.X.ToString("0"), at.Y.ToString("0"), at.Z.ToString("0"), caller.Pos.HorDistanceTo(at).ToString("0")));
        }

        /// <summary>Nearest surface patch of the liquid (rain-map top is still water, or sea water) with at least MinPatch
        /// columns Clear or more blocks from <paramref name="center"/>.</summary>
        private static List<BlockPos> FindPatch(IBlockAccessor ba, BlockPos center, int minPatch, string liquid)
        {
            var seen = new HashSet<long>();
            var starts = new List<(double d, int x, int z)>();
            for (int dx = -Radius; dx <= Radius; dx += 3)
                for (int dz = -Radius; dz <= Radius; dz += 3)
                {
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d <= Radius && d >= 6) starts.Add((d, center.X + dx, center.Z + dz));
                }
            starts.Sort((p, q) => p.d.CompareTo(q.d));
            foreach (var (_, sx, sz) in starts)
            {
                if (seen.Contains(Key(sx, sz)) || !LakeSearch.Surface(ba, sx, sz, out int y, liquid)) continue;
                var patch = new List<BlockPos>();
                var queue = new Queue<(int x, int z)>();
                queue.Enqueue((sx, sz)); seen.Add(Key(sx, sz));
                while (queue.Count > 0 && patch.Count < MaxFill)
                {
                    var (x, z) = queue.Dequeue();
                    patch.Add(new BlockPos(x, y, z));
                    foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                    {
                        if (seen.Contains(Key(nx, nz))) continue;
                        if (LakeSearch.Surface(ba, nx, nz, out int ny, liquid) && ny == y) { seen.Add(Key(nx, nz)); queue.Enqueue((nx, nz)); }
                    }
                }
                if (patch.Count(c => Math.Sqrt((c.X - center.X) * (c.X - center.X) + (c.Z - center.Z) * (c.Z - center.Z)) >= Clear) >= minPatch) return patch;
            }
            return null;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
