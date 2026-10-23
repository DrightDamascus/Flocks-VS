using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// Where a flushed group of geese should come down: first another part of the lake they are on, far enough from
    /// whatever scared them; failing that, the nearest other lake within SearchRadius, preferring lakes that don't lie
    /// toward the threat. Only loaded chunks are seen (unloaded columns read as no water). Sea birds search salt water
    /// the same way: the stretch of sea along the coast they are on, else another within reach.
    /// </summary>
    public static class LakeSearch
    {
        public const int SearchRadius = 160, Step = 4, MaxLakeFill = 3000, OtherLakeFill = 800;
        /// <summary>A landing spot keeps at least this far from the threat: on the same lake, and on another lake.</summary>
        public const double SafeSameLake = 26, SafeOtherLake = 32;
        /// <summary>A same-lake hop is at least this long, so it reads as moving off rather than a shuffle.</summary>
        public const double MinHop = 14;
        /// <summary>The group spreads over open water within this many blocks of the chosen spot.</summary>
        public const int Spread = 4;
        /// <summary>Open water = lake for this many blocks all round: geese touch down within ~1.5 blocks of their spot
        /// (3.6 worst case in a flight simulation), so a margin of 2 keeps them off the bank.</summary>
        public const int Margin = 2;

        /// <summary>Landing points (column centre at the water's surface) for a group, or null if no lake qualifies.
        /// liquid: the water it lands on ("water", or "saltwater" for sea birds).</summary>
        public static List<Vec3d> FindSpots(IBlockAccessor ba, Vec3d from, double threatX, double threatZ, int minLake, Random rand, string liquid = "water")
        {
            int fx = (int)Math.Floor(from.X), fz = (int)Math.Floor(from.Z);
            var seen = new HashSet<long>();
            if (Surface(ba, fx, fz, out int y, liquid))
            {
                var lake = Flood(ba, fx, fz, y, MaxLakeFill, seen, liquid);
                var spot = PickSpot(ba, lake, y, from, threatX, threatZ, SafeSameLake, MinHop, rand);
                if (spot != null) return Around(lake, spot.Value, y);
            }

            // other lakes: sample a grid, nearest first, with lakes toward the threat counted as up to 2.5x further
            double ax = from.X - threatX, az = from.Z - threatZ, al = Math.Sqrt(ax * ax + az * az);
            var starts = new List<(double score, int x, int z)>();
            for (int dx = -SearchRadius; dx <= SearchRadius; dx += Step)
                for (int dz = -SearchRadius; dz <= SearchRadius; dz += Step)
                {
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > SearchRadius || d < 8) continue;
                    double toward = al > 0.1 ? -(dx * ax + dz * az) / (d * al) : 0; // 1 = straight at the threat
                    starts.Add((d * (1.75 + 0.75 * toward), fx + dx, fz + dz));
                }
            starts.Sort((p, q) => p.score.CompareTo(q.score));
            foreach (var (_, sx, sz) in starts)
            {
                if (seen.Contains(Key(sx, sz)) || !Surface(ba, sx, sz, out int sy, liquid)) continue;
                var lake = Flood(ba, sx, sz, sy, OtherLakeFill, seen, liquid);
                if (lake.Count < minLake) continue;
                var spot = PickSpot(ba, lake, sy, from, threatX, threatZ, SafeOtherLake, 0, rand);
                if (spot != null) return Around(lake, spot.Value, sy);
            }
            return null;
        }

        /// <summary>
        /// An open-water column (lake for Margin blocks all round) at least minThreat from the threat and minHere from the
        /// goose, within shore range (Residents' ShoreDistance) so the geese don't settle in the middle of a big lake.
        /// Picks at random among the dozen nearest the goose.
        /// </summary>
        private static (int x, int z)? PickSpot(IBlockAccessor ba, HashSet<long> lake, int y, Vec3d from, double tx, double tz,
            double minThreat, double minHere, Random rand)
        {
            var open = new List<(double d, int x, int z)>();
            foreach (long k in lake)
            {
                int x = (int)(k >> 32), z = (int)k;
                double cx = x + 0.5, cz = z + 0.5;
                if (Dist(cx, cz, tx, tz) < minThreat) continue;
                if (FlockRegistry.Instance?.IsAbandoned(cx, cz, Spread + 1) == true) continue; // a lake geese left: not yet (nor the spread round it)
                double d = Dist(cx, cz, from.X, from.Z);
                if (d < minHere || !Open(lake, x, z)) continue;
                open.Add((d, x, z));
            }
            open.Sort((p, q) => p.d.CompareTo(q.d));
            var near = new List<(int, int)>();
            var tmp = new BlockPos(0, y, 0);
            for (int i = 0; i < open.Count && near.Count < 12; i++)
            {
                tmp.Set(open[i].x, y, open[i].z);
                if (ResidentGeese.ShoreDirection(ba, tmp, ResidentGeese.ShoreDistanceCfg, out _) >= 0) near.Add((open[i].x, open[i].z));
                else if (i > 60) break; // the nearest open water is all far from shore; don't scan the whole lake
            }
            if (near.Count == 0) return null;
            return near[rand.Next(near.Count)];
        }

        private static List<Vec3d> Around(HashSet<long> lake, (int x, int z) c, int y)
        {
            var spots = new List<Vec3d>();
            for (int dx = -Spread; dx <= Spread; dx++)
                for (int dz = -Spread; dz <= Spread; dz++)
                    if (dx * dx + dz * dz <= Spread * Spread && Open(lake, c.x + dx, c.z + dz))
                        spots.Add(new Vec3d(c.x + dx + 0.5, y + 0.875, c.z + dz + 0.5));
            return spots;
        }

        private static bool Open(HashSet<long> lake, int x, int z)
        {
            for (int dx = -Margin; dx <= Margin; dx++)
                for (int dz = -Margin; dz <= Margin; dz++)
                    if (!lake.Contains(Key(x + dx, z + dz))) return false;
            return true;
        }

        /// <summary>Surface columns of the liquid connected to (sx, sz) at level y, up to cap; marks them in seen.</summary>
        private static HashSet<long> Flood(IBlockAccessor ba, int sx, int sz, int y, int cap, HashSet<long> seen, string liquid = "water")
        {
            var lake = new HashSet<long>();
            WalkSurface(ba, sx, sz, y, cap, seen, liquid, (x, z) => lake.Add(Key(x, z)));
            return lake;
        }

        /// <summary>
        /// Walks a lake's surface outward from (sx, sz): each column whose top is the liquid at level y and that borders
        /// one already taken, fewest steps from the start first, until cap columns are taken. Every column taken is
        /// marked in seen, so a caller trying several starts never walks the same lake twice, and is passed to visit.
        /// Returns how many were taken. Used by the landing search, /flocks land and the spawn checks alike.
        /// </summary>
        public static int WalkSurface(IBlockAccessor ba, int sx, int sz, int y, int cap, HashSet<long> seen, string liquid, Action<int, int> visit)
        {
            var frontier = new Queue<long>();
            long start = Key(sx, sz);
            seen.Add(start);
            frontier.Enqueue(start);
            int taken = 0;
            while (taken < cap && frontier.Count > 0)
            {
                long k = frontier.Dequeue();
                int x = (int)(k >> 32), z = (int)k;
                visit?.Invoke(x, z);
                taken++;
                for (int side = 0; side < 4; side++)   // east, west, south, north
                {
                    int nx = x + (side == 0 ? 1 : side == 1 ? -1 : 0), nz = z + (side == 2 ? 1 : side == 3 ? -1 : 0);
                    long nk = Key(nx, nz);
                    if (seen.Contains(nk) || !Surface(ba, nx, nz, out int ny, liquid) || ny != y) continue;
                    seen.Add(nk);
                    frontier.Enqueue(nk);
                }
            }
            return taken;
        }

        private static double Dist(double ax, double az, double bx, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        /// <summary>One number per column, x in the high half and z in the low (WalkSurface decodes it).</summary>
        public static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        // ---------------------------------------------------------------- wading (herons)

        /// <summary>Wading birds keep at least this far from the threat when they come down again.</summary>
        public const double SafeWade = 30;

        /// <summary>
        /// Water a wading bird can stand in: the column's surface is fresh water exactly one block deep, with solid
        /// ground under it. y = the water block, which is where the bird's feet are (on the bottom). Vintage Story's still
        /// water is always a full block, so the heron is big enough for its legs to reach the bottom of that.
        /// </summary>
        public static bool IsWadeable(IBlockAccessor ba, int x, int z, out int y)
        {
            if (!Surface(ba, x, z, out y)) return false;
            var below = ba.GetBlock(new BlockPos(x, y - 1, z));
            if (below == null || below.Id == 0 || below.IsLiquid()) return false;
            if (ba.GetBlock(new BlockPos(x, y - 1, z), BlockLayersAccess.Fluid)?.IsLiquid() == true) return false;
            return below.CollisionBoxes != null && below.CollisionBoxes.Length > 0;
        }

        /// <summary>
        /// Where a flushed wading bird comes down: wadeable water within SearchRadius, at least SafeWade from the
        /// threat and MinHop from where it was, on water of at least minLake surface blocks; one of the dozen nearest,
        /// and the wadeable water within 3 blocks of it (for a pair). Landing points are on the bottom. Null if none.
        /// </summary>
        public static List<Vec3d> FindWadeSpots(IBlockAccessor ba, Vec3d from, double threatX, double threatZ, int minLake, Random rand)
        {
            int fx = (int)Math.Floor(from.X), fz = (int)Math.Floor(from.Z);
            var found = new List<(double d, int x, int z, int y)>();
            for (int dx = -SearchRadius; dx <= SearchRadius; dx += 2)
                for (int dz = -SearchRadius; dz <= SearchRadius; dz += 2)
                {
                    int x = fx + dx, z = fz + dz;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > SearchRadius || d < MinHop) continue;
                    if (Dist(x + 0.5, z + 0.5, threatX, threatZ) < SafeWade) continue;
                    if (FlockRegistry.Instance?.IsAbandoned(x + 0.5, z + 0.5) == true) continue;
                    if (!IsWadeable(ba, x, z, out int y) || DeepNear(ba, x, z, y)) continue;
                    found.Add((d, x, z, y));
                }
            found.Sort((p, q) => p.d.CompareTo(q.d));
            var near = new List<(int x, int z, int y)>();
            foreach (var f in found)
            {
                if (near.Count >= 12) break;
                if (Flood(ba, f.x, f.z, f.y, minLake, new HashSet<long>()).Count >= minLake) near.Add((f.x, f.z, f.y));
            }
            if (near.Count == 0) return null;
            var c = near[rand.Next(near.Count)];
            var spots = new List<Vec3d>();
            for (int dx = -3; dx <= 3; dx++)
                for (int dz = -3; dz <= 3; dz++)
                    if (IsWadeable(ba, c.x + dx, c.z + dz, out int wy) && wy == c.y && !DeepNear(ba, c.x + dx, c.z + dz, wy))
                        spots.Add(new Vec3d(c.x + dx + 0.5, wy, c.z + dz + 0.5));
            return spots;
        }

        /// <summary>Deep water (the same lake, more than a block deep) right next to (x, z): a touchdown a block off would
        /// put a wader in over its head, so landing spots keep a block away from the drop-off.</summary>
        private static bool DeepNear(IBlockAccessor ba, int x, int z, int y)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if ((dx != 0 || dz != 0) && Surface(ba, x + dx, z + dz, out int wy) && wy == y && !IsWadeable(ba, x + dx, z + dz, out _)) return true;
            return false;
        }

        /// <summary>Rain-map top of the column is the liquid (fresh "water" unless told otherwise); y = that block.</summary>
        public static bool Surface(IBlockAccessor ba, int x, int z, out int y, string liquid = "water")
        {
            y = ba.GetRainMapHeightAt(x, z);
            if (y <= 0) return false;
            var b = ba.GetBlock(new BlockPos(x, y, z), BlockLayersAccess.Fluid);
            return b != null && b.IsLiquid() && b.LiquidCode == liquid;
        }
    }
}
