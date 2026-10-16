using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Flocks
{
    /// <summary>One flock's lasting identity, saved with the world.</summary>
    [ProtoContract]
    public class FlockRecord
    {
        [ProtoMember(1)] public long Id;
        [ProtoMember(2)] public string Species;
        /// <summary>Living members. Deaths take it down; a flock that reaches 0 is Lost.</summary>
        [ProtoMember(3)] public int Count;
        [ProtoMember(4)] public FlockState State;
        /// <summary>Where it was last on the ground (landed), or where it was handed to the sky (away).</summary>
        [ProtoMember(5)] public double X;
        [ProtoMember(6)] public double Y;
        [ProtoMember(7)] public double Z;
        /// <summary>Game hours when it entered its current state.</summary>
        [ProtoMember(8)] public double SinceHours;
        /// <summary>Members killed over the flock's life.</summary>
        [ProtoMember(9)] public int Killed;
        /// <summary>How disturbed the flock is (lake wariness) as of WaryHours; it calms down over time.</summary>
        [ProtoMember(10)] public double Wariness;
        [ProtoMember(11)] public double WaryHours;
    }

    /// <summary>A lake a flock left because it kept being disturbed: no geese land or spawn near it for a while.</summary>
    [ProtoContract]
    public class AbandonedLake
    {
        [ProtoMember(1)] public double X;
        [ProtoMember(2)] public double Z;
        [ProtoMember(3)] public double Hours;
    }

    public enum FlockState { Landed = 0, Away = 1, Lost = 2 }

    /// <summary>
    /// Server-side record of every landed flock: which geese belong together, how many are left, and whether the flock
    /// is on a lake, away on migration, or lost (all killed). Geese carry their flock id in WatchedAttributes.
    /// A departing flock flies off as real geese; once DepartRange from every player its entities are removed and its
    /// record goes Away, so the migration schedule can bring it back (LandingFlocks).
    /// Lake wariness: every flush disturbs the flock (a kill a little more). A disturbed flock is skittish (it flees and
    /// takes off from further away); a flock disturbed again and again leaves the lake and rejoins the migration, and
    /// that lake gets no geese for a while. It is the place they learn to avoid, not the player, and it wears off.
    /// </summary>
    public class FlockRegistry
    {
        public const string FlockIdKey = "flocksFlockId", SaveKey = "flocks:registry", AbandonedKey = "flocks:abandonedlakes";
        /// <summary>Geese stay away from this far around an abandoned lake's last spot.</summary>
        public const double AbandonRadius = 64;
        /// <summary>Departing geese are removed this far from the nearest player: 3-4 px across by then. Must stay
        /// inside a flying goose's simulation range (EntityBehaviorWaterfowl.FlightRange), or it would stop ticking first.</summary>
        public const double DepartRange = 360;
        /// <summary>A goose with no flock joins a landed flock with a member this close, else starts its own.</summary>
        public const double JoinRange = 24;
        private const int MaxRecords = 256;

        public static FlockRegistry Instance { get; private set; }

        private readonly ICoreServerAPI sapi;
        private readonly FlocksServerConfig cfg;
        private Dictionary<long, FlockRecord> flocks = new();
        private List<AbandonedLake> abandoned = new();
        private long nextId = 1;
        private readonly Dictionary<long, double> lastDepartCheck = new();

        public FlockRegistry(ICoreServerAPI sapi, FlocksServerConfig cfg)
        {
            this.sapi = sapi; this.cfg = cfg; Instance = this;
            sapi.Event.SaveGameLoaded += Load;
            sapi.Event.GameWorldSave += Save;
        }

        private void Load()
        {
            try
            {
                var data = sapi.WorldManager.SaveGame.GetData(SaveKey);
                var list = data == null ? new List<FlockRecord>() : SerializerUtil.Deserialize<List<FlockRecord>>(data) ?? new List<FlockRecord>();
                flocks = list.ToDictionary(f => f.Id);
                nextId = flocks.Count == 0 ? 1 : flocks.Keys.Max() + 1;
                var ab = sapi.WorldManager.SaveGame.GetData(AbandonedKey);
                abandoned = ab == null ? new List<AbandonedLake>() : SerializerUtil.Deserialize<List<AbandonedLake>>(ab) ?? new List<AbandonedLake>();
            }
            catch (Exception ex) { sapi.Logger.Error("[Flocks] flock registry unreadable, starting empty: {0}", ex); flocks = new(); }
        }

        private void Save()
        {
            sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(flocks.Values.ToList()));
            PruneAbandoned();
            sapi.WorldManager.SaveGame.StoreData(AbandonedKey, SerializerUtil.Serialize(abandoned));
        }

        public FlockRecord Get(long id) => flocks.TryGetValue(id, out var f) ? f : null;

        public FlockRecord Create(string species, int count, Vec3d at)
        {
            var f = new FlockRecord { Id = nextId++, Species = species, Count = count, State = FlockState.Landed, X = at.X, Y = at.Y, Z = at.Z, SinceHours = sapi.World.Calendar.TotalHours };
            flocks[f.Id] = f;
            Trim();
            return f;
        }

        /// <summary>Gives a goose without a flock one: a landed flock with a member within JoinRange, else a new flock.
        /// Covers every way geese appear (vanilla group spawns, old saves, /flocks land stragglers).</summary>
        public void Adopt(Entity goose, string species)
        {
            if (goose.WatchedAttributes.HasAttribute(FlockIdKey)) return;
            var near = sapi.World.GetEntitiesAround(goose.Pos.XYZ, (float)JoinRange, 8, e => e != goose && e.Alive && e.Code.Equals(goose.Code)
                && Get(e.WatchedAttributes.GetLong(FlockIdKey, 0))?.State == FlockState.Landed);
            var f = near.Length > 0 ? Get(near[0].WatchedAttributes.GetLong(FlockIdKey)) : null;
            if (f != null) f.Count++;
            else f = Create(species, 1, goose.Pos.XYZ);
            goose.WatchedAttributes.SetLong(FlockIdKey, f.Id);
        }

        /// <summary>
        /// The flock that comes down in a landing: the flock away longest returns with whoever is left in it; failing
        /// that, a flock that was killed off is replaced by a new one (the old record goes); failing that, a new flock
        /// of min-max (the species' landing flock size). Marked Landed at the lake.
        /// </summary>
        public FlockRecord TakeForLanding(string species, Vec3d lake, Random rand, int min, int max)
        {
            var f = flocks.Values.Where(r => r.Species == species && r.State == FlockState.Away && r.Count > 0).OrderBy(r => r.SinceHours).FirstOrDefault();
            if (f == null)
            {
                var lost = flocks.Values.Where(r => r.Species == species && r.State == FlockState.Lost).OrderBy(r => r.SinceHours).FirstOrDefault();
                if (lost != null) flocks.Remove(lost.Id);
                f = Create(species, min + rand.Next(Math.Max(1, max - min + 1)), lake);
            }
            f.State = FlockState.Landed; f.X = lake.X; f.Y = lake.Y; f.Z = lake.Z; f.SinceHours = sapi.World.Calendar.TotalHours;
            return f;
        }

        /// <summary>A landing that couldn't complete (its lake unloaded): the flock is away again.</summary>
        public void LandingFailed(FlockRecord f) { f.State = FlockState.Away; f.SinceHours = sapi.World.Calendar.TotalHours; }

        // ---------------------------------------------------------------------------------------------- lake wariness

        private double Hours => sapi.World.Calendar.TotalHours;

        /// <summary>The flock's wariness now, after calming down by WarinessDecayPerDay per in-game day.</summary>
        public double Wariness(long id)
        {
            var f = Get(id);
            if (f == null || !cfg.LakeWariness) return 0;
            return Math.Max(0, f.Wariness - (Hours - f.WaryHours) / 24 * cfg.WarinessDecayPerDay);
        }

        /// <summary>Skittish: it flees and takes off from SkittishRangeFactor times further away.</summary>
        public bool Skittish(long id) => Wariness(id) >= cfg.SkittishAt;
        public double SkittishRangeFactor => cfg.SkittishRangeFactor;

        /// <summary>The flock was disturbed (a flush, or extra for a kill). True when it has had enough of this lake: it
        /// should leave, and the lake is abandoned for AbandonDays.</summary>
        public bool Disturb(long id, double amount, Vec3d at, bool mayLeave = true)
        {
            var f = Get(id);
            if (f == null || !cfg.LakeWariness) return false;
            f.Wariness = Wariness(id) + amount; f.WaryHours = Hours;
            if (!mayLeave || f.Wariness < cfg.LeaveAt) return false;
            f.Wariness = 0;   // the next lake is a fresh start
            abandoned.Add(new AbandonedLake { X = at.X, Z = at.Z, Hours = Hours });
            sapi.Logger.Notification("[Flocks] flock #{0} abandons the lake at {1:0}, {2:0} after repeated disturbance", id, at.X, at.Z);
            return true;
        }

        /// <summary>Within AbandonRadius of a lake a flock left in the last AbandonDays: no landings, no spawns.</summary>
        public bool IsAbandoned(double x, double z, double margin = 0)
        {
            if (!cfg.LakeWariness) return false;
            double since = Hours - cfg.AbandonDays * 24, r = AbandonRadius + margin;
            foreach (var a in abandoned)
                if (a.Hours >= since && (a.X - x) * (a.X - x) + (a.Z - z) * (a.Z - z) < r * r) return true;
            return false;
        }

        private void PruneAbandoned() { double since = Hours - cfg.AbandonDays * 24; abandoned.RemoveAll(a => a.Hours < since); }

        /// <summary>/flocks calm: every flock calm, no lake abandoned.</summary>
        public void Calm() { foreach (var f in flocks.Values) f.Wariness = 0; abandoned.Clear(); }

        /// <summary>A member was killed. factor: the species' WarinessFactor (bold birds mind a kill less).</summary>
        public void MemberDied(Entity goose, double factor = 1)
        {
            var f = Get(goose.WatchedAttributes.GetLong(FlockIdKey, 0));
            if (f == null) return;
            // only adds: the flush that follows adds the rest and decides whether they leave
            Disturb(f.Id, cfg.KillWariness * factor, goose.Pos.XYZ, mayLeave: false);
            f.Count = Math.Max(0, f.Count - 1); f.Killed++;
            if (f.Count == 0) { f.State = FlockState.Lost; f.SinceHours = sapi.World.Calendar.TotalHours; }
        }

        /// <summary>
        /// Called by a departing goose in flight. Collects every departing airborne member of its flock; once their
        /// centre is DepartRange from every player, the flock is Away and its geese are removed. Checked at most twice a
        /// second per flock.
        /// </summary>
        public void TryDepart(Entity goose)
        {
            long id = goose.WatchedAttributes.GetLong(FlockIdKey, 0);
            double now = sapi.World.ElapsedMilliseconds / 1000.0;
            if (lastDepartCheck.TryGetValue(id, out double last) && now - last < 0.5) return;
            lastDepartCheck[id] = now;

            var members = new List<(Entity e, EntityBehaviorBird b)>();
            foreach (var e in sapi.World.LoadedEntities.Values)
            {
                if (!e.Alive || !e.Code.Equals(goose.Code) || e.WatchedAttributes.GetLong(FlockIdKey, 0) != id) continue;
                if (id == 0 && e != goose) continue; // flockless: hand it off on its own
                var b = e.GetBehavior<EntityBehaviorBird>();
                if (b != null && b.DepartingInFlight) members.Add((e, b));
            }
            if (members.Count == 0) return;
            double cx = members.Average(m => m.e.Pos.X), cy = members.Average(m => m.e.Pos.Y), cz = members.Average(m => m.e.Pos.Z);
            var centre = new Vec3d(cx, cy, cz);
            var players = sapi.World.AllOnlinePlayers.Where(p => p.Entity != null).ToList();
            if (players.Any(p => p.Entity.Pos.HorDistanceTo(centre) < DepartRange)) return;

            var f = Get(id);
            if (f != null) { f.State = FlockState.Away; f.X = cx; f.Y = cy; f.Z = cz; f.SinceHours = sapi.World.Calendar.TotalHours; }
            foreach (var (e, _) in members) e.Die(EnumDespawnReason.Expire);
            lastDepartCheck.Remove(id);
        }

        /// <summary>Keeps the registry bounded: drops the oldest Lost, then the oldest Away records past MaxRecords.</summary>
        private void Trim()
        {
            if (flocks.Count <= MaxRecords) return;
            foreach (var f in flocks.Values.Where(f => f.State != FlockState.Landed).OrderBy(f => f.State == FlockState.Lost ? 0 : 1).ThenBy(f => f.SinceHours).Take(flocks.Count - MaxRecords).ToList())
                flocks.Remove(f.Id);
        }

        public string Report()
        {
            int landed = 0, away = 0, lost = 0, geese = 0;
            foreach (var f in flocks.Values)
            {
                if (f.State == FlockState.Landed) { landed++; geese += f.Count; }
                else if (f.State == FlockState.Away) away++; else lost++;
            }
            PruneAbandoned();
            int wary = flocks.Values.Count(f => f.State == FlockState.Landed && Skittish(f.Id));
            return $"Flocks: {landed} landed ({geese} geese, {wary} skittish), {away} away on migration, {lost} lost (all killed); " +
                   $"{abandoned.Count} lake(s) abandoned{(cfg.LakeWariness ? "" : " (lake wariness OFF)")}.";
        }
    }
}
