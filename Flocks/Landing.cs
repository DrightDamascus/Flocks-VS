using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>/flocks land: finds open fresh water near the player and spawns a group of greylag geese on it.</summary>
    public static class Landing
    {
        public const int Radius = 64, MinPatch = 36, MaxFill = 2000;
        /// <summary>Geese land at least this far from the player: past the goose's fleeRange (14 in entities/greylag.json),
        /// otherwise the group paddles off or takes off on the first tick.</summary>
        public const double Clear = 20;

        public static TextCommandResult Land(ICoreServerAPI sapi, Entity caller, int count)
        {
            if (caller == null) return TextCommandResult.Error("Needs a player.");
            var type = sapi.World.GetEntityType(new AssetLocation("flocks", "greylag"));
            if (type == null) return TextCommandResult.Error("Entity type flocks:greylag is not loaded; see server-main.log.");
            var rand = sapi.World.Rand;
            int n = count > 0 ? GameMath.Clamp(count, 1, 30) : 7 + rand.Next(6); // landing flocks are 7-12, like natural groups
            var patch = FindPatch(sapi.World.BlockAccessor, caller.Pos.AsBlockPos);
            if (patch == null) return TextCommandResult.Error($"No open water with {MinPatch}+ surface blocks at least {Clear:0} blocks from you (the geese would flee at once) within {Radius} blocks.");

            // a compact group on the nearest water past Clear: the columns closest to the first such column
            double px = caller.Pos.X, pz = caller.Pos.Z;
            double Dist(BlockPos c) => Math.Sqrt((c.X + 0.5 - px) * (c.X + 0.5 - px) + (c.Z + 0.5 - pz) * (c.Z + 0.5 - pz));
            var spots = patch.Where(c => Dist(c) >= Clear).ToList();
            var anchor = spots.OrderBy(Dist).First();
            spots = spots.OrderBy(c => (c.X - anchor.X) * (c.X - anchor.X) + (c.Z - anchor.Z) * (c.Z - anchor.Z)).Take(Math.Max(MinPatch, n * 4)).ToList();
            int spawned = 0;
            // one flock record for the whole group, so they leave together and are tracked as one
            var flock = FlockRegistry.Instance?.Create("goose", n, new Vec3d(anchor.X + 0.5, anchor.Y + 1, anchor.Z + 0.5));
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
            return TextCommandResult.Success($"Landed {spawned} greylag geese on water at {anchor.X}, {anchor.Y}, {anchor.Z} (~{Dist(anchor):0} blocks away, patch of {patch.Count}+ blocks).");
        }

        /// <summary>Nearest fresh-water surface patch (rain-map top is still water) with at least MinPatch columns
        /// Clear or more blocks from <paramref name="center"/>.</summary>
        private static List<BlockPos> FindPatch(IBlockAccessor ba, BlockPos center)
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
                if (seen.Contains(Key(sx, sz)) || !Surface(ba, sx, sz, out int y)) continue;
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
                        if (Surface(ba, nx, nz, out int ny) && ny == y) { seen.Add(Key(nx, nz)); queue.Enqueue((nx, nz)); }
                    }
                }
                if (patch.Count(c => Math.Sqrt((c.X - center.X) * (c.X - center.X) + (c.Z - center.Z) * (c.Z - center.Z)) >= Clear) >= MinPatch) return patch;
            }
            return null;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static bool Surface(IBlockAccessor ba, int x, int z, out int y)
        {
            y = ba.GetRainMapHeightAt(x, z);
            if (y <= 0) return false;                 // unloaded
            var b = ba.GetBlock(new BlockPos(x, y, z), BlockLayersAccess.Fluid);
            return b != null && b.IsLiquid() && b.LiquidCode == "water";
        }
    }
}
