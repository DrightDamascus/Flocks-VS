using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>
    /// Landing flocks (server). On spring migration days each online player gets up to one landing a day (scaled by
    /// the day's migration strength, at a goose flying hour). A landing needs open water 40-160 blocks from the
    /// player with no geese on it yet, in the resident season. The flock is real geese from the start: they appear
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
        public const double FormBack = 1.2, FormOut = 1.5;

        private readonly ICoreServerAPI sapi;
        private readonly Species goose;
        private readonly Random rand = new();
        private readonly Dictionary<string, (int day, List<double> hours)> plans = new();

        public LandingFlocks(ICoreServerAPI sapi)
        {
            this.sapi = sapi;
            goose = Species.Create(null)["goose"]; // timing data only; meshes are never loaded on the server
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
                if (!plans.TryGetValue(plr.PlayerUID, out var plan) || plan.day != day)
                {
                    plan = (day, PlanDay(plr.Entity.Pos.AsBlockPos, hour));
                    plans[plr.PlayerUID] = plan;
                }
                for (int i = plan.hours.Count - 1; i >= 0; i--)
                {
                    if (hour < plan.hours[i]) continue;
                    plan.hours.RemoveAt(i);
                    Arrive(plr, false, out _);
                }
            }
        }

        /// <summary>Today's landing hours for a player: up to one, scaled by the spring migration strength there.
        /// Hours already well past (joined late, slept) are dropped.</summary>
        private List<double> PlanDay(BlockPos pos, double now)
        {
            var hours = new List<double>();
            var cal = sapi.World.Calendar;
            double s = MigrationScheduler.Strength(goose, cal.GetSeasonRel(pos), cal.OnGetLatitude(pos.Z), out bool spring);
            if (!spring || s <= 0) return hours;
            int n = (int)Math.Floor(s + rand.NextDouble());
            for (int i = 0; i < n; i++)
            {
                double h = MigrationScheduler.PickHour(goose, rand);
                if (h > now - 1) hours.Add(h);
            }
            return hours;
        }

        /// <summary>Starts a landing flock for this player now. force (the /flocks arrive command) skips the season check.</summary>
        public bool Arrive(IServerPlayer plr, bool force, out string msg)
        {
            var ba = sapi.World.BlockAccessor;
            var p = plr.Entity.Pos;
            var spots = LakeSearch.FindSpots(ba, p.XYZ, p.X, p.Z, Landing.MinPatch, rand);
            if (spots == null || spots.Count == 0) { msg = "No lake to land on within 160 blocks."; return false; }
            var centre = spots[spots.Count / 2];
            if (centre.HorizontalSquareDistanceTo(p.X, p.Z) < MinFromPlayer * MinFromPlayer) { msg = $"The nearest landing water is under {MinFromPlayer:0} blocks from you."; return false; }
            if (!force && !ResidentGeese.InSeason(sapi.World, centre.AsBlockPos)) { msg = "Not the geese's resident season there."; return false; }
            if (sapi.World.GetEntitiesAround(centre, 48, 24, e => e.Alive && e.Code.Equals(ResidentGeese.Code)).Length > 0) { msg = "Geese are already on that lake."; return false; }
            var type = sapi.World.GetEntityType(ResidentGeese.Code);
            if (type == null) { msg = "Entity type flocks:greylag is not loaded."; return false; }

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
            if (entry == 0) { msg = "No loaded ground far enough out for the flock to fly in from."; return false; }
            // a comfortable height above the water and the ground at the start; Waterfowl climbs over anything later
            double ey = Math.Max(centre.Y + 24, MaxGround(ba, ex, ez, dx, dz, -8, 40) + 12);

            var flock = FlockRegistry.Instance.TakeForLanding("goose", centre, rand);
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
            msg = $"A flock of {n} greylag geese (flock #{flock.Id}) is flying in from {entry} blocks out to land at {centre.X:0}, {centre.Y:0}, {centre.Z:0}.";
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
