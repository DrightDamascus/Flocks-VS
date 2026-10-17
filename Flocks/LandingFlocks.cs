using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>
    /// Landing flocks (server), for every species that lands. On its spring migration days each online player gets up
    /// to one landing a day per species (scaled by the day's migration strength, at one of its flying hours). A landing
    /// needs open water 40-160 blocks from the player with none of that species on it yet, in its resident season. The
    /// flock is real birds from the start: they appear
    /// in a V up to MaxEntry blocks out along the arrival heading (inside loaded chunks and the flight simulation
    /// range), already flying at Speed, each toward its own spot on the water, and glide in (Waterfowl).
    /// The flock is the one away longest, or a replacement for one that was killed off, or a new one (FlockRegistry).
    /// Autumn has no landings: geese out of their resident season would leave again at once.
    /// </summary>
    public class LandingFlocks
    {
        public const double Speed = 13, MinFromPlayer = 40;
        /// <summary>They appear this far out from the lake (the furthest that is loaded and within reach), at least MinEntry.</summary>
        public const int MaxEntry = 320, MinEntry = 140;
        /// <summary>V formation at the start (blocks): back per rank, out per rank (for size 0.65 geese, 1.7 across).</summary>
        public const double FormBack = 1.8, FormOut = 2.2;

        private readonly ICoreServerAPI sapi;
        private readonly Dictionary<string, Species> species;
        private readonly Random rand = new();
        /// <summary>Today's planned landing hours, per player and species.</summary>
        private readonly Dictionary<(string player, string species), (int day, List<double> hours)> plans = new();

        public LandingFlocks(ICoreServerAPI sapi, Dictionary<string, Species> species)
        {
            this.sapi = sapi; this.species = species;
            sapi.Event.RegisterGameTickListener(Tick, 5000);
        }

        private void Tick(float dt)
        {
            var cal = sapi.World.Calendar;
            int day = (int)cal.TotalDays;
            double hour = cal.HourOfDay;
            foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
            {
                if (plr.Entity == null || plr.ConnectionState != EnumClientState.Playing) continue;
                foreach (var sp in species.Values)
                {
                    if (!sp.Lands || !sp.ArriveInFlocks) continue;
                    var key = (plr.PlayerUID, sp.Code);
                    if (!plans.TryGetValue(key, out var plan) || plan.day != day)
                    {
                        plan = (day, PlanDay(sp, plr.Entity.Pos.AsBlockPos, hour));
                        plans[key] = plan;
                    }
                    for (int i = plan.hours.Count - 1; i >= 0; i--)
                    {
                        if (hour < plan.hours[i]) continue;
                        plan.hours.RemoveAt(i);
                        Arrive(plr, sp, false, out _);
                    }
                }
            }
        }

        /// <summary>Today's landing hours for a player: up to one, scaled by the spring migration strength there.
        /// Hours already well past (joined late, slept) are dropped.</summary>
        private List<double> PlanDay(Species sp, BlockPos pos, double now)
        {
            var hours = new List<double>();
            var cal = sapi.World.Calendar;
            double s = MigrationScheduler.Strength(sp, cal.GetSeasonRel(pos), cal.OnGetLatitude(pos.Z), out bool spring);
            if (!spring || s <= 0) return hours;
            int n = (int)Math.Floor(s + rand.NextDouble());
            for (int i = 0; i < n; i++)
            {
                double h = MigrationScheduler.PickHour(sp, rand);
                if (h > now - 1) hours.Add(h);
            }
            return hours;
        }

        /// <summary>Starts a landing flock for this player now. force (the /flocks arrive command) skips the season check,
        /// and the realm check when the Biomes mod is installed.</summary>
        public bool Arrive(IServerPlayer plr, Species sp, bool force, out string msg) => Arrive(plr.Entity.Pos.XYZ, sp, force, out msg);

        /// <summary>The same for a position standing in for the player (the in-engine tests use it).</summary>
        public bool Arrive(Vec3d p, Species sp, bool force, out string msg)
        {
            string name = sp.DisplayName;
            if (!sp.Lands) { msg = Lang.Get("flocks:arrive-doesnt-land", name); return false; }
            if (sp.Ground || sp.Soars || sp.Woodland) { msg = Lang.Get("flocks:arrive-not-in-flocks", name); return false; }
            var code = sp.LandedEntityCode;
            var ba = sapi.World.BlockAccessor;
            var spots = LakeSearch.FindSpots(ba, p, p.X, p.Z, sp.MinLake(Landing.MinPatch), rand, sp.Liquid);
            if (spots == null || spots.Count == 0) { msg = Lang.Get("flocks:arrive-no-lake"); return false; }
            var centre = spots[spots.Count / 2];
            if (centre.HorizontalSquareDistanceTo(p.X, p.Z) < MinFromPlayer * MinFromPlayer) { msg = Lang.Get("flocks:arrive-too-close", MinFromPlayer.ToString("0")); return false; }
            if (!force && !ResidentGeese.InSeason(sapi.World, centre.AsBlockPos, sp)) { msg = Lang.Get("flocks:arrive-season", name); return false; }
            if (!force && !BiomesCompat.Allows(sapi, sp, centre.AsBlockPos)) { msg = Lang.Get("flocks:arrive-realm", name); return false; }
            if (sapi.World.GetEntitiesAround(centre, 48, 24, e => e.Alive && e.Code.Equals(code)).Length > 0) { msg = Lang.Get("flocks:arrive-occupied", name); return false; }
            var type = sapi.World.GetEntityType(code);
            if (type == null) { msg = Lang.Get("flocks:arrive-not-loaded", code); return false; }

            // Spring heads poleward (north is heading pi, -Z), so they come in from the equator side, give or take 25 deg.
            double lat = sapi.World.Calendar.OnGetLatitude((int)centre.Z);
            double heading = (lat >= 0 ? Math.PI : 0) + (rand.NextDouble() - 0.5) * 0.9;
            double dx = Math.Sin(heading), dz = Math.Cos(heading);

            // Appear as far out as possible: the chunk must be loaded and the flock inside its flight simulation range
            // of this player (minus a margin), or it would never start ticking.
            double reach = EntityBehaviorWaterfowl.FlightRange - 40, ex = 0, ez = 0;
            int entry = 0;
            for (int d = MaxEntry; d >= MinEntry; d -= 20)
            {
                ex = centre.X - dx * d; ez = centre.Z - dz * d;
                if ((ex - p.X) * (ex - p.X) + (ez - p.Z) * (ez - p.Z) > reach * reach) continue;
                if (ba.GetChunkAtBlockPos(new BlockPos((int)ex, (int)centre.Y, (int)ez)) == null) continue;
                entry = d; break;
            }
            if (entry == 0) { msg = Lang.Get("flocks:arrive-no-entry"); return false; }
            // a comfortable height above the water and the ground at the start; Waterfowl climbs over anything later
            double ey = Math.Max(centre.Y + 24, MaxGround(ba, ex, ez, dx, dz, -8, 40) + 12);

            var flock = FlockRegistry.Instance.TakeForLanding(sp.Code, centre, rand, sp.LandingMin, sp.LandingMax);
            int n = Math.Max(1, flock.Count);
            for (int i = 0; i < n; i++)
            {
                int rank = (i + 1) / 2, side = i == 0 ? 0 : (i % 2 == 0 ? 1 : -1);
                double along = -rank * FormBack + (rand.NextDouble() - 0.5) * 0.3, across = side * rank * FormOut + (rand.NextDouble() - 0.5) * 0.3;
                var e = sapi.World.ClassRegistry.CreateEntity(type);
                e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, flock.Id);
                // all at the same speed, so the V holds until each peels off to its own spot
                EntityBehaviorWaterfowl.PrepareLanding(e, spots[rand.Next(spots.Count)].Clone(), heading, Speed, p.X, p.Z);
                // X along the heading, Z across (perp = (cos, -sin)), as in the sky renderer
                e.Pos.SetPos(ex + dx * along + dz * across, ey + rank * 0.15 + (rand.NextDouble() - 0.5) * 0.4, ez + dz * along - dx * across);
                sapi.World.SpawnEntity(e);
            }
            msg = Lang.Get("flocks:arrive-started", n, name, flock.Id, entry, centre.X.ToString("0"), centre.Y.ToString("0"), centre.Z.ToString("0"));
            sapi.Logger.Notification("[Flocks] landing: {0}", msg);
            return true;
        }

        /// <summary>Highest ground or tree top along the heading from 'from' to 'to' blocks past (x, z), loaded chunks only.</summary>
        private static double MaxGround(IBlockAccessor ba, double x, double z, double dx, double dz, int from, int to)
        {
            double top = 0;
            for (int d = from; d <= to; d += 4)
            {
                int h = ba.GetRainMapHeightAt((int)Math.Floor(x + dx * d), (int)Math.Floor(z + dz * d));
                if (h > 0) top = Math.Max(top, h + 1);
            }
            return top;
        }
    }
}
