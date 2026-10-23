using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    public class PlannedFlock
    {
        public Species Species;
        public double Hour;      // game hour of day it sets off
        public int Count;
        public double Heading;   // radians, SkyFlight convention: dir = (sin, 0, cos), so pi = north (-Z)
        public double Altitude;  // blocks above the player's ground
        public string Status = "pending";
        public bool Finished;
    }

    /// <summary>
    /// Plans each in-game day's passing flocks for each player (server), from the world seed and day number (so the plan
    /// is the same on every reload) and the season and latitude where the player is, then sends each one over the player
    /// when the clock reaches its hour (SkyFlocks), unless a storm is on. Several flocks can be up at once; they are kept
    /// apart in height (SkyFlight.Separation). Players near each other share a sky: a flock isn't sent while another of
    /// its kind is already passing near the player. The settings are flocks.json's (FlocksConfig), the server's copy.
    /// </summary>
    public class MigrationScheduler
    {
        private readonly ICoreServerAPI sapi;
        private readonly SkyFlocks sky;
        private readonly Dictionary<string, Species> species;
        private readonly FlocksConfig cfg;
        private readonly FlocksServerConfig serverCfg;
        /// <summary>Each player's plan for the day (by player UID).</summary>
        private readonly Dictionary<string, (int day, List<PlannedFlock> plan)> plans = new();
        /// <summary>A flock isn't sent while one of its kind is passing within this of the player (another player's).</summary>
        public const double SharedSky = 600;

        public MigrationScheduler(ICoreServerAPI sapi, SkyFlocks sky, Dictionary<string, Species> species, FlocksConfig cfg, FlocksServerConfig serverCfg)
        {
            this.sapi = sapi; this.sky = sky; this.species = species; this.cfg = cfg; this.serverCfg = serverCfg;
            sapi.Event.RegisterGameTickListener(Tick, 1000);
            sapi.Event.PlayerDisconnect += plr => plans.Remove(plr.PlayerUID);
        }

        /// <summary>The player's plan for today, made when first needed.</summary>
        private List<PlannedFlock> Today(IServerPlayer plr)
        {
            int today = (int)sapi.World.Calendar.TotalDays;
            if (!plans.TryGetValue(plr.PlayerUID, out var p) || p.day != today)
                plans[plr.PlayerUID] = p = (today, Plan(plr.Entity.Pos.AsBlockPos, today, plr.PlayerName));
            return p.plan;
        }

        private void Tick(float dt)
        {
            foreach (IServerPlayer plr in sapi.World.AllOnlinePlayers)
            {
                if (plr.Entity == null || plr.ConnectionState != EnumClientState.Playing) continue;
                if (FlockCamera.Instance?.Watching(plr) == true) continue;   // off following a flock (FlockCamera)
                var plan = Today(plr);
                if (cfg.AutoMigration) Run(plr, plan);
            }
        }

        private void Run(IServerPlayer plr, List<PlannedFlock> plan)
        {
            int day = (int)sapi.World.Calendar.TotalDays;
            double hour = sapi.World.Calendar.HourOfDay;
            var at = plr.Entity.Pos.XYZ;
            var pos = plr.Entity.Pos.AsBlockPos;
            foreach (var p in plan)
            {
                if (p.Finished || hour < p.Hour) continue;
                bool late = hour > p.Hour + cfg.MaxDelayHours;
                if (!serverCfg.SpeciesEnabled(p.Species) || !BiomesCompat.Allows(sapi, p.Species, pos)) { Finish(p, day, "skipped: off on this server, or not in this realm (Biomes)"); continue; }
                // sea birds pass along the coast: only where there is sea near the player
                if (p.Species.Sea && !SeaNear(pos)) { Finish(p, day, "skipped: no sea nearby"); continue; }
                if (sky.Near(p.Species.Code, at, SharedSky) != null) { Finish(p, day, "skipped: some are passing here already"); continue; }
                string hold = Stormy(pos) ? "storm" : null;
                if (hold == null && !late)
                {
                    sky.Launch(p.Species, p.Count, at, p.Heading, p.Altitude);
                    Finish(p, day, $"sent {HourText(hour)}");
                }
                else if (late) Finish(p, day, p.Status.StartsWith("held") ? $"skipped: {hold ?? "held too long"}" : "missed (time skipped past it)");
                else p.Status = "held: " + hold;
            }
        }

        private void Finish(PlannedFlock p, int day, string status)
        {
            p.Finished = true; p.Status = status;
            if (cfg.LogSchedule) sapi.Logger.Notification("[Flocks] day {0} {1} {2} x{3}: {4}", day, HourText(p.Hour), p.Species.Code, p.Count, status);
        }

        /// <summary>Storm-strength wind or a downpour at the player. Ordinary rain and breezes don't stop migration.</summary>
        public bool Stormy(BlockPos pos)
        {
            var ba = sapi.World.BlockAccessor;
            double wind = ba.GetWindSpeedAt(pos)?.Length() ?? 0;
            var climate = ba.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            return wind >= cfg.StormWind || (climate != null && climate.Rainfall >= cfg.StormPrecipitation);
        }

        private List<PlannedFlock> Plan(BlockPos pos, int day, string who)
        {
            var plan = new List<PlannedFlock>();
            var cal = sapi.World.Calendar;
            float rel = cal.GetSeasonRel(pos);
            double lat = cal.OnGetLatitude(pos.Z);
            foreach (var sp in species.Values.Where(s => cfg.SpeciesEnabled(s.Code)))
            {
                double strength = Strength(sp, rel, lat, out bool spring);
                if (cfg.ForceMigration) { strength = 1; spring = false; }
                if (strength <= 0) continue;
                var rnd = new Random(StableSeed(sapi.World.Seed, day, sp.Code));
                int n = (int)Math.Floor(sp.MaxFlocksPerDay * strength * cfg.FlocksPerDayMultiplier + rnd.NextDouble());
                // spring heads poleward, autumn toward the equator; in VS north is -Z, which is heading pi here.
                // Birds that stay all year go wherever they like.
                bool headNorth = spring == (lat >= 0);
                double baseHeading = headNorth ? Math.PI : 0;
                for (int i = 0; i < n; i++)
                {
                    plan.Add(new PlannedFlock
                    {
                        Species = sp, Hour = PickHour(sp, rnd), Count = rnd.Next(sp.CountMin, sp.CountMax + 1),
                        Heading = sp.YearRound ? rnd.NextDouble() * Math.PI * 2 : baseHeading + (rnd.NextDouble() * 2 - 1) * 20 * GameMath.DEG2RAD,
                        Altitude = sp.RandomAltitude(rnd),
                    });
                }
            }
            plan.Sort((a, b) => a.Hour.CompareTo(b.Hour));
            // the clock may already be past some of today's flocks (just joined, or slept): those quietly don't happen
            double now = cal.HourOfDay;
            foreach (var p in plan) if (now > p.Hour + cfg.MaxDelayHours) { p.Finished = true; p.Status = "missed (before you arrived)"; }
            if (cfg.LogSchedule)
                sapi.Logger.Notification("[Flocks] day {0} for {1}: seasonRel={2:0.000} latitude={3:0.000} -> {4}", day, who, rel, lat, plan.Count == 0 ? "no flocks" : Describe(plan));
            return plan;
        }

        /// <summary>
        /// 0..1 how strongly this species migrates today. Each window rises and falls as a sine, peaking mid-window.
        /// No migration within ~10 deg of the equator (that's where they winter); further from it, autumn starts
        /// earlier and spring arrives later, by up to ~3% of a year at the poles.
        /// </summary>
        public static double Strength(Species sp, float rel, double lat, out bool spring)
        {
            spring = false;
            if (sp.YearRound) return 1;     // no migration: about the same every day
            if (Tropical(lat)) return 0;
            float shift = LatitudeShift(lat);
            double s = Window(rel, sp.SpringStart + shift, sp.SpringEnd + shift) * sp.SpringWeight;
            double f = Window(rel, sp.AutumnStart - shift, sp.AutumnEnd - shift) * sp.AutumnWeight;
            spring = s >= f;
            return Math.Max(s, f);
        }

        /// <summary>
        /// Whether birds that land are here at this point of the year (rel: seasonRel). They arrive as the spring
        /// migration starts and leave as the autumn one starts, both moved by latitude exactly as in Strength(). In the
        /// tropics (latitude under 0.11, about 10 deg) nothing migrates, so they never leave.
        /// </summary>
        public static bool ResidentSeason(float springStart, float autumnStart, float rel, double lat)
        {
            if (Tropical(lat)) return true;
            return springStart + LatitudeShift(lat) <= rel && rel < autumnStart - LatitudeShift(lat);
        }

        /// <summary>Within ~10 deg of the equator, where nothing migrates.</summary>
        public static bool Tropical(double lat) => Math.Abs(lat) < 0.11;

        /// <summary>How much later spring (and earlier autumn) comes at this latitude, as a share of the year.</summary>
        public static float LatitudeShift(double lat) => (float)((Math.Abs(lat) - 0.5) * 0.06);

        /// <summary>The seasonRel at which the species' spring (arrival) or autumn (departure) migration opens here.</summary>
        public static float SeasonStart(Species sp, bool spring, double lat)
            => spring ? sp.SpringStart + LatitudeShift(lat) : sp.AutumnStart - LatitudeShift(lat);

        /// <summary>In-game days from seasonRel 'rel' forward to seasonRel 'target' (wrapping round the year).</summary>
        public static double DaysUntil(float target, float rel, int daysPerYear) => GameMath.Mod(target - rel, 1f) * daysPerYear;

        /// <summary>Sea (salt water at the surface) within 96 blocks of pos, sampled every 8 blocks in loaded chunks.</summary>
        private bool SeaNear(BlockPos pos)
        {
            var ba = sapi.World.BlockAccessor;
            for (int dx = -96; dx <= 96; dx += 8)
                for (int dz = -96; dz <= 96; dz += 8)
                    if (dx * dx + dz * dz <= 96 * 96 && LakeSearch.Surface(ba, pos.X + dx, pos.Z + dz, out _, "saltwater")) return true;
            return false;
        }

        private static double Window(float rel, float start, float end)
            => rel <= start || rel >= end ? 0 : Math.Sin(Math.PI * (rel - start) / (end - start));

        /// <summary>A random hour inside the species' flying hours, longer windows proportionally more likely.</summary>
        internal static double PickHour(Species sp, Random rnd)
        {
            double total = 0;
            for (int i = 0; i + 1 < sp.Hours.Length; i += 2) total += sp.Hours[i + 1] - sp.Hours[i];
            double t = rnd.NextDouble() * total;
            for (int i = 0; i + 1 < sp.Hours.Length; i += 2)
            {
                double len = sp.Hours[i + 1] - sp.Hours[i];
                if (t < len) return sp.Hours[i] + t;
                t -= len;
            }
            return sp.Hours[0];
        }

        /// <summary>FNV-1a over seed, day and species code; string.GetHashCode is randomised per process.</summary>
        private static int StableSeed(int worldSeed, int day, string code)
        {
            unchecked
            {
                uint h = 2166136261;
                void Mix(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 16777619; } }
                Mix(worldSeed); Mix(day);
                foreach (char c in code) Mix(c);
                return (int)h;
            }
        }

        private static string Describe(List<PlannedFlock> plan)
        {
            if (plan.Count == 0) return "no flocks";
            var sb = new StringBuilder();
            foreach (var p in plan)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append($"{HourText(p.Hour)} {p.Species.Code} x{p.Count} heading {Compass(p.Heading)} - {p.Status}");
            }
            return sb.ToString();
        }

        /// <summary>Today's plan for the player and the conditions behind it, for /flocks schedule, with the flocks up
        /// near them now. local: the part about landed flocks, appended.</summary>
        public string Report(IServerPlayer plr, string local = null)
        {
            var player = plr?.Entity;
            if (player == null) return "No player.";
            var pos = player.Pos.AsBlockPos;
            var cal = sapi.World.Calendar;
            var plan = Today(plr);
            float rel = cal.GetSeasonRel(pos);
            double lat = cal.OnGetLatitude(pos.Z);
            var sb = new StringBuilder();
            sb.Append($"Day {(int)cal.TotalDays}, {HourText(cal.HourOfDay)}, {cal.GetSeason(pos)} (seasonRel {rel:0.000}), latitude {lat:0.000}");
            if (!cfg.AutoMigration) sb.Append(", automatic migration OFF in flocks.json");
            if (cfg.ForceMigration) sb.Append(", ForceMigration ON (every day is peak autumn)");
            if (Stormy(pos)) sb.Append(", storm: flocks held");
            foreach (var sp in species.Values)
            {
                double s = Strength(sp, rel, lat, out bool spring);
                string state;
                if (!cfg.SpeciesEnabled(sp.Code)) state = "off in flocks.json";
                else if (!serverCfg.SpeciesEnabled(sp)) state = "off in flocks-server.json";
                else if (!BiomesCompat.Allows(sapi, sp, pos)) state = "not of this realm (Biomes)";
                else if (sp.YearRound) state = "resident all year" + (sp.Sea ? (SeaNear(pos) ? ", sea nearby" : ", no sea nearby") : "");
                else state = s > 0 ? $"{(spring ? "spring" : "autumn")} migration, strength {s:0.00}" : "not migrating today";
                sb.Append($"\n{sp.Code}: {state}");
            }
            sb.Append("\nToday: " + (plan.Count == 0 ? "no flocks" : Describe(plan).Replace("; ", "\n  ")));
            var up = sky.Describe(player.Pos.XYZ);
            sb.Append("\nIn the sky near you: " + (up.Count == 0 ? "none" : "\n  " + string.Join("\n  ", up)));
            if (!string.IsNullOrEmpty(local)) sb.Append("\n" + local);
            return sb.ToString();
        }

        private static string Compass(double heading)
        {
            // heading 0 = +Z = south, pi = -Z = north, pi/2 = +X = east
            string[] names = { "S", "SE", "E", "NE", "N", "NW", "W", "SW" };
            int i = (int)Math.Round(GameMath.Mod(heading, 2 * Math.PI) / (Math.PI / 4)) % 8;
            return names[i];
        }

        internal static string HourText(double hour)
        {
            int h = (int)hour, m = (int)((hour - h) * 60);
            return $"{h:00}:{m:00}";
        }
    }
}
