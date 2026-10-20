using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>
    /// /flocks journey: one flock's whole migration, followed from above (FlockCamera). It leaves its lake and flies the
    /// real way to the tropics (within 23.4 deg of the equator) over generated terrain, finds water
    /// there and settles; the clock moves on to the start of spring migration at home; then the same flock flies the whole
    /// way back and lands on its own lake. The flock stays in the world all the way (FlockRegistry.TryDepart skips it);
    /// on the way it is steered by its course (EntityBehaviorWaterfowl.JourneyCourse), and slows where the terrain ahead
    /// isn't loaded yet. Stopping early (Sneak, /flocks unwatch, leaving): on the way out it just carries on departing;
    /// later it is sent away (FlockRegistry.SendAway) so an ordinary spring landing brings it back.
    /// </summary>
    public class FlockJourney
    {
        private enum Stage { Out, Seek, Winter, Home, Land }

        private class Trip
        {
            public IServerPlayer Plr; public long Id; public Species Sp; public Vec3d Home;
            public Stage Stage; public double StageTime, Missing, Retry, Report, Tropics;
        }

        public static FlockJourney Instance { get; private set; }
        /// <summary>Whether this flock is on a journey (it isn't handed off far out).</summary>
        public static bool Has(long flockId) => flockId != 0 && Instance?.trips.ContainsKey(flockId) == true;

        private readonly ICoreServerAPI sapi;
        private readonly FlockCamera camera;
        /// <summary>Moves the clock to the start of a species' spring or autumn migration at a place (FlocksModSystem.MoveClockToSeason).</summary>
        private readonly System.Func<Species, bool, BlockPos, string> moveClock;
        private readonly Dictionary<long, Trip> trips = new();
        private readonly Random rand = new();
        private const double Tick = 1, LandingRange = 200;

        public FlockJourney(ICoreServerAPI sapi, FlockCamera camera, System.Func<Species, bool, BlockPos, string> moveClock)
        {
            this.sapi = sapi; this.camera = camera; this.moveClock = moveClock; Instance = this;
            sapi.Event.RegisterGameTickListener(_ => { foreach (var t in trips.Values.ToList()) Step(t); }, (int)(Tick * 1000));
        }

        private static double Lat(IWorldAccessor w, double z) => w.Calendar.OnGetLatitude((int)z);

        /// <summary>A journey's flock winters in the tropics: within the Tropic of Cancer or Capricorn (23.4 degrees; the
        /// game's latitude runs -1 to 1 for -90 to 90). Further toward the equator than the mod's no-migration band
        /// (MigrationScheduler.Tropical, ~10 degrees) is not needed: that one is about where resident birds stay put.</summary>
        private const double TropicLatitude = 23.4 / 90;
        private static bool InTheTropics(double lat) => Math.Abs(lat) < TropicLatitude;

        /// <summary>Sets the flock round this bird off on its journey, with the player watching.</summary>
        public TextCommandResult Start(IServerPlayer plr, Species sp, Entity bird)
        {
            long id = bird.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0);
            if (id == 0) return TextCommandResult.Error(Lang.Get("flocks:watch-no-flock"));
            if (sp == null || sp.YearRound) return TextCommandResult.Error(Lang.Get("flocks:season-year-round", sp?.DisplayName ?? bird.Code.Path));
            if (trips.Values.Any(t => t.Plr.PlayerUID == plr.PlayerUID)) return TextCommandResult.Error(Lang.Get("flocks:journey-busy"));
            var members = Members(id);
            var home = Centre(members);
            double lat = Lat(sapi.World, home.Z);
            if (InTheTropics(lat)) return TextCommandResult.Error(Lang.Get("flocks:journey-tropics"));
            // toward the equator: whichever way along Z latitude falls from here. Latitude repeats along Z (pole to pole
            // and back), so +Z is toward the equator in some parts of a world and away from it in others. Heading 0
            // runs +Z, pi -Z.
            double dz = Math.Abs(Lat(sapi.World, home.Z + 500)) < Math.Abs(lat) ? 1 : -1, course = dz > 0 ? 0 : Math.PI;
            double dist = -1;
            for (double d = 0; d <= 3_000_000; d += 250)
                if (InTheTropics(Lat(sapi.World, home.Z + dz * d))) { dist = d; break; }
            if (dist < 0) return TextCommandResult.Error(Lang.Get("flocks:journey-no-tropics"));
            string err = camera.Begin(plr);
            if (err != null) return TextCommandResult.Error(err);
            var trip = new Trip { Plr = plr, Id = id, Sp = sp, Home = home, Stage = Stage.Out, Tropics = home.Z + dz * dist };
            trips[id] = trip;
            foreach (var m in members) m.GetBehavior<EntityBehaviorWaterfowl>()?.JourneyCourse(course);
            camera.Follow(plr, id, home, journey: true);
            double speed = 13;
            return TextCommandResult.Success(Lang.Get("flocks:journey-started", id, members.Count, sp.DisplayName, dist.ToString("0"), (dist / speed / 60).ToString("0")));
        }

        private List<Entity> Members(long id)
            => sapi.World.LoadedEntities.Values.Where(e => e.Alive && e.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0) == id
                && e.GetBehavior<EntityBehaviorWaterfowl>() != null).ToList();

        private static Vec3d Centre(List<Entity> m)
            => m.Count == 0 ? null : new Vec3d(m.Average(e => e.Pos.X), m.Average(e => e.Pos.Y), m.Average(e => e.Pos.Z));

        private void Tell(Trip t, string key, params object[] args)
            => t.Plr.SendMessage(GlobalConstants.GeneralChatGroup, "[Flocks] " + Lang.Get(key, args), EnumChatType.Notification);

        private void Step(Trip t)
        {
            if (t.Plr.ConnectionState != EnumClientState.Playing || !camera.Watching(t.Plr)) { Cancel(t.Plr); return; }
            var members = Members(t.Id);
            if (members.Count == 0)
            {
                // birds just out of the loaded area come back as the camera catches up; for long, the flock is lost
                if ((t.Missing += Tick) > 30) { Tell(t, "flocks:journey-lost"); End(t, sendAway: true); }
                return;
            }
            t.Missing = 0;
            t.StageTime += Tick;
            var c = Centre(members);
            var birds = members.Select(m => m.GetBehavior<EntityBehaviorWaterfowl>()).ToList();
            switch (t.Stage)
            {
                case Stage.Out:
                    if (InTheTropics(Lat(sapi.World, c.Z))) { Next(t, Stage.Seek); Tell(t, "flocks:journey-tropics-reached"); break; }
                    if ((t.Report += Tick) >= 120) { t.Report = 0; Tell(t, "flocks:journey-progress", Math.Abs(t.Tropics - c.Z).ToString("0")); }
                    break;

                case Stage.Seek:
                    // let them look for water to winter on; again every few seconds, flying on meanwhile
                    if ((t.Retry -= Tick) <= 0) { t.Retry = 8; foreach (var b in birds) b.JourneySeekWater(); }
                    if (birds.All(b => b.OnWater)) { Next(t, Stage.Winter); Tell(t, "flocks:journey-wintering"); }
                    else if (t.StageTime > 240) { Next(t, Stage.Winter); Tell(t, "flocks:journey-no-water"); }
                    break;

                case Stage.Winter:
                    if (t.StageTime < 10) break;
                    string clock = moveClock(t.Sp, true, t.Home.AsBlockPos);
                    Tell(t, "flocks:journey-spring", clock, Math.Sqrt(c.HorizontalSquareDistanceTo(t.Home.X, t.Home.Z)).ToString("0"));
                    Next(t, Stage.Home);
                    goto case Stage.Home;

                case Stage.Home:
                {
                    double toHome = Math.Atan2(t.Home.X - c.X, t.Home.Z - c.Z);
                    foreach (var b in birds) b.JourneyCourse(toHome);
                    if (Math.Sqrt(c.HorizontalSquareDistanceTo(t.Home.X, t.Home.Z)) > LandingRange) break;
                    // close: their own lake (its chunks are loaded by now), else whatever water they find
                    var spots = LakeSearch.FindSpots(sapi.World.BlockAccessor, t.Home, c.X, c.Z, t.Sp.MinLake(Landing.MinPatch), rand, t.Sp.Liquid);
                    foreach (var b in birds)
                        if (spots != null && spots.Count > 0) b.JourneyLand(spots[rand.Next(spots.Count)]); else b.JourneySeekWater();
                    Next(t, Stage.Land);
                    break;
                }

                case Stage.Land:
                    if (birds.All(b => b.OnWater))
                    {
                        if ((t.Retry += Tick) >= 5) { Tell(t, "flocks:journey-home", t.Id, members.Count); End(t, sendAway: false); }
                    }
                    else if (t.StageTime > 180) { Tell(t, "flocks:journey-home", t.Id, members.Count); End(t, sendAway: false); }
                    else if (t.StageTime % 20 == 0) foreach (var b in birds) if (b.Flying && !b.HasSpot) b.JourneySeekWater();
                    break;
            }
        }

        private static void Next(Trip t, Stage s) { t.Stage = s; t.StageTime = 0; t.Retry = 0; }

        /// <summary>The player stopped watching before the end. Out of sight of home on the way out, the flock simply
        /// carries on leaving; anywhere further along it is sent away, to come back in an ordinary spring landing.</summary>
        public void Cancel(IServerPlayer plr)
        {
            var t = trips.Values.FirstOrDefault(x => x.Plr.PlayerUID == plr.PlayerUID);
            if (t == null) return;
            End(t, sendAway: t.Stage != Stage.Out && t.Stage != Stage.Land);
        }

        private void End(Trip t, bool sendAway)
        {
            trips.Remove(t.Id);   // first: the camera's Stop calls Cancel
            if (sendAway)
            {
                var members = Members(t.Id);
                FlockRegistry.Instance?.SendAway(t.Id, Centre(members) ?? t.Home);
                foreach (var m in members) m.Die(EnumDespawnReason.Expire);
            }
            camera.Stop(t.Plr);
        }
    }
}
