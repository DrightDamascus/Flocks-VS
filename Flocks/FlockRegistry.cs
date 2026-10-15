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
    }

    public enum FlockState { Landed = 0, Away = 1, Lost = 2 }

    /// <summary>Server -> clients: these departing birds are now a drawn flock; carry on from here.</summary>
    [ProtoContract]
    public class FlockHandoffPacket
    {
        [ProtoMember(1)] public string Species;
        [ProtoMember(2)] public double[] X;
        [ProtoMember(3)] public double[] Y;
        [ProtoMember(4)] public double[] Z;
        [ProtoMember(5)] public double Heading;
        [ProtoMember(6)] public double Speed;
        [ProtoMember(7)] public long FlockId;
    }

    /// <summary>
    /// Server-side record of every landed flock: which geese belong together, how many are left, and whether the flock
    /// is on a lake, away on migration, or lost (all killed). Geese carry their flock id in WatchedAttributes.
    /// A departing flock is handed to the clients' sky renderer once it is HandoffRange from every player: its entities
    /// are removed and every client nearby continues it as a drawn flock, and its record goes Away so the migration
    /// schedule can bring it back (landing flocks, next step).
    /// </summary>
    public class FlockRegistry
    {
        public const string FlockIdKey = "flocksFlockId", SaveKey = "flocks:registry";
        /// <summary>Departing geese become a drawn flock this far from the nearest player. Must stay well inside the
        /// server's simulation range (128): entities further than that from every player stop ticking.</summary>
        public const double HandoffRange = 80;
        /// <summary>Players this close to the hand-off get the drawn flock (the sky renderer's farthest reach).</summary>
        public const double BroadcastRange = 1600;
        /// <summary>A goose with no flock joins a landed flock with a member this close, else starts its own.</summary>
        public const double JoinRange = 24;
        private const int MaxRecords = 256;

        public static FlockRegistry Instance { get; private set; }

        private readonly ICoreServerAPI sapi;
        private readonly IServerNetworkChannel channel;
        private Dictionary<long, FlockRecord> flocks = new();
        private long nextId = 1;
        private readonly Dictionary<long, double> lastHandoffCheck = new();

        public FlockRegistry(ICoreServerAPI sapi, IServerNetworkChannel channel)
        {
            this.sapi = sapi; this.channel = channel; Instance = this;
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
            }
            catch (Exception ex) { sapi.Logger.Error("[Flocks] flock registry unreadable, starting empty: {0}", ex); flocks = new(); }
        }

        private void Save() => sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(flocks.Values.ToList()));

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

        public void MemberDied(Entity goose)
        {
            var f = Get(goose.WatchedAttributes.GetLong(FlockIdKey, 0));
            if (f == null) return;
            f.Count = Math.Max(0, f.Count - 1); f.Killed++;
            if (f.Count == 0) { f.State = FlockState.Lost; f.SinceHours = sapi.World.Calendar.TotalHours; }
        }

        /// <summary>
        /// Called by a departing goose in flight. Collects every departing airborne member of its flock; once their
        /// centre is HandoffRange from the nearest player, sends them to the clients as one drawn flock and removes them.
        /// Checked at most twice a second per flock.
        /// </summary>
        public void TryHandoff(Entity goose, string species)
        {
            long id = goose.WatchedAttributes.GetLong(FlockIdKey, 0);
            double now = sapi.World.ElapsedMilliseconds / 1000.0;
            if (lastHandoffCheck.TryGetValue(id, out double last) && now - last < 0.5) return;
            lastHandoffCheck[id] = now;

            var members = new List<(Entity e, EntityBehaviorWaterfowl b)>();
            foreach (var e in sapi.World.LoadedEntities.Values)
            {
                if (!e.Alive || !e.Code.Equals(goose.Code) || e.WatchedAttributes.GetLong(FlockIdKey, 0) != id) continue;
                if (id == 0 && e != goose) continue; // flockless: hand it off on its own
                var b = e.GetBehavior<EntityBehaviorWaterfowl>();
                if (b != null && b.DepartingInFlight) members.Add((e, b));
            }
            if (members.Count == 0) return;
            double cx = members.Average(m => m.e.Pos.X), cy = members.Average(m => m.e.Pos.Y), cz = members.Average(m => m.e.Pos.Z);
            var centre = new Vec3d(cx, cy, cz);
            var players = sapi.World.AllOnlinePlayers.Where(p => p.Entity != null).ToList();
            if (players.Any(p => p.Entity.Pos.HorDistanceTo(centre) < HandoffRange)) return;

            // average heading as a vector, so headings either side of north don't cancel out
            double hx = members.Sum(m => Math.Sin(m.b.FlyHeading)), hz = members.Sum(m => Math.Cos(m.b.FlyHeading));
            var pkt = new FlockHandoffPacket
            {
                Species = species, FlockId = id, Heading = Math.Atan2(hx, hz), Speed = members.Average(m => m.b.FlySpeed),
                X = members.Select(m => m.e.Pos.X).ToArray(), Y = members.Select(m => m.e.Pos.Y).ToArray(), Z = members.Select(m => m.e.Pos.Z).ToArray(),
            };
            var send = players.Where(p => p.Entity.Pos.HorDistanceTo(centre) < BroadcastRange).Select(p => (IServerPlayer)p).ToArray();
            if (send.Length > 0) channel.SendPacket(pkt, send);

            var f = Get(id);
            if (f != null) { f.State = FlockState.Away; f.X = cx; f.Y = cy; f.Z = cz; f.SinceHours = sapi.World.Calendar.TotalHours; }
            foreach (var (e, _) in members) e.Die(EnumDespawnReason.Expire);
            lastHandoffCheck.Remove(id);
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
            return $"Flocks: {landed} landed ({geese} geese), {away} away on migration, {lost} lost (all killed).";
        }
    }
}
