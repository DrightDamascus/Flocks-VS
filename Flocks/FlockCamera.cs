using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Flocks
{
    /// <summary>Both ways: server -> client starts (or stops) watching a flock; client -> server says the watch is over.</summary>
    [ProtoContract]
    public class FlockCamPacket
    {
        [ProtoMember(1)] public long FlockId;
        /// <summary>Where the flock is now: the camera goes there first, as birds further than the entity tracking range
        /// (128 blocks) from the player aren't sent to the client at all.</summary>
        [ProtoMember(2)] public double X;
        [ProtoMember(3)] public double Y;
        [ProtoMember(4)] public double Z;
        [ProtoMember(5)] public bool Stop;
        /// <summary>A whole journey (FlockJourney): no time limit, and the server, not the client, says when it's over.</summary>
        [ProtoMember(6)] public bool Journey;
    }

    /// <summary>
    /// The server's half of "watch" (/flocks depart|arrive ... watch): the player becomes a spectator and their client
    /// flies them behind the flock (FlockCameraClient), the way vanilla's cinematic camera moves the player itself. That
    /// keeps the flock inside the entity tracking range and its chunks loaded. Where the player was, their game mode and
    /// flight settings are kept in their player data until the watch ends, so a crash or a disconnect mid-watch still
    /// puts them back (on their next join). While watching, the player counts as being at that spot for the flock's
    /// departure (FlockRegistry.TryDepart) and the automatic landings.
    /// </summary>
    public class FlockCamera
    {
        public const string HomeKey = "flocks:camhome";
        public static FlockCamera Instance { get; private set; }
        private readonly ICoreServerAPI sapi;
        private readonly IServerNetworkChannel channel;

        public FlockCamera(ICoreServerAPI sapi, IServerNetworkChannel channel)
        {
            this.sapi = sapi; this.channel = channel; Instance = this;
            channel.SetMessageHandler<FlockCamPacket>((plr, pkt) => { if (pkt.Stop) Stop(plr); });
            sapi.Event.PlayerNowPlaying += plr => Stop(plr);   // a watch cut off by a disconnect or a crash
        }

        private class Home { public Vec3d Pos; public EnumGameMode Mode; public bool FreeMove, NoClip; }

        private static Home ReadHome(IServerPlayer plr)
        {
            if (plr?.ServerData?.CustomPlayerData == null || !plr.ServerData.CustomPlayerData.TryGetValue(HomeKey, out string s)) return null;
            var v = s.Split(',');
            if (v.Length < 6) return null;
            double D(int i) => double.Parse(v[i], CultureInfo.InvariantCulture);
            return new Home { Pos = new Vec3d(D(0), D(1), D(2)), Mode = (EnumGameMode)int.Parse(v[3]), FreeMove = v[4] == "1", NoClip = v[5] == "1" };
        }

        public bool Watching(IServerPlayer plr) => ReadHome(plr) != null;

        /// <summary>Where the player counts as being: their spot before the watch, else where they are.</summary>
        public static Vec3d Where(IPlayer plr)
            => (plr is IServerPlayer sp ? ReadHome(sp)?.Pos : null) ?? plr.Entity?.Pos.XYZ;

        /// <summary>Starts watching the flock (a watch already running just moves to it).</summary>
        public string Start(IServerPlayer plr, long flockId, Vec3d at)
        {
            if (flockId == 0) return Lang.Get("flocks:watch-no-flock");
            string err = Begin(plr);
            if (err != null) return err;
            Follow(plr, flockId, at);
            return Lang.Get("flocks:watch-started");
        }

        /// <summary>Tells the client which flock to follow (the player is a spectator already: Begin).</summary>
        public void Follow(IServerPlayer plr, long flockId, Vec3d at, bool journey = false)
            => channel.SendPacket(new FlockCamPacket { FlockId = flockId, X = at.X, Y = at.Y, Z = at.Z, Journey = journey }, plr);

        /// <summary>Makes the player a spectator for a watch, keeping where and how they were; null when done, else why
        /// not. The client is told what to follow separately (Start, or a passing flock's /flocks send).</summary>
        public string Begin(IServerPlayer plr)
        {
            if (plr.Entity.MountedOn != null) return Lang.Get("flocks:watch-mounted");
            if (!Watching(plr))
            {
                var p = plr.Entity.Pos;
                var wd = plr.WorldData;
                plr.ServerData.CustomPlayerData[HomeKey] = string.Join(",", p.X.ToString("R", CultureInfo.InvariantCulture),
                    p.Y.ToString("R", CultureInfo.InvariantCulture), p.Z.ToString("R", CultureInfo.InvariantCulture),
                    ((int)wd.CurrentGameMode).ToString(), wd.FreeMove ? "1" : "0", wd.NoClip ? "1" : "0");
                SetMode(plr, EnumGameMode.Spectator, true, true);
            }
            return null;
        }

        /// <summary>Ends the watch: back to where they were, as they were. False if they weren't watching.</summary>
        public bool Stop(IServerPlayer plr)
        {
            FlockJourney.Instance?.Cancel(plr);   // a journey under way ends with the watch
            var home = ReadHome(plr);
            if (home == null || plr.Entity == null) return false;
            plr.ServerData.CustomPlayerData.Remove(HomeKey);
            SetMode(plr, home.Mode, home.FreeMove, home.NoClip);
            plr.Entity.TeleportToDouble(home.Pos.X, home.Pos.Y, home.Pos.Z);
            if (plr.ConnectionState == EnumClientState.Playing) channel.SendPacket(new FlockCamPacket { Stop = true }, plr);
            return true;
        }

        /// <summary>The game mode through vanilla's own /gamemode (so every system hears of the switch), then the exact
        /// flight settings.</summary>
        private void SetMode(IServerPlayer plr, EnumGameMode mode, bool freeMove, bool noClip)
        {
            if (plr.WorldData.CurrentGameMode != mode)
            {
                var args = new TextCommandCallingArgs
                {
                    Caller = new Caller { Type = EnumCallerType.Console, CallerRole = "admin", CallerPrivileges = new[] { "*" }, FromChatGroupId = GlobalConstants.ConsoleGroup }
                };
                sapi.ChatCommands.ExecuteUnparsed($"/gamemode {plr.PlayerName} {(int)mode}", args);
            }
            plr.WorldData.FreeMove = freeMove; plr.WorldData.NoClip = noClip;
            plr.BroadcastPlayerData();
        }
    }

    /// <summary>
    /// The client's half of a watch: each frame the player (a spectator by now) is put behind and above the flock's
    /// centre, a little to one side, looking at it; eased, so it glides along rather than jumps. The flock is either its
    /// loaded birds with its flock id (one that lands), or a passing flock the renderer draws. The watch ends when the
    /// flock has gone (removed far out on departure, or flown out of the sky), when it has been down on the water a few
    /// seconds (an arrival), after 3 minutes, or on Sneak; the server then puts the player back.
    /// </summary>
    public class FlockCameraClient : IRenderer
    {
        /// <summary>Framing (blocks behind, above and to the side of the flock's centre); at least Clearance over the
        /// ground and Margin from any block.</summary>
        private const double Back = 11, Up = 4.5, Side = 3.5, Clearance = 2.5, Margin = 0.6;
        private const double MaxSeconds = 180, GoneSeconds = 3, LandedSeconds = 5, NeverSeenSeconds = 15;
        private readonly ICoreClientAPI capi;
        private readonly IClientNetworkChannel channel;
        private readonly FlockRenderer renderer;
        private Flock sky;   // a passing flock being watched, or null
        private long flockId;
        private bool active, seen, journey;
        private Vec3d cam, centre, lastCentre;
        private double dirX = 0, dirZ = 1, time, missing, landed, rescan;
        private readonly List<Entity> members = new();

        public double RenderOrder => 0;
        public int RenderRange => 0;

        public FlockCameraClient(ICoreClientAPI capi, IClientNetworkChannel channel, FlockRenderer renderer)
        {
            this.capi = capi; this.channel = channel; this.renderer = renderer;
            channel.SetMessageHandler<FlockCamPacket>(OnPacket);
            capi.Event.RegisterRenderer(this, EnumRenderStage.Before, "flockscam");
        }

        private void OnPacket(FlockCamPacket pkt)
        {
            if (pkt.Stop) { active = false; sky = null; return; }
            Begin(pkt.FlockId, null, new Vec3d(pkt.X, pkt.Y, pkt.Z));
            journey = pkt.Journey;
        }

        /// <summary>Follow a passing flock (/flocks send ... watch); the server has already made the player a spectator.</summary>
        public void WatchSky(Flock f) => Begin(0, f, SkyCentre(f));

        /// <summary>A watch the server began that can't go ahead (no flock was sent): put the player back.</summary>
        public void Cancel() { active = true; Finish(); }

        private void Begin(long id, Flock f, Vec3d at)
        {
            flockId = id; sky = f; active = true; seen = false; journey = false;
            time = missing = landed = rescan = 0;
            centre = at; lastCentre = null; cam = null;
            var me = capi.World.Player.Entity.Pos;
            double dx = at.X - me.X, dz = at.Z - me.Z, len = Math.Sqrt(dx * dx + dz * dz);
            if (f != null) { dirX = Math.Sin(f.Heading); dirZ = Math.Cos(f.Heading); }
            else if (len > 1) { dirX = dx / len; dirZ = dz / len; }   // until it moves: looking out from where the player was
        }

        /// <summary>The middle of a passing flock: its leader, back half the formation's length.</summary>
        private static Vec3d SkyCentre(Flock f)
            => new Vec3d(f.Pos.X - Math.Sin(f.Heading) * f.Tail * 0.5, f.Pos.Y + (f.Rise - f.Drop) * 0.5, f.Pos.Z - Math.Cos(f.Heading) * f.Tail * 0.5);

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!active) return;
            var plr = capi.World.Player;
            var e = plr?.Entity;
            if (e == null) return;
            if (plr.WorldData.CurrentGameMode != EnumGameMode.Spectator) { if ((time += dt) > 10) Finish(); return; }   // not switched yet
            time += dt;
            if (e.Controls.Sneak || (time > MaxSeconds && !journey)) { Finish(); return; }

            if (sky != null)
            {
                if (!renderer.Has(sky)) { Finish(); return; }
                members.Clear();
            }
            else if ((rescan -= dt) <= 0)
            {
                rescan = 0.25;
                members.Clear();
                foreach (var b in capi.World.LoadedEntities.Values)
                    if (b.Alive && b.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0) == flockId) members.Add(b);
            }
            members.RemoveAll(b => !b.Alive);
            if (sky == null && members.Count == 0)
            {
                missing += dt;
                if (!journey && (seen ? missing > GoneSeconds : time > NeverSeenSeconds)) { Finish(); return; }   // a journey: the server decides
            }
            else
            {
                seen = true; missing = 0;
                var c = sky != null ? SkyCentre(sky)
                    : new Vec3d(members.Average(b => b.Pos.X), members.Average(b => b.Pos.Y), members.Average(b => b.Pos.Z));
                // the way it's going, from its centre's movement, eased
                if (lastCentre != null && dt > 0)
                {
                    double vx = (c.X - lastCentre.X) / dt, vz = (c.Z - lastCentre.Z) / dt, v = Math.Sqrt(vx * vx + vz * vz);
                    if (v > 2 && v < 80)
                    {
                        double k = 1 - Math.Exp(-dt * 0.8);
                        dirX += (vx / v - dirX) * k; dirZ += (vz / v - dirZ) * k;
                        double l = Math.Sqrt(dirX * dirX + dirZ * dirZ);
                        if (l > 1e-3) { dirX /= l; dirZ /= l; }
                    }
                }
                lastCentre = c;
                centre = cam == null ? c : centre + (c - centre) * (1 - Math.Exp(-dt * 4));
                bool down = sky == null && members.All(b => !b.WatchedAttributes.HasAttribute(EntityBehaviorWaterfowl.FlightKey));
                landed = down ? landed + dt : 0;
                if (landed > LandedSeconds && !journey) { Finish(); return; }
            }

            // behind it, above, a little to the side (perpendicular (dirZ, -dirX)), eased; kept above the ground
            var want = new Vec3d(centre.X - dirX * Back + dirZ * Side, centre.Y + Up, centre.Z - dirZ * Back - dirX * Side);
            want.Y = Math.Max(want.Y, GroundAt(want.X, want.Z) + Clearance);
            cam = cam == null ? want : cam + (want - cam) * (1 - Math.Exp(-dt * 1.5));
            cam.Y = Math.Max(cam.Y, GroundAt(cam.X, cam.Z) + Clearance);
            // never inside a block: a hill or a tree between the flock and the camera brings it in, in front of that
            var eye = InTheClear(centre, cam);
            e.Pos.SetPos(eye.X, eye.Y - e.LocalEyePos.Y, eye.Z);   // the view is from the eyes, not the feet
            e.Pos.Motion.Set(0, 0, 0);
            // look at the centre: the view is (-cos p sin y, sin p, -cos p cos y) (EntityPos.GetViewVector)
            double lx = centre.X - eye.X, ly = centre.Y - eye.Y, lz = centre.Z - eye.Z;
            float yaw = (float)Math.Atan2(-lx, -lz), pitch = (float)Math.Atan2(ly, Math.Sqrt(lx * lx + lz * lz));
            e.Pos.Yaw = yaw; e.Pos.Pitch = pitch;
            capi.Input.MouseYaw = yaw; capi.Input.MousePitch = pitch;
        }

        /// <summary>The top of the ground, water or tree canopy at a column (the rain map), as a height to stay over.</summary>
        private double GroundAt(double x, double z)
            => capi.World.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z)) + 1;

        private readonly BlockPos probe = new BlockPos(0, 0, 0);

        /// <summary>A block the view can't pass: anything with a collision box (leaves too; not water or grass).</summary>
        private bool Solid(double x, double y, double z)
        {
            probe.Set((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
            var cb = capi.World.BlockAccessor.GetBlock(probe)?.CollisionBoxes;
            return cb != null && cb.Length > 0;
        }

        /// <summary>The camera spot pulled in along the line from the flock until nothing solid is in between (or around
        /// it), like a third-person camera against a wall.</summary>
        private Vec3d InTheClear(Vec3d from, Vec3d to)
        {
            if (Solid(from.X, from.Y, from.Z)) from = from.AddCopy(0, 1.5, 0);   // a centre down among the reeds or a tree
            double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z, len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 0.01) return to;
            dx /= len; dy /= len; dz /= len;
            double free = 0;
            for (double t = 0; t <= len + Margin; t += 0.2)
            {
                double x = from.X + dx * t, y = from.Y + dy * t, z = from.Z + dz * t;
                if (Solid(x, y, z) || Solid(x, y - Margin, z) || Solid(x, y + Margin, z)) break;
                free = t;
            }
            double d = Math.Min(len, Math.Max(0, free - Margin));
            return new Vec3d(from.X + dx * d, from.Y + dy * d, from.Z + dz * d);
        }

        private void Finish()
        {
            if (!active) return;
            active = false; sky = null;
            channel.SendPacket(new FlockCamPacket { Stop = true });
        }

        public void Dispose() { }
    }
}
