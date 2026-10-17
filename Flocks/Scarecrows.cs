using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// The scarecrow is vanilla's straw dummy (the entity game:strawdummy: two hay blocks over a stick), and with the
    /// Scarecrow mod (modid scarecrow) its scarecrow and little scarecrow too (entities, like the dummy). Crows don't raid
    /// crops, don't come down to feed and don't spawn within the server's ScarecrowRadius of a standing one, measured
    /// horizontally, and crows already feeding there when one is put up fly off. It never stops working, wherever it
    /// stands (Dright: moving it about would make farming tedious); one knocked down (dead) no longer counts. All of them
    /// reach the same ScarecrowRadius: the Scarecrow mod's own, smaller ranges are for the animals it scares.
    /// </summary>
    public static class Scarecrows
    {
        public static readonly AssetLocation[] Codes = { new("game", "strawdummy"), new("scarecrow", "scarecrow"), new("scarecrow", "little-scarecrow") };

        private static bool IsScarecrow(Entity e)
        {
            if (!e.Alive) return false;
            foreach (var code in Codes) if (code.Equals(e.Code)) return true;
            return false;
        }

        /// <summary>The Scarecrow mod's texts, and our line about crows added to the end of each.</summary>
        private static readonly (string key, string crows)[] ModTexts =
        {
            ("game:item-handbooktext-strawdummy", "flocks:scarecrowmod-crows-strawdummy"),
            ("scarecrow:itemdesc-scarecrow", "flocks:scarecrowmod-crows-scarecrow"),
            ("scarecrow:itemdesc-little-scarecrow", "flocks:scarecrowmod-crows-scarecrow"),
        };

        /// <summary>The Scarecrow mod writes its own straw dummy handbook text (its pickup is ctrl, not shift) over ours,
        /// loading after us, and its scarecrows' descriptions name only hares and raccoons. Lang files are read straight
        /// from the mods, not patched, so with it installed our line about crows is added to the end of its texts, in
        /// each loaded language that has it. Called once assets are in and again when the client's world is.</summary>
        public static void HandbookText(ICoreAPI api)
        {
            if (!api.ModLoader.IsModEnabled("scarecrow")) return;
            foreach (var lang in Lang.AvailableLanguages.Values)
            {
                var entries = lang.GetAllEntries();
                foreach (var (key, crows) in ModTexts)
                    if (entries.TryGetValue(key, out string text) && lang.GetIfExists(crows) is string line && !text.Contains(line)) entries[key] = text + line;
            }
        }

        /// <summary>A standing scarecrow within radius of at, horizontally (and within radius up or down).</summary>
        public static bool Protects(IWorldAccessor world, Vec3d at, double radius)
        {
            if (radius <= 0) return false;
            double r2 = radius * radius;
            return world.GetEntitiesAround(at, (float)radius, (float)radius, e => IsScarecrow(e)
                && (e.Pos.X - at.X) * (e.Pos.X - at.X) + (e.Pos.Z - at.Z) * (e.Pos.Z - at.Z) <= r2).Length > 0;
        }

        /// <summary>The nearest standing scarecrow within radius of at (horizontally), or null.</summary>
        public static Vec3d Nearest(IWorldAccessor world, Vec3d at, double radius)
        {
            if (radius <= 0) return null;
            Vec3d best = null; double bestD = radius * radius;
            foreach (var e in world.GetEntitiesAround(at, (float)radius, (float)radius, IsScarecrow))
            {
                double d = (e.Pos.X - at.X) * (e.Pos.X - at.X) + (e.Pos.Z - at.Z) * (e.Pos.Z - at.Z);
                if (d <= bestD) { bestD = d; best = e.Pos.XYZ; }
            }
            return best;
        }

        /// <summary>Where the standing scarecrows are within range of at, to test many spots against at once.</summary>
        public static List<Vec3d> Around(IWorldAccessor world, Vec3d at, double range)
        {
            var list = new List<Vec3d>();
            foreach (var e in world.GetEntitiesAround(at, (float)range, (float)range, IsScarecrow)) list.Add(e.Pos.XYZ);
            return list;
        }

        /// <summary>Whether any of these scarecrows (from Around) stands within radius of (x, z).</summary>
        public static bool Protects(List<Vec3d> dummies, double x, double z, double radius)
        {
            foreach (var d in dummies) if ((d.X - x) * (d.X - x) + (d.Z - z) * (d.Z - z) <= radius * radius) return true;
            return false;
        }
    }
}
