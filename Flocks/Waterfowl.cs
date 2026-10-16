using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// Server-side behaviour for landed waterfowl (flocks:greylag). Keeps the bird floating at the water surface,
    /// drifts it slowly and paddles away from players that come close. When a player gets very close or it is shot
    /// at, the group takes off in a hurry (a short run across the water), flies to another part of the lake, or to
    /// another lake if there's no safe water left on this one, and glides in to land there.
    /// A group that departs for good (the resident season ends, a /flocks land test goose's time is up, or there is
    /// nowhere to land nearby) flies off as real geese and is removed once 360 blocks from every player
    /// (FlockRegistry.TryDepart): it rejoins the migration rather than vanishing in view.
    /// In flight a goose's simulation range is FlightRange instead of the usual 128 blocks, so it keeps flying, and
    /// stays visible to players out to their view distance, far beyond where resting animals are simulated.
    /// Flight state is kept in WatchedAttributes, so a goose that flies out of the loaded area carries on later.
    /// Vanilla's land-creature AI tasks are not used: their pathfinder avoids water and getoutofwater would drag
    /// the birds ashore, so movement is set here directly and the vanilla physics does the rest.
    /// Damage: arrows and other ranged projectiles hurt it normally. Spears (thrown or held) and melee hits do
    /// nothing except scare it off - the goose dodges, like a fish slipping a spear.
    /// </summary>
    public class EntityBehaviorWaterfowl : EntityBehavior
    {
        public const string Code = "flocks.waterfowl";
        private const string SpawnKey = "flocksSpawnHours", FlightKey = "flocksFlight";
        /// <summary>Set by /flocks land on its test geese: they leave after lifeHours. Every other goose is a resident
        /// (spawned by vanilla's spawner) and stays until its resident season ends.</summary>
        public const string TestKey = "flocksTestGoose";

        public bool Resident => !entity.WatchedAttributes.HasAttribute(TestKey);
        private float seasonCheck = 5;
        // far-from-shore check: every few seconds; while too far out, drift is pulled toward the nearest land seen
        private float shoreCheck = 2;
        private double shoreHeading = double.NaN;

        private float fleeRange = 14, takeoffRange = 7, lifeHours = 6, sink = 0.15f;
        private double driftSpeed = 0.004, paddleSpeed = 0.025;

        // ---- flight, in blocks and seconds (Motion is per 1/60 s, hence the /60 where it is set)
        private enum Phase { Water, Run, Fly, Glide, Splash }
        /// <summary>The run across the water before the feet leave it, and its top speed.</summary>
        private const double RunSeconds = 0.55, RunSpeed = 7;
        private double cruiseMin = 11, cruiseMax = 13.5;
        private const double Accel = 7, TurnRate = 1.4, ClimbMax = 4.5, SinkMax = 3, GroundClear = 6;
        /// <summary>Gliding in: blocks forward per block down, and the speed at touchdown.</summary>
        private const double GlideRatio = 3.5, LandSpeed = 6, SplashSeconds = 0.9;
        /// <summary>Species code of this bird's flock (FlockRegistry, Species.cs).</summary>
        private string species = "goose";
        /// <summary>Simulation range while flying: the server ticks an entity, and sends it to players, only within this
        /// of some player (vanilla default 128). Landing flocks arrive from up to ~320 blocks out and departing ones are
        /// removed at 360 (FlockRegistry.DepartRange), so it must cover both.</summary>
        public const int FlightRange = 400;
        /// <summary>Terrain avoidance: feet clear ground and trees by Clearance; obstacles are seen LookSeconds ahead
        /// across the wingspan (HalfSpan either side). A wall too steep to climb at speed makes the goose pull up hard
        /// (SteepClimb) and shed speed, down to a near-vertical hover-climb at MinPullSpeed.</summary>
        private const double Clearance = 1.5, LookSeconds = 3, HalfSpan = 0.8, SteepClimb = 7, MinPullSpeed = 1.2, PullDecel = 20;
        /// <summary>A landing approach that can't get in for this long gives up on that spot and picks another.</summary>
        private const float ApproachGiveUp = 40;

        private Phase phase = Phase.Water;
        private bool restored, runStarted, hardFlap;
        private float phaseTime, searchTimer;
        private Vec3d target;              // landing point at the water surface; null while searching or departing
        private bool departing;
        private double flyHeading, speed, cruise, travel, threatX, threatZ;

        private Entity scarer;
        private float nextCheck;
        private double heading;
        private string anim;
        private readonly Random rand = new();

        public EntityBehaviorWaterfowl(Entity entity) : base(entity) { }
        public override string PropertyName() => Code;

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            fleeRange = attributes["fleeRange"].AsFloat(fleeRange);
            takeoffRange = attributes["takeoffRange"].AsFloat(takeoffRange);
            lifeHours = attributes["lifeHours"].AsFloat(lifeHours);
            sink = attributes["sink"].AsFloat(sink);
            cruiseMin = attributes["cruiseSpeedMin"].AsDouble(cruiseMin);
            cruiseMax = attributes["cruiseSpeedMax"].AsDouble(cruiseMax);
            species = attributes["species"].AsString(species);
            heading = rand.NextDouble() * Math.PI * 2;
        }

        public override void OnGameTick(float dt)
        {
            if (entity.World.Side != EnumAppSide.Server) return;
            var pos = entity.Pos;
            if (!entity.Alive) { pos.Roll = 0; if (entity.Swimming || entity.FeetInLiquid) pos.Motion.Y = SurfaceMotion(); return; }
            if (!restored) { restored = true; Restore(); FlockRegistry.Instance?.Adopt(entity, species); }
            entity.SimulationRange = phase == Phase.Water ? GlobalConstants.DefaultSimulationRange : FlightRange;
            if (phase != Phase.Water) { TickFlight(dt); return; }

            var attrs = entity.WatchedAttributes;
            double now = entity.World.Calendar.TotalHours;
            if (!attrs.HasAttribute(SpawnKey)) attrs.SetDouble(SpawnKey, now);

            if (!Resident && now - attrs.GetDouble(SpawnKey) > lifeHours) { TakeoffGroup(rand.NextDouble() * Math.PI * 2, null, true); return; }
            if (Resident && (seasonCheck -= dt) <= 0)
            {
                seasonCheck = 30;
                var bp = pos.AsBlockPos;
                double lat = entity.World.Calendar.OnGetLatitude(bp.Z);
                if (!ResidentGeese.InSeason(entity.World, bp))
                {
                    // autumn: the whole group leaves toward the equator (0 = south, pi = north)
                    TakeoffGroup((lat >= 0 ? 0 : Math.PI) + (rand.NextDouble() - 0.5) * 0.5, null, true);
                    return;
                }
            }

            // the nearest player is looked up 4 times a second and kept in between, so fleeing is continuous
            if ((nextCheck -= dt) <= 0) { nextCheck = 0.25f; scarer = NearestPlayer(fleeRange); }
            var player = scarer;
            double speedNow = driftSpeed;
            if (player != null)
            {
                double dx = pos.X - player.Pos.X, dz = pos.Z - player.Pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
                double away = Math.Atan2(dx, dz);
                if (d < takeoffRange) { TakeoffGroup(away, player, false); return; }
                if (d < fleeRange) { heading = away; speedNow = paddleSpeed; }
            }
            else
            {
                heading += (rand.NextDouble() - 0.5) * 0.15; // slow wandering drift
                if (!double.IsNaN(shoreHeading))            // too far out: ease back toward land
                    heading += GameMath.AngleRadDistance((float)heading, (float)shoreHeading) * 0.05;
            }
            if ((shoreCheck -= dt) <= 0) { shoreCheck = 3; CheckShore(); }

            bool inWater = entity.Swimming || entity.FeetInLiquid;
            // A bank flush with the water is only ~0.3 blocks above a floating goose, well within stepHeight, so it
            // would never collide and could paddle ashore and wander inland. Look ahead and turn back at the edge
            // instead (a goose already ashore just heads back toward water).
            if (inWater && !WaterAt(heading, 0.8)) heading = TowardWater(heading);
            else if (!inWater) heading = TowardWater(heading);
            pos.Motion.X = Math.Sin(heading) * speedNow;
            pos.Motion.Z = Math.Cos(heading) * speedNow;
            if (inWater) pos.Motion.Y = SurfaceMotion();
            if (entity.CollidedHorizontally) heading += Math.PI * (0.5 + rand.NextDouble()); // turn away from a wall
            pos.Yaw = (float)heading;
            SetAnim(inWater ? "swim" : (speedNow > driftSpeed ? "walk" : "idle-stand"));
        }

        // ------------------------------------------------------------------------------------------------ flight

        /// <summary>
        /// This bird and the rest of its group on the lake take off together, each a moment apart. Unless they are
        /// departing for good, one search picks where the group comes down, and each bird gets its own spot there.
        /// threat: what scared them (null = nothing in particular; the search then just avoids their own spot).
        /// </summary>
        private void TakeoffGroup(double away, Entity threat, bool depart)
        {
            var pos = entity.Pos;
            double tx = threat?.Pos.X ?? pos.X, tz = threat?.Pos.Z ?? pos.Z;
            List<Vec3d> spots = depart ? null : LakeSearch.FindSpots(entity.World.BlockAccessor, pos.XYZ, tx, tz, Landing.MinPatch, rand);
            if (spots == null || spots.Count == 0) depart = true; // nowhere to land nearby: they rejoin the migration
            Takeoff(away, Pick(spots), depart, tx, tz, 0);
            foreach (var e in entity.World.GetEntitiesAround(pos.XYZ, 16, 8, e => e.Code == entity.Code && e.Alive && e != entity))
                e.GetBehavior<EntityBehaviorWaterfowl>()?.Takeoff(away + (rand.NextDouble() - 0.5) * 0.6, Pick(spots), depart, tx, tz,
                    (float)(0.08 + rand.NextDouble() * 0.5));
        }

        private Vec3d Pick(List<Vec3d> spots) => spots == null || spots.Count == 0 ? null : spots[rand.Next(spots.Count)].Clone();

        public void Takeoff(double towards, Vec3d spot, bool depart, double tx, double tz, float delay)
        {
            if (phase != Phase.Water || !entity.Alive) return;
            phase = Phase.Run; phaseTime = -delay; runStarted = false;
            flyHeading = towards; speed = 0; travel = 0; searchTimer = 3;
            cruise = cruiseMin + rand.NextDouble() * (cruiseMax - cruiseMin);
            target = spot; if (target != null) target.Y -= sink;
            departing = depart; threatX = tx; threatZ = tz;
            Persist();
        }

        private void TickFlight(float dt)
        {
            var pos = entity.Pos;
            phaseTime += dt;
            double vy = 0;
            bool onWater = false;
            switch (phase)
            {
                case Phase.Run:
                    if (phaseTime < 0) { pos.Motion.Set(0, SurfaceMotion(), 0); return; } // the alarm hasn't reached this one yet
                    if (!runStarted)
                    {
                        runStarted = true;
                        flyHeading = ClearestHeading(flyHeading);
                        var w = entity.World;
                        w.PlaySoundAt(new AssetLocation("game", "environment/smallsplash"), entity, null, true, 24, 0.5f);
                        if (rand.NextDouble() < 0.6) w.PlaySoundAt(new AssetLocation("flocks", "wings/flap" + (1 + rand.Next(2))), entity, null, true, 32, 0.9f);
                        if (rand.NextDouble() < 0.4) w.PlaySoundAt(new AssetLocation("flocks", "goose/flight" + (1 + rand.Next(3))), entity, null, true, 48, 0.7f);
                    }
                    SetAnim("takeoff");
                    // pattering along the surface, wings thrashing, then the feet leave the water
                    speed = Math.Min(RunSpeed, speed + RunSpeed / RunSeconds * dt);
                    onWater = phaseTime < RunSeconds * 0.7;
                    vy = onWater ? 0 : 2.5;
                    if (phaseTime >= RunSeconds) SetPhase(Phase.Fly);
                    break;

                case Phase.Fly:
                {
                    if (target != null && phaseTime > ApproachGiveUp) { target = null; searchTimer = 0; } // can't get in: try elsewhere
                    if (target == null && !departing && (searchTimer -= dt) <= 0)
                    {
                        searchTimer = 4;
                        target = Pick(LakeSearch.FindSpots(entity.World.BlockAccessor, pos.XYZ, threatX, threatZ, Landing.MinPatch, rand));
                        if (target != null) { target.Y -= sink; Persist(); }
                        else { departing = true; Persist(); } // nowhere to land: rejoin the migration
                    }
                    // Climb out straight for a moment, then turn toward the landing spot. A spot close by and well off
                    // the nose can sit inside the turning circle, where steering at it only orbits it: fly on until it
                    // is far enough to turn and line up (in a flight simulation every spot 14-110 blocks off, at any
                    // bearing, was reached in under 23 s with at most one go-round).
                    if (target != null && phaseTime > 0.8)
                    {
                        double want = Math.Atan2(target.X - pos.X, target.Z - pos.Z);
                        bool tooClose = HorDist(target) < 2.2 * speed / TurnRate && Math.Abs(GameMath.AngleRadDistance((float)flyHeading, (float)want)) > 1.2;
                        if (!tooClose) Steer(want, TurnRate, dt);
                    }
                    vy = ClimbRate(CruiseY());
                    double cap = cruise;
                    AvoidTerrain(ref vy, ref cap, dt);
                    speed = speed < cap ? Math.Min(cap, speed + Accel * dt) : Math.Max(cap, speed - PullDecel * dt);
                    // beat hard while climbing out or pulling up, cruise-flap otherwise (with some hysteresis)
                    if (phaseTime < 1.4 || vy > 3) hardFlap = true; else if (vy < 2) hardFlap = false;
                    SetAnim(hardFlap ? "takeoff" : "fly-flap");
                    if (target != null)
                    {
                        double d = HorDist(target), drop = pos.Y - target.Y;
                        double err = Math.Abs(GameMath.AngleRadDistance((float)flyHeading, (float)Math.Atan2(target.X - pos.X, target.Z - pos.Z)));
                        // glide in only along a clear line: nothing within Clearance of the straight path down
                        bool clear = NeededSlope(flyHeading, Math.Max(0, d - 2.5)) <= -drop / Math.Max(d, 1) + 0.05;
                        if (d <= drop * GlideRatio + 3 && err < 0.5 && phaseTime > 1.5 && clear) SetPhase(Phase.Glide);
                    }
                    else if (departing && phaseTime > 3)
                    {
                        // far enough from every player: the flock has left (FlockRegistry.TryDepart)
                        FlockRegistry.Instance?.TryDepart(entity);
                        if (!entity.Alive) return;
                    }
                    break;
                }

                case Phase.Glide:
                {
                    double d = HorDist(target);
                    SetAnim(d < 8 ? "land" : "fly-soar");
                    speed = Math.Max(LandSpeed, speed - 3 * dt);
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 1.3, dt);
                    // a straight line down to the spot at the current speed
                    vy = GameMath.Clamp(-(pos.Y - target.Y) / Math.Max(0.3, d / speed), -4, 0.5);
                    // something rose into the glide path (a turn brought a tree into line): go round and climb over it
                    if (NeededSlope(flyHeading, Math.Max(0, d - 2.5)) > vy / Math.Max(speed, 0.5) + 0.05) { SetPhase(Phase.Fly); break; }
                    if (entity.Swimming || entity.FeetInLiquid || pos.Y <= target.Y + 0.15)
                    {
                        entity.World.PlaySoundAt(new AssetLocation("game", "environment/mediumsplash"), entity, null, true, 24, 0.45f);
                        SetPhase(Phase.Splash);
                    }
                    else if ((d < 1.5 && pos.Y - target.Y > 1.5) || phaseTime > 15) SetPhase(Phase.Fly); // overshot: go round
                    break;
                }

                case Phase.Splash:
                    // skid to a stop on the water, wings still up, then fold them and float
                    SetAnim(phaseTime < 0.5 ? "land" : "swim");
                    speed *= Math.Exp(-4 * dt);
                    onWater = true;
                    if (phaseTime >= SplashSeconds)
                    {
                        heading = flyHeading; target = null; pos.Roll = 0;
                        SetPhase(Phase.Water);
                        return;
                    }
                    break;
            }

            if (entity.CollidedHorizontally && (phase == Phase.Fly || phase == Phase.Glide))
            {
                // clipped something the scan missed: stop pushing into it and climb straight up
                speed = Math.Min(speed, MinPullSpeed); vy = SteepClimb;
                if (phase == Phase.Glide) SetPhase(Phase.Fly);
            }
            travel += speed * dt;
            pos.Motion.Set(Math.Sin(flyHeading) * speed / 60, onWater ? SurfaceMotion() : vy / 60, Math.Cos(flyHeading) * speed / 60);
            pos.Yaw = (float)flyHeading;
            // Pitch the whole body with the climb angle. For this model the renderer's Roll turns about the wing axis
            // (applied after yaw; head at -X), and negative is nose-up. Synced and interpolated like the yaw.
            double pitch = phase == Phase.Run ? 0.15 : phase == Phase.Splash ? 0 : Math.Atan2(vy, Math.Max(speed, 0.5));
            double wantRoll = -GameMath.Clamp(pitch, -0.45, 1.1);
            pos.Roll += (float)((wantRoll - pos.Roll) * Math.Min(1, dt * 6));
        }

        /// <summary>Height to fly at: over a short hop a few blocks above the water, further off higher (up to 22),
        /// and always GroundClear above the ground and trees just ahead.</summary>
        private double CruiseY()
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            double baseY = target?.Y ?? pos.Y, alt = target != null ? GameMath.Clamp(HorDist(target) * 0.25, 6, 22) : 18;
            double ground = 0;
            for (int d = 0; d <= 24; d += 6)
            {
                int h = ba.GetRainMapHeightAt((int)Math.Floor(pos.X + Math.Sin(flyHeading) * d), (int)Math.Floor(pos.Z + Math.Cos(flyHeading) * d));
                if (h > 0) ground = Math.Max(ground, h + 1);
            }
            if (target == null && ground > 0) baseY = ground;
            return Math.Max(baseY + alt, ground + GroundClear);
        }

        /// <summary>
        /// Steepest climb (blocks up per block forward) needed to keep the feet Clearance above every ground or tree
        /// top within dist along towards, sampled every 1.5 blocks down the middle and at each wingtip; -infinity if
        /// nothing is in the way. Open water never counts.
        /// </summary>
        private double NeededSlope(double towards, double dist)
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            double sx = Math.Sin(towards), sz = Math.Cos(towards), worst = double.NegativeInfinity;
            var tmp = new BlockPos(0, 0, 0);
            for (double d = 1; d <= dist; d += 1.5)
                for (int side = -1; side <= 1; side++)
                {
                    int x = (int)Math.Floor(pos.X + sx * d + sz * side * HalfSpan), z = (int)Math.Floor(pos.Z + sz * d - sx * side * HalfSpan);
                    int h = ba.GetRainMapHeightAt(x, z);
                    if (h <= 0 || h + 1 + Clearance <= pos.Y) continue;                       // unloaded, or well below
                    tmp.Set(x, h, z);
                    if (ba.GetBlock(tmp, BlockLayersAccess.Fluid)?.IsLiquid() == true) continue;
                    // the face may be up to one sample spacing nearer than the sample that sees it
                    worst = Math.Max(worst, (h + 1 + Clearance - pos.Y) / Math.Max(d - 2.1, 0.4));
                }
            return worst;
        }

        /// <summary>
        /// Raises vy to clear whatever is ahead, starting early (LookSeconds out) so most climbs are gentle. When the
        /// climb is steeper than SteepClimb allows at the current speed, caps the speed so the climb angle is enough:
        /// the goose pulls up hard and sheds speed, down to a hover-climb. If something is overhead (it flew in under
        /// a crown), climbing can't help: it turns hard toward the most open direction instead.
        /// </summary>
        private void AvoidTerrain(ref double vy, ref double cap, float dt)
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            int x = (int)Math.Floor(pos.X), z = (int)Math.Floor(pos.Z), above = ba.GetRainMapHeightAt(x, z);
            if (above + 1 > pos.Y + 1.2 && ba.GetBlock(new BlockPos(x, above, z), BlockLayersAccess.Fluid)?.IsLiquid() != true)
            {
                double best = flyHeading, bestSlope = double.MaxValue;
                foreach (double off in new[] { 0, 0.8, -0.8, 1.6, -1.6, Math.PI })
                {
                    double sl = NeededSlope(flyHeading + off, 6);
                    if (sl < bestSlope) { bestSlope = sl; best = flyHeading + off; }
                }
                Steer(best, TurnRate * 2.5, dt);
                vy = Math.Min(vy, 0.5); cap = Math.Min(cap, 4);
                return;
            }
            double slope = NeededSlope(flyHeading, Math.Max(8, speed * LookSeconds));
            if (double.IsNegativeInfinity(slope)) return;
            vy = Math.Max(vy, Math.Min(slope * Math.Max(speed, MinPullSpeed) * 1.35 + 0.6, SteepClimb));
            cap = Math.Min(cap, Math.Max(MinPullSpeed, SteepClimb / (slope * 1.35)));
        }

        /// <summary>
        /// Geese run out along open water, not at the bank. A wall within ~4 blocks of where the feet leave the water
        /// can't be cleared from a run (side-on simulation: 10+ block trees 2 blocks off were hit; from 4-8 blocks out
        /// even a 24-block cliff was cleared). So take the smallest turn, up to 120 degrees either way, whose next 12
        /// blocks need no more than a 1.5 climb; if none does, the least steep.
        /// </summary>
        private double ClearestHeading(double away)
        {
            double best = away, bestSlope = double.MaxValue;
            foreach (double off in new[] { 0, 0.4, -0.4, 0.8, -0.8, 1.25, -1.25, 1.7, -1.7, 2.1, -2.1 })
            {
                double s = NeededSlope(away + off, 12);
                if (s <= 1.5) return away + off;
                if (s < bestSlope) { bestSlope = s; best = away + off; }
            }
            return best;
        }

        private double ClimbRate(double wantY) => GameMath.Clamp((wantY - entity.Pos.Y) * 0.8, -SinkMax, phaseTime < 2 ? ClimbMax * 0.8 : ClimbMax);

        private void Steer(double want, double rate, float dt)
            => flyHeading += GameMath.Clamp(GameMath.AngleRadDistance((float)flyHeading, (float)want), -rate * dt, rate * dt);

        private double HorDist(Vec3d p) => Math.Sqrt((p.X - entity.Pos.X) * (p.X - entity.Pos.X) + (p.Z - entity.Pos.Z) * (p.Z - entity.Pos.Z));

        private void SetPhase(Phase p) { phase = p; phaseTime = 0; Persist(); }

        /// <summary>Flight state lives in WatchedAttributes so it is saved with the entity (chunk unloads, restarts).</summary>
        private void Persist()
        {
            if (phase == Phase.Water) { entity.WatchedAttributes.RemoveAttribute(FlightKey); return; }
            var t = new TreeAttribute();
            t.SetInt("phase", (int)phase);
            t.SetBool("depart", departing);
            t.SetDouble("travel", travel); t.SetDouble("cruise", cruise);
            t.SetDouble("threatX", threatX); t.SetDouble("threatZ", threatZ);
            if (target != null) { t.SetDouble("tx", target.X); t.SetDouble("ty", target.Y); t.SetDouble("tz", target.Z); }
            entity.WatchedAttributes[FlightKey] = t;
        }

        private void Restore()
        {
            if (entity.WatchedAttributes[FlightKey] is not ITreeAttribute t) return;
            var p = (Phase)t.GetInt("phase");
            // a goose saved mid-run carries on as a flier; one saved on the water mid-splash just floats
            phase = p == Phase.Run ? Phase.Fly : p == Phase.Splash ? Phase.Water : p;
            departing = t.GetBool("depart"); travel = t.GetDouble("travel"); cruise = t.GetDouble("cruise", cruiseMin);
            threatX = t.GetDouble("threatX"); threatZ = t.GetDouble("threatZ");
            target = t.HasAttribute("tx") ? new Vec3d(t.GetDouble("tx"), t.GetDouble("ty"), t.GetDouble("tz")) : null;
            if (target != null && t.GetBool("rawTarget")) { target.Y -= sink; Persist(); } // set by PrepareLanding
            flyHeading = entity.Pos.Yaw; speed = cruise; phaseTime = 2; searchTimer = 0;
            if (phase == Phase.Water) Persist();
        }

        /// <summary>
        /// Before spawning a goose of a landing flock (LandingFlocks): it starts in flight at speed, already
        /// headed for spot (a water-surface point from LakeSearch), and glides in on its first ticks. Written in the
        /// same saved form Restore() reads; the bird's own sink is applied there.
        /// </summary>
        public static void PrepareLanding(Entity goose, Vec3d spot, double heading, double speed, double threatX, double threatZ)
        {
            var t = new TreeAttribute();
            t.SetInt("phase", (int)Phase.Fly);
            t.SetBool("depart", false);
            t.SetDouble("travel", 0); t.SetDouble("cruise", speed);
            t.SetDouble("threatX", threatX); t.SetDouble("threatZ", threatZ);
            t.SetDouble("tx", spot.X); t.SetDouble("ty", spot.Y); t.SetDouble("tz", spot.Z);
            t.SetBool("rawTarget", true);
            goose.WatchedAttributes[FlightKey] = t;
            goose.Pos.Yaw = (float)heading;
            goose.SimulationRange = FlightRange; // active from the start, though it appears far from any player
        }

        /// <summary>A goose loaded from a save mid-flight needs its flight range before its first tick: beyond the
        /// default 128 blocks from every player it would otherwise be inactive and never tick to set it.</summary>
        public override void OnEntityLoaded()
        {
            if (entity.WatchedAttributes.HasAttribute(FlightKey)) entity.SimulationRange = FlightRange;
        }

        /// <summary>Climbing away for good, ready to be handed to the sky renderer.</summary>
        public bool DepartingInFlight => phase == Phase.Fly && departing;
        public double FlyHeading => flyHeading;
        public double FlySpeed => speed;

        public override void OnEntityDeath(DamageSource damageSourceForDeath)
        {
            if (entity.World.Side == EnumAppSide.Server) FlockRegistry.Instance?.MemberDied(entity);
        }

        // ------------------------------------------------------------------------------------------------ water

        /// <summary>
        /// Sets shoreHeading while the goose is more than 1.5 x ShoreDistance from any shore (8-direction scan up to
        /// 3 x ShoreDistance; if nothing is found at all, no steering). Cleared once it is back within range.
        /// </summary>
        private void CheckShore()
        {
            if (!(entity.Swimming || entity.FeetInLiquid)) { shoreHeading = double.NaN; return; }
            int far = (int)(ResidentGeese.ShoreDistanceCfg * 1.5);
            var ba = entity.World.BlockAccessor; var bp = entity.Pos.AsBlockPos;
            if (ResidentGeese.ShoreDirection(ba, bp, far, out _) >= 0) { shoreHeading = double.NaN; return; }
            shoreHeading = ResidentGeese.ShoreDirection(ba, bp, ResidentGeese.ShoreDistanceCfg * 3, out double h) >= 0 ? h : double.NaN;
        }

        /// <summary>Vertical motion that eases the bird to sit a little below the top of the water it is in.</summary>
        private double SurfaceMotion()
        {
            var ba = entity.World.BlockAccessor;
            var p = entity.Pos.AsBlockPos;
            int y = p.Y, top = p.Y;
            for (int i = 0; i < 6; i++, y++)
            {
                var b = ba.GetBlock(new BlockPos(p.X, y, p.Z), BlockLayersAccess.Fluid);
                if (b == null || !b.IsLiquid()) break;
                top = y + 1;
            }
            double target = top - 0.125 - sink;
            return GameMath.Clamp((target - entity.Pos.Y) * 0.15, -0.03, 0.03);
        }

        /// <summary>Nearest living player within range, ignoring spectators (they can't be seen).</summary>
        private Entity NearestPlayer(float range) =>
            entity.World.GetNearestEntity(entity.Pos.XYZ, range, range, e => e is EntityPlayer ep && ep.Alive
                && ep.Player?.WorldData?.CurrentGameMode != EnumGameMode.Spectator);

        /// <summary>Water at the goose's level (or one below, for a goose standing ashore) dist blocks along heading.</summary>
        private bool WaterAt(double towards, double dist)
        {
            var ba = entity.World.BlockAccessor;
            int x = (int)Math.Floor(entity.Pos.X + Math.Sin(towards) * dist), z = (int)Math.Floor(entity.Pos.Z + Math.Cos(towards) * dist);
            int y = (int)Math.Floor(entity.Pos.Y);
            for (int dy = 0; dy >= -1; dy--)
            {
                var b = ba.GetBlock(new BlockPos(x, y + dy, z), BlockLayersAccess.Fluid);
                if (b != null && b.IsLiquid() && b.LiquidCode == "water") return true;
            }
            return false;
        }

        /// <summary>The heading itself if there's water just ahead, else the direction of the nearest water within 6
        /// blocks (closest to the current heading on ties), else a random turn.</summary>
        private double TowardWater(double current)
        {
            if (WaterAt(current, 0.8)) return current;
            for (int dist = 1; dist <= 6; dist++)
            {
                double best = double.NaN, bestTurn = double.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    double h = current + i * Math.PI / 4, turn = Math.Abs(GameMath.AngleRadDistance((float)current, (float)h));
                    if (turn < bestTurn && WaterAt(h, dist)) { best = h; bestTurn = turn; }
                }
                if (!double.IsNaN(best)) return best;
            }
            return current + Math.PI * (0.5 + rand.NextDouble());
        }

        private void SetAnim(string code)
        {
            if (anim == code) return;
            if (anim != null) entity.AnimManager?.StopAnimation(anim);
            anim = code;
            float speedMul = code switch { "takeoff" => 2.6f, "fly-flap" => 1.6f, "land" => 1.3f, _ => 1f };
            entity.AnimManager?.StartAnimation(new AnimationMetaData { Code = code, Animation = code, AnimationSpeed = speedMul }.Init());
        }

        // ------------------------------------------------------------------------------------------------ damage

        public override void OnEntityReceiveDamage(DamageSource source, ref float damage)
        {
            if (entity.World.Side != EnumAppSide.Server || damage <= 0 || source == null) return;
            var from = source.GetCauseEntity() ?? source.SourceEntity;
            if (Dodges(source))
            {
                damage = 0;
                double away = from != null ? Math.Atan2(entity.Pos.X - from.Pos.X, entity.Pos.Z - from.Pos.Z) : rand.NextDouble() * Math.PI * 2;
                TakeoffGroup(away, from, false);
                return;
            }
            if (from != null)
            {
                // a real hit: the rest of the group flies off; this one too if it survives (health runs after us).
                // The threat is the shooter, not the arrow, so they land well away from whoever is shooting.
                var threat = source.GetCauseEntity() ?? from;
                double away = Math.Atan2(entity.Pos.X - threat.Pos.X, entity.Pos.Z - threat.Pos.Z);
                entity.World.RegisterCallback(_ => { if (entity.Alive) TakeoffGroup(away, threat, false); else ScareGroup(away, threat); }, 1);
            }
        }

        /// <summary>A goose was killed: the others on the water flush, using the same group search.</summary>
        private void ScareGroup(double away, Entity threat)
        {
            foreach (var e in entity.World.GetEntitiesAround(entity.Pos.XYZ, 16, 8, e => e.Code == entity.Code && e.Alive && e != entity))
            {
                e.GetBehavior<EntityBehaviorWaterfowl>()?.TakeoffGroup(away, threat, false);
                return; // TakeoffGroup brings the rest along
            }
        }

        /// <summary>
        /// Spears and melee are dodged; ranged projectiles (arrows, thrown stones, other mods' shot) are not.
        /// A projectile hit has SourceEntity = the projectile (not an agent); a melee hit has the attacker itself as
        /// the source. A thrown spear is a projectile whose code starts with "spear".
        /// </summary>
        private static bool Dodges(DamageSource source)
        {
            var src = source.SourceEntity;
            var cause = source.GetCauseEntity();
            if (src == null && cause == null) return false;                          // fire, falls, drowning, commands
            if (src != null && !(src is EntityAgent))
                return src.Code?.Path?.StartsWith("spear") == true;                  // a projectile: only spears miss
            return true;                                                             // melee by an agent (any weapon)
        }
    }
}
