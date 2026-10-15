using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>
    /// Server -> clients: a flock is coming in to land. The path is fixed by these numbers alone, so every client
    /// and the server agree where each bird is at any moment: a straight line along Heading that ends at the
    /// hand-off point, flown at Speed; level at CruiseY until DescentStart blocks along, then down to HandoffY.
    /// Offsets are the real geese's formation (entity scale); clients draw it wider and close it up before the swap.
    /// </summary>
    [ProtoContract]
    public class LandingFlockPacket
    {
        [ProtoMember(1)] public string Species;
        [ProtoMember(2)] public double HandoffX;
        [ProtoMember(3)] public double HandoffZ;
        [ProtoMember(4)] public double Heading;
        [ProtoMember(5)] public double Speed;
        [ProtoMember(6)] public double EntryDist;
        [ProtoMember(7)] public double CruiseY;
        [ProtoMember(8)] public double DescentStart;
        [ProtoMember(9)] public double HandoffY;
        /// <summary>Seconds from sending until the birds reach the hand-off point (the path's end).</summary>
        [ProtoMember(10)] public double SecondsToHandoff;
        [ProtoMember(11)] public double[] Along;
        [ProtoMember(12)] public double[] Up;
        [ProtoMember(13)] public double[] Across;
    }

    /// <summary>
    /// Landing flocks (server). On spring migration days each online player gets up to one landing a day (scaled by
    /// the day's migration strength, at a goose flying hour). A landing needs open water 40-160 blocks from the
    /// player with no geese on it yet, in the resident season. The flock is announced to every client within 1600
    /// blocks, flies in from ~800 blocks out as a drawn flock, and 60 blocks short of the lake the drawn birds are
    /// swapped for real geese, already gliding in to their spots (Waterfowl takes it from there).
    /// The flock is the one away longest, or a replacement for one that was killed off, or a new one (FlockRegistry).
    /// Autumn has no landings: geese out of their resident season would leave again at once.
    /// </summary>
    public class LandingFlocks
    {
        public const double EntryDist = 800, HandoffDist = 60, Speed = 13, MinFromPlayer = 40;
        /// <summary>Descent from cruise to the hand-off height: blocks forward per block down.</summary>
        public const double DescentRatio = 6;
        /// <summary>Formation of the real geese at the swap (blocks): back per rank, out per rank.</summary>
        public const double TightBack = 1.0, TightOut = 1.3;

        private readonly ICoreServerAPI sapi;
        private readonly IServerNetworkChannel channel;
        private readonly Species goose;
        private readonly Random rand = new();
        private readonly Dictionary<string, (int day, List<double> hours)> plans = new();

        public LandingFlocks(ICoreServerAPI sapi, IServerNetworkChannel channel)
        {
            this.sapi = sapi; this.channel = channel;
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

        /// <summary>
        /// Starts a landing flock for this player now. force (the /flocks arrive command) skips the season check.
        /// </summary>
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
            double hx = centre.X - dx * HandoffDist, hz = centre.Z - dz * HandoffDist;

            var flock = FlockRegistry.Instance.TakeForLanding("goose", centre, rand);
            int n = Math.Max(1, flock.Count);

            // heights: hand-off a little above the water and clear of the ground just before the lake; cruise well up
            double handoffY = Math.Max(centre.Y + 16, MaxGround(ba, hx, hz, dx, dz, -40, 20) + 8);
            // The drawn flock has no collision, so the path itself must clear the terrain: cruise above everything
            // along it (loaded chunks; further out is too far to see), and steepen the descent from DescentRatio
            // wherever a hill short of the lake would otherwise poke through the line (8 blocks of clearance).
            double cruiseY = Math.Max(handoffY + 40, MaxGround(ba, p.X, p.Z, dx, dz, -20, 20) + 70);
            double ratio = DescentRatio;
            for (int b = 8; b <= EntryDist; b += 8)
            {
                double g = Ground(ba, hx - dx * b, hz - dz * b);
                if (g <= 0) continue;
                cruiseY = Math.Max(cruiseY, g + 30);
                if (g + 8 > handoffY) ratio = Math.Min(ratio, b / (g + 8 - handoffY));
            }
            cruiseY = Math.Min(cruiseY, ba.MapSizeY + 40);
            double descentStart = Math.Max(0, EntryDist - (cruiseY - handoffY) * Math.Max(ratio, 0.5));

            var pkt = new LandingFlockPacket
            {
                Species = "goose", HandoffX = hx, HandoffZ = hz, Heading = heading, Speed = Speed, EntryDist = EntryDist,
                CruiseY = cruiseY, DescentStart = descentStart, HandoffY = handoffY, SecondsToHandoff = EntryDist / Speed,
                Along = new double[n], Up = new double[n], Across = new double[n],
            };
            for (int i = 0; i < n; i++)
            {
                int rank = (i + 1) / 2, side = i == 0 ? 0 : (i % 2 == 0 ? 1 : -1);
                pkt.Along[i] = -rank * TightBack + (rand.NextDouble() - 0.5) * 0.3;
                pkt.Across[i] = side * rank * TightOut + (rand.NextDouble() - 0.5) * 0.3;
                pkt.Up[i] = rank * 0.1 + (rand.NextDouble() - 0.5) * 0.4;
            }
            var send = sapi.World.AllOnlinePlayers.Where(o => o.Entity != null && o.Entity.Pos.HorDistanceTo(centre) < FlockRegistry.BroadcastRange)
                .Select(o => (IServerPlayer)o).ToArray();
            channel.SendPacket(pkt, send);
            double tx = p.X, tz = p.Z;
            sapi.Event.RegisterCallback(_ => Handoff(pkt, flock, spots, type, tx, tz), (int)(pkt.SecondsToHandoff * 1000));
            msg = $"A flock of {n} greylag geese (flock #{flock.Id}) is coming in to land at {centre.X:0}, {centre.Y:0}, {centre.Z:0}, ~{pkt.SecondsToHandoff:0} s out.";
            sapi.Logger.Notification("[Flocks] landing: {0}", msg);
            return true;
        }

        /// <summary>The swap: the drawn flock has reached the hand-off point on every client; real geese appear in its
        /// place, each in flight toward its own spot on the water.</summary>
        private void Handoff(LandingFlockPacket pkt, FlockRecord flock, List<Vec3d> spots, EntityProperties type, double threatX, double threatZ)
        {
            var ba = sapi.World.BlockAccessor;
            if (ba.GetChunkAtBlockPos(new BlockPos((int)pkt.HandoffX, (int)pkt.HandoffY, (int)pkt.HandoffZ)) == null
                || ba.GetChunkAtBlockPos(spots[0].AsBlockPos) == null)
            {
                FlockRegistry.Instance.LandingFailed(flock); // nobody near enough any more: the flock stays away
                return;
            }
            double dx = Math.Sin(pkt.Heading), dz = Math.Cos(pkt.Heading);
            for (int i = 0; i < pkt.Along.Length; i++)
            {
                var e = sapi.World.ClassRegistry.CreateEntity(type);
                e.WatchedAttributes.SetLong(FlockRegistry.FlockIdKey, flock.Id);
                EntityBehaviorWaterfowl.PrepareLanding(e, spots[rand.Next(spots.Count)].Clone(), pkt.Heading, pkt.Speed, threatX, threatZ);
                // same frame as the sky renderer: X along the heading, Z across (perp = (cos, -sin))
                e.Pos.SetPos(pkt.HandoffX + dx * pkt.Along[i] + dz * pkt.Across[i], pkt.HandoffY + pkt.Up[i], pkt.HandoffZ + dz * pkt.Along[i] - dx * pkt.Across[i]);
                sapi.World.SpawnEntity(e);
            }
        }

        /// <summary>Highest ground or tree top along the heading from 'from' to 'to' blocks past (x, z), loaded chunks only.</summary>
        private static double MaxGround(IBlockAccessor ba, double x, double z, double dx, double dz, int from, int to)
        {
            double top = 0;
            for (int d = from; d <= to; d += 4) top = Math.Max(top, Ground(ba, x + dx * d, z + dz * d));
            return top;
        }

        /// <summary>Top of the ground or trees at (x, z); 0 where the chunk isn't loaded.</summary>
        private static double Ground(IBlockAccessor ba, double x, double z)
        {
            int h = ba.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z));
            return h > 0 ? h + 1 : 0;
        }
    }
}
