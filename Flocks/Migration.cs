using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    public class PlannedFlock
    {
        public Species Species;
        public double Hour;      // game hour of day it sets off
        public int Count;
        public double Heading;   // radians, FlockRenderer convention: dir = (sin, 0, cos), so pi = north (-Z)
        public double Altitude;  // blocks above the player's ground
        public string Status = "pending";
        public bool Finished;
    }

    /// <summary>
    /// Plans each in-game day's flocks from the world seed and day number (so the plan is the same on every reload),
    /// then sends each one when the clock reaches its hour, unless a storm is on or another flock is still overhead.
    /// </summary>
    public class MigrationScheduler
    {
        private readonly ICoreClientAPI capi;
        private readonly FlockRenderer renderer;
        private readonly Dictionary<string, Species> species;
        private readonly FlocksConfig cfg;
        private int day = int.MinValue;
        private readonly List<PlannedFlock> plan = new();

        public MigrationScheduler(ICoreClientAPI capi, FlockRenderer renderer, Dictionary<string, Species> species, FlocksConfig cfg)
        {
            this.capi = capi; this.renderer = renderer; this.species = species; this.cfg = cfg;
        }

        public void Tick(float dt)
        {
            var player = capi.World?.Player?.Entity;
            if (player == null) return;
            var cal = capi.World.Calendar;
            int today = (int)cal.TotalDays;
            if (today != day) { day = today; Plan(player.Pos.AsBlockPos); }
            if (!cfg.AutoMigration) return;

            double hour = cal.HourOfDay;
            foreach (var p in plan)
            {
                if (p.Finished || hour < p.Hour) continue;
                bool late = hour > p.Hour + cfg.MaxDelayHours;
                if (!p.Species.Meshes.Ok) { Finish(p, "skipped: no meshes"); continue; }
                string hold = renderer.Count > 0 ? "sky busy" : (Stormy(player.Pos.AsBlockPos) ? "storm" : null);
                if (hold == null && !late)
                {
                    renderer.Send(p.Species, p.Count, p.Heading, p.Altitude);
                    Finish(p, $"sent {HourText(hour)}");
                }
                else if (late) Finish(p, p.Status.StartsWith("held") ? $"skipped: {hold ?? "held too long"}" : "missed (time skipped past it)");
                else p.Status = "held: " + hold;
            }
        }

        private void Finish(PlannedFlock p, string status)
        {
            p.Finished = true; p.Status = status;
            if (cfg.LogSchedule) capi.Logger.Notification("[Flocks] day {0} {1} {2} x{3}: {4}", day, HourText(p.Hour), p.Species.Code, p.Count, status);
        }

        /// <summary>Storm-strength wind or a downpour at the player. Ordinary rain and breezes don't stop migration.</summary>
        public bool Stormy(BlockPos pos)
        {
            var ba = capi.World.BlockAccessor;
            double wind = ba.GetWindSpeedAt(pos)?.Length() ?? 0;
            var climate = ba.GetClimateAt(pos, EnumGetClimateMode.NowValues);
            return wind >= cfg.StormWind || (climate != null && climate.Rainfall >= cfg.StormPrecipitation);
        }

        private void Plan(BlockPos pos)
        {
            plan.Clear();
            var cal = capi.World.Calendar;
            float rel = cal.GetSeasonRel(pos);
            double lat = cal.OnGetLatitude(pos.Z);
            foreach (var sp in species.Values)
            {
                if (!cfg.SpeciesEnabled(sp.Code)) continue;
                double strength = Strength(sp, rel, lat, out bool spring);
                if (cfg.ForceMigration) { strength = 1; spring = false; }
                if (strength <= 0) continue;
                var rnd = new Random(StableSeed(capi.World.Seed, day, sp.Code));
                int n = (int)Math.Floor(sp.MaxFlocksPerDay * strength * cfg.FlocksPerDayMultiplier + rnd.NextDouble());
                // spring heads poleward, autumn toward the equator; in VS north is -Z, which is heading pi here
                bool headNorth = spring == (lat >= 0);
                double baseHeading = headNorth ? Math.PI : 0;
                for (int i = 0; i < n; i++)
                {
                    plan.Add(new PlannedFlock
                    {
                        Species = sp, Hour = PickHour(sp, rnd), Count = rnd.Next(sp.CountMin, sp.CountMax + 1),
                        Heading = baseHeading + (rnd.NextDouble() * 2 - 1) * 20 * GameMath.DEG2RAD,
                        Altitude = sp.AltitudeMin + rnd.NextDouble() * (sp.AltitudeMax - sp.AltitudeMin),
                    });
                }
            }
            plan.Sort((a, b) => a.Hour.CompareTo(b.Hour));
            // the clock may already be past some of today's flocks (just joined, or slept): those quietly don't happen
            double now = cal.HourOfDay;
            foreach (var p in plan) if (now > p.Hour + cfg.MaxDelayHours) { p.Finished = true; p.Status = "missed (before you arrived)"; }
            if (cfg.LogSchedule)
                capi.Logger.Notification("[Flocks] day {0}: seasonRel={1:0.000} latitude={2:0.000} -> {3}", day, rel, lat, plan.Count == 0 ? "no flocks" : Describe());
        }

        /// <summary>
        /// 0..1 how strongly this species migrates today. Each window rises and falls as a sine, peaking mid-window.
        /// No migration within ~10 deg of the equator (that's where they winter); further from it, autumn starts
        /// earlier and spring arrives later, by up to ~3% of a year at the poles.
        /// </summary>
        public static double Strength(Species sp, float rel, double lat, out bool spring)
        {
            spring = false;
            double a = Math.Abs(lat);
            if (a < 0.11) return 0;
            float shift = (float)((a - 0.5) * 0.06);
            double s = Window(rel, sp.SpringStart + shift, sp.SpringEnd + shift) * sp.SpringWeight;
            double f = Window(rel, sp.AutumnStart - shift, sp.AutumnEnd - shift) * sp.AutumnWeight;
            spring = s >= f;
            return Math.Max(s, f);
        }

        /// <summary>
        /// Resident season: from the start of the spring window to the start of the autumn window, with the same
        /// latitude shift as Strength(). Within ~10 deg of the equator (no migration) residents stay all year.
        /// </summary>
        public static bool ResidentSeason(float springStart, float autumnStart, float rel, double lat)
        {
            double a = Math.Abs(lat);
            if (a < 0.11) return true;
            float shift = (float)((a - 0.5) * 0.06);
            return rel >= springStart + shift && rel < autumnStart - shift;
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

        public string Describe()
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

        /// <summary>Today's plan and the conditions behind it, for /flocks schedule.</summary>
        public string Report()
        {
            var player = capi.World?.Player?.Entity;
            if (player == null) return "No player.";
            var pos = player.Pos.AsBlockPos;
            var cal = capi.World.Calendar;
            float rel = cal.GetSeasonRel(pos);
            double lat = cal.OnGetLatitude(pos.Z);
            var sb = new StringBuilder();
            sb.Append($"Day {day}, {HourText(cal.HourOfDay)}, {cal.GetSeason(pos)} (seasonRel {rel:0.000}), latitude {lat:0.000}");
            if (!cfg.AutoMigration) sb.Append(", automatic migration OFF in flocks.json");
            if (cfg.ForceMigration) sb.Append(", ForceMigration ON (every day is peak autumn)");
            if (Stormy(pos)) sb.Append(", storm: flocks held");
            foreach (var sp in species.Values)
            {
                double s = Strength(sp, rel, lat, out bool spring);
                if (!cfg.SpeciesEnabled(sp.Code)) { sb.Append($"\n{sp.Code}: off in flocks.json"); continue; }
                sb.Append($"\n{sp.Code}: " + (s > 0 ? $"{(spring ? "spring" : "autumn")} migration, strength {s:0.00}" : "not migrating today"));
            }
            sb.Append("\nToday: " + (plan.Count == 0 ? "no flocks" : Describe().Replace("; ", "\n  ")));
            return sb.ToString();
        }

        private static string Compass(double heading)
        {
            // heading 0 = +Z = south, pi = -Z = north, pi/2 = +X = east
            string[] names = { "S", "SE", "E", "NE", "N", "NW", "W", "SW" };
            int i = (int)Math.Round(GameMath.Mod(heading, 2 * Math.PI) / (Math.PI / 4)) % 8;
            return names[i];
        }

        private static string HourText(double hour)
        {
            int h = (int)hour, m = (int)((hour - h) * 60);
            return $"{h:00}:{m:00}";
        }
    }
}
