using System.Collections.Generic;
using Vintagestory.API.Common.Entities;

namespace Flocks
{
    /// <summary>
    /// Spacing for bird sounds (server side): remembers where and when each kind of bird last made each sound, so birds
    /// near one another take turns instead of all calling at once (EntityBehaviorBird.PlaySpaced). Keyed by entity code
    /// and sound name; a sound counts as recent within Radius blocks (horizontally) for the gap the caller asks for.
    /// </summary>
    public static class BirdCalls
    {
        public const double Radius = 32;
        /// <summary>Entries older than this are dropped (longer than any gap asked for).</summary>
        private const long KeepMs = 120000;

        private static readonly Dictionary<string, List<(double x, double z, long ms)>> recent = new();

        /// <summary>Whether the bird may make the sound now; if so it is noted as made.</summary>
        public static bool TryCall(Entity e, string sound, double gapSeconds)
        {
            long now = e.World.ElapsedMilliseconds, gap = (long)(gapSeconds * 1000);
            string key = e.Code + "/" + sound;
            if (!recent.TryGetValue(key, out var list)) recent[key] = list = new();
            // drop old entries (and ones from before a world was reloaded, whose clock ran further)
            list.RemoveAll(c => now - c.ms > KeepMs || c.ms > now);
            double x = e.Pos.X, z = e.Pos.Z;
            foreach (var c in list)
            {
                if (now - c.ms < gap && (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z) < Radius * Radius) return false;
            }
            list.Add((x, z, now));
            return true;
        }
    }
}
