using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>Server -> client: a passing flock as it is now, for drawing. Gone: it has left the sky.</summary>
    [ProtoContract]
    public class SkyFlockPacket
    {
        [ProtoMember(1)] public long Id;
        [ProtoMember(2)] public string Species;
        [ProtoMember(3)] public int Seed;
        [ProtoMember(4)] public double X;
        [ProtoMember(5)] public double Y;
        [ProtoMember(6)] public double Z;
        [ProtoMember(7)] public double BaseHeading;
        [ProtoMember(8)] public double Wander1;
        [ProtoMember(9)] public double Wander2;
        [ProtoMember(10)] public double Age;
        [ProtoMember(11)] public double Speed;
        [ProtoMember(12)] public double ClimbRate;
        [ProtoMember(13)] public double TargetY;
        [ProtoMember(14)] public double Travel;
        [ProtoMember(15)] public double Length;
        /// <summary>Each bird's formation slot: x, y, z in turn.</summary>
        [ProtoMember(16)] public float[] Slots;
        /// <summary>Each bird's real entity, or 0 while it is only drawn.</summary>
        [ProtoMember(17)] public long[] Entities;
        [ProtoMember(18)] public bool Gone;
    }

    /// <summary>
    /// The passing flocks (server). Each flies across the sky (SkyFlight) and every client within SendRange draws it
    /// (FlockRenderer), until it comes within RealRange of a player: then its birds become real entities, each flying its
    /// own slot in the formation as the flock moves on (EntityBehaviorBird.Passage), and can be shot. A bird shot dead
    /// falls and stays, a carcass like any other; the rest scatter, climb a little, close up again and fly on, a bird
    /// short. Once every real bird is DrawnRange from all players they are removed and drawn again from their slots.
    /// Nothing of a passing flock is saved: a real bird loaded from a save after a restart is removed (Passage).
    /// </summary>
    public class SkyFlocks
    {
        /// <summary>Real birds within this of a player (horizontally, from the flock's centre); drawn again beyond
        /// DrawnRange from all of them (FlockRegistry.DepartRange: 3-4 px by then, and inside the flight simulation range).
        /// A player whose chunks don't reach that far gets real birds once the flock is over loaded ground.</summary>
        public const double RealRange = 300, DrawnRange = FlockRegistry.DepartRange;
        /// <summary>Clients within this of a flock are told about it. Flocks set off StartRange back along their way:
        /// where even a big bird is under 2 px across, so they come into sight as specks.</summary>
        public const double SendRange = 1700, StartRange = 1500;
        /// <summary>Shots and kills within this many seconds are one alarm; each alarm takes the flock up AlarmClimb
        /// blocks, at most MaxAlarms times.</summary>
        private const double AlarmMerge = 3, AlarmClimb = 10;
        private const int MaxAlarms = 3;

        public static SkyFlocks Instance { get; private set; }

        private readonly ICoreServerAPI sapi;
        private readonly IServerNetworkChannel channel;
        private readonly Dictionary<string, Species> species;
        private readonly List<Flock> flocks = new();
        private readonly Dictionary<long, (Flock f, Bird b)> byEntity = new();
        private readonly Dictionary<long, (double at, int count)> alarms = new();
        private readonly Random rand = new();
        private long nextId = 1;
        private double sendTimer;

        public SkyFlocks(ICoreServerAPI sapi, IServerNetworkChannel channel, Dictionary<string, Species> species)
        {
            this.sapi = sapi; this.channel = channel; this.species = species; Instance = this;
            sapi.Event.RegisterGameTickListener(Tick, 100);
        }

        public IReadOnlyList<Flock> All => flocks;
        public Flock Get(long id) => flocks.FirstOrDefault(f => f.Id == id);

        /// <summary>The flock and slot a real bird is flying for; false when it flies for none (any more).</summary>
        public bool Slot(long entityId, out Flock f, out Bird b)
        {
            f = null; b = null;
            if (!byEntity.TryGetValue(entityId, out var fb)) return false;
            f = fb.f; b = fb.b;
            return true;
        }

        /// <summary>
        /// A new passing flock of count birds over 'over' (a player's spot), heading that way (radians, 0 = south), at
        /// altitude blocks over the ground there. startRange: how far back along its way it sets off.
        /// </summary>
        public Flock Launch(Species sp, int count, Vec3d over, double heading, double altitude, double startRange = StartRange)
        {
            var ba = sapi.World.BlockAccessor;
            int ground = ba.GetRainMapHeightAt((int)over.X, (int)over.Z);
            double y = Math.Min(ground + altitude, ba.MapSizeY + 40);
            var f = SkyFlight.Create(nextId++, sp, count, over, heading, y, startRange, rand, flocks, ba);
            flocks.Add(f);
            Send(f);
            return f;
        }

        /// <summary>The spots players see the sky from: everyone in the game (whatever their mode: creative players see
        /// birds too), and the in-engine tests' stand-ins.</summary>
        private List<Vec3d> Viewers()
        {
            var list = new List<Vec3d>();
            foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
                if (p.Entity != null && p.ConnectionState == EnumClientState.Playing) list.Add(p.Entity.Pos.XYZ);
            foreach (var t in EntityBehaviorBird.TestPlayers) if (t != null && t.Alive) list.Add(t.Pos.XYZ);
            return list;
        }

        private static double Nearest(List<Vec3d> viewers, double x, double z)
        {
            double best = double.MaxValue;
            foreach (var v in viewers) best = Math.Min(best, (v.X - x) * (v.X - x) + (v.Z - z) * (v.Z - z));
            return Math.Sqrt(best);
        }

        private void Tick(float dt)
        {
            if (flocks.Count == 0) return;
            var ba = sapi.World.BlockAccessor;
            var viewers = Viewers();
            bool sendAll = (sendTimer -= dt) <= 0;
            if (sendAll) sendTimer = 1;
            for (int i = flocks.Count - 1; i >= 0; i--)
            {
                var f = flocks[i];
                SkyFlight.Move(f, dt, ba, flocks);
                bool changed = Members(f);
                bool real = f.Birds.Any(b => b.EntityId != 0);
                if (f.Birds.Count == 0 || (!real && f.Travel > f.Length)) { Remove(i); continue; }
                var c = f.Centre;
                if (!real && Nearest(viewers, c.X, c.Z) < RealRange) changed |= MakeReal(f);
                else if (real && f.Birds.All(b => b.EntityId == 0 || FarFromAll(viewers, b))) { MakeDrawn(f); changed = true; }
                if (changed || sendAll) Send(f);
            }
        }

        /// <summary>Real birds that died or vanished since the last tick. A dead one is a kill: it leaves the flock and the
        /// rest are alarmed. One no longer loaded (its chunk unloaded, say) goes back to being drawn. True if any.</summary>
        private bool Members(Flock f)
        {
            bool changed = false;
            for (int j = f.Birds.Count - 1; j >= 0; j--)
            {
                var b = f.Birds[j];
                if (b.EntityId == 0) continue;
                var e = sapi.World.GetEntityById(b.EntityId);
                if (e != null && e.Alive) continue;
                byEntity.Remove(b.EntityId);
                changed = true;
                if (e != null && e.State != EnumEntityState.Despawned && !e.Alive)
                {
                    f.Birds.RemoveAt(j);
                    sapi.Logger.Notification("[Flocks] a bird of passing flock #{0} ({1}) was killed; {2} fly on", f.Id, f.Species.Code, f.Birds.Count);
                    Alarm(f);
                }
                else b.EntityId = 0;
            }
            return changed;
        }

        private bool FarFromAll(List<Vec3d> viewers, Bird b)
        {
            var e = sapi.World.GetEntityById(b.EntityId);
            return e == null || Nearest(viewers, e.Pos.X, e.Pos.Z) > DrawnRange;
        }

        /// <summary>Real birds for every slot, where each is drawn now, already flying at the flock's speed. Only where all
        /// of them are over loaded chunks, and only for species with a bird to be (the crane has none) that are on.</summary>
        private bool MakeReal(Flock f)
        {
            var sp = f.Species;
            if (!sp.Lands || !ResidentGeese.Config.SpeciesEnabled(sp)) return false;
            var type = sapi.World.GetEntityType(sp.LandedEntityCode);
            if (type == null) return false;
            var ba = sapi.World.BlockAccessor;
            var spots = f.Birds.Select(b => f.SlotInWorld(b.Offset)).ToList();
            foreach (var p in spots)
                if (ba.GetChunkAtBlockPos((int)p.X, GameMath.Clamp((int)p.Y, 0, ba.MapSizeY - 1), (int)p.Z) == null) return false;
            double dx = Math.Sin(f.Heading), dz = Math.Cos(f.Heading);
            for (int i = 0; i < f.Birds.Count; i++)
            {
                var e = sapi.World.ClassRegistry.CreateEntity(type);
                var t = new TreeAttribute();
                t.SetLong("flock", f.Id);
                e.Attributes[EntityBehaviorBird.PassageKey] = t;
                e.Pos.SetPos(spots[i]);
                e.Pos.Yaw = (float)f.Heading;
                e.Pos.Motion.Set(dx * f.Speed / 60, f.ClimbRate / 60, dz * f.Speed / 60);
                e.SimulationRange = EntityBehaviorBird.FlightRange;
                sapi.World.SpawnEntity(e);
                f.Birds[i].EntityId = e.EntityId;
                byEntity[e.EntityId] = (f, f.Birds[i]);
            }
            return true;
        }

        /// <summary>Every real bird of the flock removed: it is drawn again, each bird at its slot.</summary>
        private void MakeDrawn(Flock f)
        {
            foreach (var b in f.Birds)
            {
                if (b.EntityId == 0) continue;
                byEntity.Remove(b.EntityId);
                var e = sapi.World.GetEntityById(b.EntityId);
                b.EntityId = 0;
                if (e != null && e.Alive) e.Die(EnumDespawnReason.Expire);
            }
        }

        private void Remove(int i)
        {
            var f = flocks[i];
            MakeDrawn(f);
            flocks.RemoveAt(i);
            alarms.Remove(f.Id);
            foreach (var plr in Receivers(f)) channel.SendPacket(new SkyFlockPacket { Id = f.Id, Gone = true }, plr);
        }

        /// <summary>
        /// Shot at, a bird hit or killed: the real birds scatter for a few seconds (EntityBehaviorBird.Scatter) and close
        /// up again, and the flock climbs AlarmClimb blocks (up to MaxAlarms times), keeping its way: the birds burst out
        /// ahead and to the sides rather than turn back, so a hunter may get a second chance.
        /// </summary>
        public void Alarm(Flock f)
        {
            double now = sapi.World.ElapsedMilliseconds / 1000.0;
            alarms.TryGetValue(f.Id, out var a);
            if (a.at > 0 && now - a.at < AlarmMerge) return;
            alarms[f.Id] = (now, a.count + 1);
            if (a.count < MaxAlarms) f.CruiseY += AlarmClimb;
            foreach (var b in f.Birds)
            {
                var e = b.EntityId == 0 ? null : sapi.World.GetEntityById(b.EntityId);
                e?.GetBehavior<EntityBehaviorBird>()?.Scatter(f.Heading);
            }
        }

        /// <summary>The players within SendRange of the flock.</summary>
        private IEnumerable<IServerPlayer> Receivers(Flock f)
        {
            var c = f.Centre;
            foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p.Entity == null || p.ConnectionState != EnumClientState.Playing) continue;
                double dx = p.Entity.Pos.X - c.X, dz = p.Entity.Pos.Z - c.Z;
                if (dx * dx + dz * dz < SendRange * SendRange) yield return p;
            }
        }

        private void Send(Flock f)
        {
            var pkt = Packet(f);
            foreach (var plr in Receivers(f)) channel.SendPacket(pkt, plr);
        }

        /// <summary>Tell this player about the flock now (before a watch starts, say).</summary>
        public void SendTo(Flock f, IServerPlayer plr) => channel.SendPacket(Packet(f), plr);

        private static SkyFlockPacket Packet(Flock f)
        {
            var slots = new float[f.Birds.Count * 3];
            var ids = new long[f.Birds.Count];
            for (int i = 0; i < f.Birds.Count; i++)
            {
                var o = f.Birds[i].Offset;
                slots[i * 3] = (float)o.X; slots[i * 3 + 1] = (float)o.Y; slots[i * 3 + 2] = (float)o.Z;
                ids[i] = f.Birds[i].EntityId;
            }
            return new SkyFlockPacket
            {
                Id = f.Id, Species = f.Species.Code, Seed = f.Seed, X = f.Pos.X, Y = f.Pos.Y, Z = f.Pos.Z,
                BaseHeading = f.BaseHeading, Wander1 = f.Wander1, Wander2 = f.Wander2, Age = f.Age, Speed = f.Speed,
                ClimbRate = f.ClimbRate, TargetY = f.TargetY, Travel = f.Travel, Length = f.Length, Slots = slots, Entities = ids,
            };
        }

        /// <summary>A flock of this species within range of pos (another player's, say), or null.</summary>
        public Flock Near(string code, Vec3d pos, double range)
            => flocks.FirstOrDefault(f => f.Species.Code == code && f.Pos.HorizontalSquareDistanceTo(pos.X, pos.Z) < range * range);

        /// <summary>For /flocks schedule: the flocks up now near the player, drawn or real.</summary>
        public List<string> Describe(Vec3d pos)
        {
            var lines = new List<string>();
            foreach (var f in flocks)
            {
                var c = f.Centre;
                double d = Math.Sqrt(c.HorizontalSquareDistanceTo(pos.X, pos.Z));
                if (d > SendRange) continue;
                int real = f.Birds.Count(b => b.EntityId != 0);
                lines.Add($"#{f.Id} {f.Species.Code} x{f.Birds.Count}, {d:0} blocks away, {c.Y - pos.Y:0} up: " +
                          (real > 0 ? $"real birds ({real})" : "drawn"));
            }
            return lines;
        }
    }
}
