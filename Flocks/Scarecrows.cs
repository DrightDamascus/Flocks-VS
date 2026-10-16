using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// The scarecrow is vanilla's straw dummy (the entity game:strawdummy: two hay blocks over a stick). Crows don't raid
    /// crops, don't come down to feed and don't spawn within the server's ScarecrowRadius of a standing one, measured
    /// horizontally, and crows already feeding there when one is put up fly off. It never stops working, wherever it
    /// stands (Dright: moving it about would make farming tedious); one knocked down (dead) no longer counts.
    /// </summary>
    public static class Scarecrows
    {
        public static readonly AssetLocation Code = new("game", "strawdummy");

        /// <summary>A standing straw dummy within radius of at, horizontally (and within radius up or down).</summary>
        public static bool Protects(IWorldAccessor world, Vec3d at, double radius)
        {
            if (radius <= 0) return false;
            double r2 = radius * radius;
            return world.GetEntitiesAround(at, (float)radius, (float)radius, e => e.Alive && Code.Equals(e.Code)
                && (e.Pos.X - at.X) * (e.Pos.X - at.X) + (e.Pos.Z - at.Z) * (e.Pos.Z - at.Z) <= r2).Length > 0;
        }

        /// <summary>The nearest standing straw dummy within radius of at (horizontally), or null.</summary>
        public static Vec3d Nearest(IWorldAccessor world, Vec3d at, double radius)
        {
            if (radius <= 0) return null;
            Vec3d best = null; double bestD = radius * radius;
            foreach (var e in world.GetEntitiesAround(at, (float)radius, (float)radius, e => e.Alive && Code.Equals(e.Code)))
            {
                double d = (e.Pos.X - at.X) * (e.Pos.X - at.X) + (e.Pos.Z - at.Z) * (e.Pos.Z - at.Z);
                if (d <= bestD) { bestD = d; best = e.Pos.XYZ; }
            }
            return best;
        }

        /// <summary>Where the standing straw dummies are within range of at, to test many spots against at once.</summary>
        public static List<Vec3d> Around(IWorldAccessor world, Vec3d at, double range)
        {
            var list = new List<Vec3d>();
            foreach (var e in world.GetEntitiesAround(at, (float)range, (float)range, e => e.Alive && Code.Equals(e.Code))) list.Add(e.Pos.XYZ);
            return list;
        }

        /// <summary>Whether any of these dummies (from Around) stands within radius of (x, z).</summary>
        public static bool Protects(List<Vec3d> dummies, double x, double z, double radius)
        {
            foreach (var d in dummies) if ((d.X - x) * (d.X - x) + (d.Z - z) * (d.Z - z) <= radius * radius) return true;
            return false;
        }
    }
}
