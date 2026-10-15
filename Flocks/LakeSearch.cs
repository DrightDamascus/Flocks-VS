using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// Where a flushed group of geese should come down: first another part of the lake they are on, far enough from
    /// whatever scared them; failing that, the nearest other lake within SearchRadius, preferring lakes that don't lie
    /// toward the threat. Only loaded chunks are seen (unloaded columns read as no water).
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

        /// <summary>Landing points (column centre at the water's surface) for a group, or null if no lake qualifies.</summary>
        public static List<Vec3d> FindSpots(IBlockAccessor ba, Vec3d from, double threatX, double threatZ, int minLake, Random rand)
        {
            int fx = (int)Math.Floor(from.X), fz = (int)Math.Floor(from.Z);
            var seen = new HashSet<long>();
            if (Surface(ba, fx, fz, out int y))
            {
                var lake = Flood(ba, fx, fz, y, MaxLakeFill, seen);
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
                if (seen.Contains(Key(sx, sz)) || !Surface(ba, sx, sz, out int sy)) continue;
                var lake = Flood(ba, sx, sz, sy, OtherLakeFill, seen);
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

        /// <summary>Fresh-water surface columns connected to (sx, sz) at level y, up to cap; marks them in seen.</summary>
        private static HashSet<long> Flood(IBlockAccessor ba, int sx, int sz, int y, int cap, HashSet<long> seen)
        {
            var lake = new HashSet<long>();
            var q = new Queue<(int x, int z)>();
            q.Enqueue((sx, sz)); seen.Add(Key(sx, sz));
            while (q.Count > 0 && lake.Count < cap)
            {
                var (x, z) = q.Dequeue();
                lake.Add(Key(x, z));
                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                    if (!seen.Contains(Key(nx, nz)) && Surface(ba, nx, nz, out int ny) && ny == y) { seen.Add(Key(nx, nz)); q.Enqueue((nx, nz)); }
            }
            return lake;
        }

        private static double Dist(double ax, double az, double bx, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        /// <summary>Rain-map top of the column is fresh water; y = that block.</summary>
        public static bool Surface(IBlockAccessor ba, int x, int z, out int y)
        {
            y = ba.GetRainMapHeightAt(x, z);
            if (y <= 0) return false;
            var b = ba.GetBlock(new BlockPos(x, y, z), BlockLayersAccess.Fluid);
            return b != null && b.IsLiquid() && b.LiquidCode == "water";
        }
    }
}
