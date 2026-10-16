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
    public class EntityBehaviorWaterfowl : EntityBehaviorBird
    {
        public const string Code = "flocks.waterfowl";
        private const string SpawnKey = "flocksSpawnHours", FlightKey = "flocksFlight";
        /// <summary>Set by /flocks land on its test geese: they leave after lifeHours. Every other goose is a resident
        /// (spawned by vanilla's spawner) and stays until its resident season ends.</summary>
        public const string TestKey = "flocksTestGoose";

        public bool Resident => !entity.WatchedAttributes.HasAttribute(TestKey);
        private float seasonCheck = 5;
        private float shotCheck;
        private double rangeMult = 1;                  // SkittishRangeFactor while the flock is skittish
        private double senseMul = 1;                   // the noticed player's sensing multiplier (sneaking, stats)
        private long FlockId => entity.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0);
        // far-from-shore check: every few seconds; while too far out, drift is pulled toward the nearest land seen
        private float shoreCheck = 2;
        private double shoreHeading = double.NaN;

        private float fleeRange = 14, takeoffRange = 7, lifeHours = 6, sink = 0.15f;
        private double driftSpeed = 0.004, paddleSpeed = 0.025;

        // ---- flight, in blocks and seconds (Motion is per 1/60 s, hence the /60 where it is set)
        private enum Phase { Water, Run, Fly, Glide, Splash }
        /// <summary>takeoffRunSeconds / takeoffRunSpeed attributes: geese run 0.55 s across the water; dabbling ducks
        /// spring almost straight up.</summary>
        private double RunSeconds = 0.55, RunSpeed = 7;
        private double cruiseMin = 11, cruiseMax = 13.5;
        private const double Accel = 7, ClimbMax = 4.5, SinkMax = 3, GroundClear = 6;
        /// <summary>Gliding in: blocks forward per block down, and the speed at touchdown.</summary>
        private const double GlideRatio = 3.5, LandSpeed = 6, SplashSeconds = 0.9;
        /// <summary>Species code of this bird's flock (FlockRegistry, Species.cs).</summary>
        private string species = "goose";
        private Species speciesData;
        /// <summary>This bird's species data (config/flocks-species/), from the server's mod system.</summary>
        private Species SpeciesData => speciesData ??= entity.Api?.ModLoader.GetModSystem<FlocksModSystem>()?.SpeciesByCode.GetValueOrDefault(species);
        /// <summary>Chances a bird plays its takeoffwings and takeoffcall sounds (entity sounds) as it takes off, so a
        /// group doesn't play one of each per bird.</summary>
        private double takeoffWingsChance = 0.6, takeoffCallChance = 0.4;
        /// <summary>Wading birds (species habitat "wade"): stand on the bottom of water one block deep, and have a taller
        /// selection box while standing there (wadeHitboxHeight), so the game's swimming check (water at 66% of it)
        /// looks above the water and they stand instead of floating. In flight they use the entity's own box.</summary>
        private bool Wade => SpeciesData?.Wades == true;
        private float wadeHitboxHeight = 1.55f, flightHitboxHeight = -1, hitboxWidth = 0.6f;
        /// <summary>Gulls (attributes wheelSecondsMin/Max, loafHoursMin/Max, soarShare): after taking off the group
        /// wheels round over the water for a while, gaining height, before heading for its spot; every loafHours or so a
        /// group takes to the air of its own accord (no disturbance) and comes down further along; and in level flight
        /// it glides soarShare of the time. All 0 = off (geese, ducks, herons).</summary>
        private double wheelMin, wheelMax, loafMin, loafMax, soarShare;
        private double wheelLeft, wheelX, wheelZ, wheelY, wheelR = 12, wheelAlt = 12, nextLoaf = double.NaN;
        private float glideCycle;
        private bool gliding;
        // wading activity: rest, stalk (walk), strike; how long it lasts
        private string activity = "rest";
        private float activityTime = 3;
        private const double StalkSpeed = 0.45;      // blocks/s

        /// <summary>Smallest lake this species lands on (its species data; else the server default).</summary>
        private int MinLake => SpeciesData?.MinLakeSize > 0 ? SpeciesData.MinLakeSize : Landing.MinPatch;
        /// <summary>
        /// A spot is given up after ApproachGiveUp seconds of trying to get in, MaxGoArounds aborted glides, or when the
        /// way in would need a descent steeper than MaxApproachSlope (blocks down per block forward; 1 = 45 deg, about
        /// what a goose dropping into a small pond among trees manages). After MaxSpotChanges given-up spots in one
        /// flight the flock rejoins the migration instead.
        /// </summary>
        private const float ApproachGiveUp = 40;
        private const int MaxGoArounds = 3, MaxSpotChanges = 3;
        private const double MaxApproachSlope = 1.0;
        /// <summary>The approach lane: geese come in along the clearest of 16 directions from the spot, joining it out at
        /// LaneLength and pursuing a point LaneLook further in (top-down simulation: from 2880 starting positions and
        /// headings, every goose was on the lane and lined up within 23 s).</summary>
        private const double LaneLength = 32, LaneLook = 14;
        /// <summary>Fastest sink in a glide (blocks/s); with the speed eased down so the line can be held.</summary>
        private const double GlideSinkMax = 6.5;

        private Phase phase = Phase.Water;
        private bool restored, runStarted, hardFlap;
        private float phaseTime, searchTimer;
        private Vec3d target;              // landing point at the water surface; null while searching or departing
        // the current spot's approach: how steeply the way in must descend (from where the goose is now), time spent
        // trying, aborted glides, and spots already given up this flight
        private double approachSlope, laneX, laneZ;   // the chosen lane: its slope and outward direction
        private bool laneChosen;
        private float approachTime, glideCheck;
        private int goArounds, spotChanges;
        private readonly List<Vec3d> badSpots = new();
        private bool departing;
        private double cruise, travel, threatX, threatZ;

        private Entity scarer;
        private float nextCheck;
        private double heading;

        public EntityBehaviorWaterfowl(Entity entity) : base(entity) { }

        protected override float AnimSpeed(string code) => code switch { "takeoff" => 2.6f, "fly-flap" => 1.6f, "land" => 1.3f, _ => 1f };
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
            takeoffWingsChance = attributes["takeoffWingsChance"].AsDouble(takeoffWingsChance);
            RunSeconds = Math.Max(0.05, attributes["takeoffRunSeconds"].AsDouble(RunSeconds));
            wadeHitboxHeight = attributes["wadeHitboxHeight"].AsFloat(wadeHitboxHeight);
            RunSpeed = attributes["takeoffRunSpeed"].AsDouble(RunSpeed);
            takeoffCallChance = attributes["takeoffCallChance"].AsDouble(takeoffCallChance);
            wheelMin = attributes["wheelSecondsMin"].AsDouble(0); wheelMax = attributes["wheelSecondsMax"].AsDouble(wheelMin);
            loafMin = attributes["loafHoursMin"].AsDouble(0); loafMax = attributes["loafHoursMax"].AsDouble(loafMin);
            soarShare = GameMath.Clamp(attributes["soarShare"].AsDouble(0), 0, 0.9);
            heading = rand.NextDouble() * Math.PI * 2;
        }

        public override void OnGameTick(float dt)
        {
            if (entity.World.Side != EnumAppSide.Server) return;
            var pos = entity.Pos;
            if (!entity.Alive) { pos.Roll = 0; if (entity.Swimming || entity.FeetInLiquid) pos.Motion.Y = SurfaceMotion(); return; }
            if (!restored) { restored = true; Restore(); FlockRegistry.Instance?.Adopt(entity, species); }
            LetSpearsPass(dt);
            entity.SimulationRange = phase == Phase.Water ? GlobalConstants.DefaultSimulationRange : FlightRange;
            if (Wade)
            {
                if (flightHitboxHeight < 0) { flightHitboxHeight = entity.SelectionBox.Y2; hitboxWidth = entity.SelectionBox.X2 - entity.SelectionBox.X1; }
                float want = phase == Phase.Water || phase == Phase.Splash ? wadeHitboxHeight : flightHitboxHeight;
                if (Math.Abs(entity.SelectionBox.Y2 - want) > 0.01) entity.SetSelectionBox(hitboxWidth, want);
            }
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
                if (SpeciesData != null && !ResidentGeese.InSeason(entity.World, bp, SpeciesData))
                {
                    // autumn: the whole group leaves toward the equator (0 = south, pi = north)
                    TakeoffGroup((lat >= 0 ? 0 : Math.PI) + (rand.NextDouble() - 0.5) * 0.5, null, true);
                    return;
                }
            }

            if ((shotCheck -= dt) <= 0)
            {
                shotCheck = 0.1f;
                if (IncomingShot(out double shotAway, out Entity shooter, out Vec3d from)) { TakeoffGroup(shotAway, shooter, false, from); return; }
            }
            // a loafing flight: now and then the group goes up of its own accord, wheels and settles further along
            if (loafMax > 0 && !Wade)
            {
                if (double.IsNaN(nextLoaf)) nextLoaf = now + loafMin + rand.NextDouble() * (loafMax - loafMin);
                else if (now >= nextLoaf && scarer == null) { TakeoffGroup(rand.NextDouble() * Math.PI * 2, null, false, null, disturb: false); return; }
            }
            // the nearest player is looked up 4 times a second and kept in between, so fleeing is continuous;
            // a skittish flock (lake wariness) flees and takes off from further away
            if ((nextCheck -= dt) <= 0)
            {
                nextCheck = 0.25f;
                rangeMult = FlockRegistry.Instance?.Skittish(FlockId) == true ? (FlockRegistry.Instance?.SkittishRangeFactor ?? 1) : 1;
                scarer = NearestPlayer((float)(fleeRange * rangeMult), out senseMul);
            }
            var player = scarer;
            double speedNow = driftSpeed;
            if (player != null)
            {
                double dx = pos.X - player.Pos.X, dz = pos.Z - player.Pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
                double away = Math.Atan2(dx, dz);
                if (d < takeoffRange * rangeMult * senseMul) { TakeoffGroup(away, player, false); return; }
                if (d < fleeRange * rangeMult * senseMul) { heading = away; speedNow = paddleSpeed; }
            }
            if (Wade) { TickWade(dt, speedNow > driftSpeed); return; }
            if (player == null)
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
        private void TakeoffGroup(double away, Entity threat, bool depart, Vec3d threatAt = null, bool disturb = true)
        {
            var pos = entity.Pos;
            double tx = threat?.Pos.X ?? threatAt?.X ?? pos.X, tz = threat?.Pos.Z ?? threatAt?.Z ?? pos.Z;
            // every flush disturbs the flock (bold species less: WarinessFactor); disturbed often enough, it leaves this
            // lake and rejoins the migration. A loafing flight is no disturbance.
            if (!depart && disturb && FlockRegistry.Instance?.Disturb(FlockId, SpeciesData?.WarinessFactor ?? 1, pos.XYZ) == true) depart = true;
            List<Vec3d> spots = depart ? null : FindSpots(pos.XYZ, tx, tz);
            if (spots == null || spots.Count == 0) depart = true; // nowhere to land nearby: they rejoin the migration
            Takeoff(away, Pick(spots), depart, tx, tz, 0);
            foreach (var e in entity.World.GetEntitiesAround(pos.XYZ, 16, 8, e => e.Code == entity.Code && e.Alive && e != entity))
                e.GetBehavior<EntityBehaviorWaterfowl>()?.Takeoff(away + (rand.NextDouble() - 0.5) * 0.6, Pick(spots), depart, tx, tz,
                    (float)(0.08 + rand.NextDouble() * 0.5));
        }

        /// <summary>Go up for a loafing flight now (tests). False unless it is on the water and its species loafs.</summary>
        public bool Loaf()
        {
            if (phase != Phase.Water || loafMax <= 0 || Wade || !entity.Alive) return false;
            TakeoffGroup(rand.NextDouble() * Math.PI * 2, null, false, null, disturb: false);
            return true;
        }

        /// <summary>A bird of prey stooping at this bird or its group (Raptor.cs): they flush away from it, a disturbance
        /// like any other (lake wariness: hunted often enough, they abandon the lake).</summary>
        public void Alarm(Entity raptor)
        {
            if (phase != Phase.Water || !entity.Alive) return;
            TakeoffGroup(Math.Atan2(entity.Pos.X - raptor.Pos.X, entity.Pos.Z - raptor.Pos.Z), raptor, false);
        }

        private Vec3d Pick(List<Vec3d> spots)
        {
            if (spots == null) return null;
            var ok = spots.FindAll(sp => !badSpots.Exists(b => b.HorizontalSquareDistanceTo(sp.X, sp.Z) < 25));
            return ok.Count == 0 ? null : ok[rand.Next(ok.Count)].Clone();
        }

        /// <summary>A new spot to come down on: the approach bookkeeping starts over.</summary>
        private void NewTarget(Vec3d spot)
        {
            target = spot; approachTime = 0; goArounds = 0; laneChosen = false; approachSlope = 0;
        }

        /// <summary>This spot can't be reached: remember it, and look for another (or, after a few, rejoin the migration).</summary>
        private void GiveUpSpot()
        {
            if (target != null) badSpots.Add(target.Clone());
            NewTarget(null);
            searchTimer = 0;
            if (++spotChanges > MaxSpotChanges) departing = true;
            if (phase == Phase.Glide) SetPhase(Phase.Fly); else Persist();
        }

        public void Takeoff(double towards, Vec3d spot, bool depart, double tx, double tz, float delay)
        {
            if (phase != Phase.Water || !entity.Alive) return;
            phase = Phase.Run; phaseTime = -delay; runStarted = false;
            flyHeading = towards; speed = 0; travel = 0; searchTimer = 3;
            cruise = cruiseMin + rand.NextDouble() * (cruiseMax - cruiseMin);
            badSpots.Clear(); spotChanges = 0;
            NewTarget(spot); if (target != null) target.Y -= sink;
            departing = depart; threatX = tx; threatZ = tz;
            // gulls: wheel over the water first, the whole group the same way round
            wheelLeft = depart || wheelMax <= 0 ? 0 : wheelMin + rand.NextDouble() * (wheelMax - wheelMin);
            wheelX = entity.Pos.X; wheelZ = entity.Pos.Z; wheelY = entity.Pos.Y;
            wheelR = 10 + rand.NextDouble() * 8; wheelAlt = 9 + rand.NextDouble() * 9;
            nextLoaf = double.NaN;
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
                        // entity sounds (the entity file's sounds: takeoffsplash, takeoffwings, takeoffcall)
                        entity.PlayEntitySound("takeoffsplash");
                        if (rand.NextDouble() < takeoffWingsChance) entity.PlayEntitySound("takeoffwings");
                        if (rand.NextDouble() < takeoffCallChance) entity.PlayEntitySound("takeoffcall");
                    }
                    SetAnim("takeoff");
                    // pattering along the surface, wings thrashing, then the feet leave the water
                    speed = Math.Min(RunSpeed, speed + RunSpeed / RunSeconds * dt);
                    onWater = !Wade && phaseTime < RunSeconds * 0.7;
                    vy = onWater ? 0 : 2.5;
                    if (phaseTime >= RunSeconds) SetPhase(Phase.Fly);
                    break;

                case Phase.Fly:
                {
                    bool wheeling = wheelLeft > 0;
                    if (wheeling) wheelLeft -= dt;
                    if (target != null && !wheeling && TrackApproach(dt)) { GiveUpSpot(); break; }
                    if (target == null && !departing && (searchTimer -= dt) <= 0)
                    {
                        searchTimer = 4;
                        NewTarget(Pick(FindSpots(pos.XYZ, threatX, threatZ)));
                        if (target != null) { target.Y -= sink; Persist(); }
                        else { departing = true; Persist(); } // nowhere (left) to land: rejoin the migration
                    }
                    // Climb out straight for a moment, then turn toward the landing spot. A spot close by and well off
                    // the nose can sit inside the turning circle, where steering at it only orbits it: fly on until it
                    // is far enough to turn and line up (in a flight simulation every spot 14-110 blocks off, at any
                    // bearing, was reached in under 23 s with at most one go-round).
                    if (wheeling && phaseTime > 0.8)
                    {
                        // circle the takeoff spot: aim along the tangent, nudged in or out to hold the radius
                        double dx = pos.X - wheelX, dz = pos.Z - wheelZ, r = Math.Sqrt(dx * dx + dz * dz);
                        double dir = FlockId % 2 == 0 ? 1 : -1;
                        double tangent = Math.Atan2(dx, dz) + dir * (Math.PI / 2 + GameMath.Clamp((r - wheelR) / wheelR, -0.6, 0.6));
                        Steer(r > wheelR * 2.5 ? Math.Atan2(-dx, -dz) : tangent, TurnRate, dt);
                    }
                    else if (target != null && phaseTime > 0.8)
                    {
                        LaneAim(out double ax, out double az, out bool laneEnd);
                        double want = Math.Atan2(ax - pos.X, az - pos.Z);
                        // the fly-on-before-turning rule only for the lane's fixed outer end: the point pursued along the
                        // lane moves with the goose, and flying on from it would only lead off the lane
                        bool tooClose = laneEnd && Math.Sqrt((ax - pos.X) * (ax - pos.X) + (az - pos.Z) * (az - pos.Z)) < 2.2 * speed / TurnRate
                            && Math.Abs(GameMath.AngleRadDistance((float)flyHeading, (float)want)) > 1.2;
                        if (!tooClose) Steer(want, TurnRate, dt);
                    }
                    vy = ClimbRate(wheeling ? Math.Max(wheelY + wheelAlt, GroundAhead() + GroundClear) : CruiseY());
                    double cap = wheeling ? cruise * 0.85 : cruise;
                    AvoidTerrain(ref vy, ref cap, dt);
                    speed = speed < cap ? Math.Min(cap, speed + Accel * dt) : Math.Max(cap, speed - PullDecel * dt);
                    // beat hard while climbing out or pulling up, cruise-flap otherwise (with some hysteresis); a bird
                    // that glides a lot (soarShare) holds its wings out for stretches of level flight
                    if (phaseTime < 1.4 || vy > 3) hardFlap = true; else if (vy < 2) hardFlap = false;
                    if (soarShare > 0 && (glideCycle -= dt) <= 0)
                    {
                        gliding = !gliding;
                        glideCycle = (float)((gliding ? soarShare : 1 - soarShare) * (3 + rand.NextDouble() * 4));
                    }
                    SetAnim(hardFlap ? "takeoff" : (gliding && vy < 1.5 ? "fly-soar" : "fly-flap"));
                    if (target != null && !wheeling)
                    {
                        double d = HorDist(target), drop = pos.Y - target.Y;
                        double err = Math.Abs(GameMath.AngleRadDistance((float)flyHeading, (float)Math.Atan2(target.X - pos.X, target.Z - pos.Z)));
                        // glide in from the lane, once the straight line down to the spot is at least as steep as the
                        // lane needs (clearing banks and trees near the spot) and no shallower than a glide
                        double line = drop / Math.Max(d, 1);
                        double bearing = Math.Atan2(pos.X - target.X, pos.Z - target.Z), laneDir = Math.Atan2(laneX, laneZ);
                        bool onLane = Math.Abs(GameMath.AngleRadDistance((float)laneDir, (float)bearing)) < 25 * GameMath.DEG2RAD;
                        if (onLane && d <= drop * GlideRatio + 3 && line >= approachSlope + 0.03 && err < 0.5 && phaseTime > 1.5) SetPhase(Phase.Glide);
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
                    if (TrackApproach(dt)) { GiveUpSpot(); break; }
                    double d = HorDist(target), line = Math.Max(0, pos.Y - target.Y) / Math.Max(d, 0.3);
                    SetAnim(d < 8 ? "land" : "fly-soar");
                    // slow down enough to hold a steep line (a drop into a pond among trees), else ease to landing speed
                    double want = Math.Max(LandSpeed, Math.Min(cruise, GlideSinkMax / Math.Max(line, 0.05)));
                    speed = speed > want ? Math.Max(want, speed - 8 * dt) : Math.Max(LandSpeed, speed - 3 * dt);
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 1.3, dt);
                    // a straight line down to the spot at the current speed
                    vy = GameMath.Clamp(-(pos.Y - target.Y) / Math.Max(0.3, d / speed), -GlideSinkMax, 0.5);
                    // Something still between the goose and its spot rises above this line (the goose sank, or turned off
                    // the lane): go round. Only what is still ahead counts; banks and trees it has already passed don't.
                    if ((glideCheck -= dt) <= 0 && d > 3)
                    {
                        glideCheck = 0.25f;
                        double ux = (pos.X - target.X) / Math.Max(d, 0.01), uz = (pos.Z - target.Z) / Math.Max(d, 0.01);
                        double ahead = ApproachSlope(ux, uz, d - 1);
                        // no slack: "needs" already keeps Clearance, and slack 10 blocks out is most of that block and a half
                        if (ahead > line + 0.02) { GoAround($"obstacle ahead (line {line:0.00}, needs {ahead:0.00}, {d:0.0} out)"); break; }
                    }
                    if (Wade ? (entity.OnGround || pos.Y <= target.Y + 0.15) : (entity.Swimming || entity.FeetInLiquid || pos.Y <= target.Y + 0.15))
                    {
                        entity.PlayEntitySound("landsplash");
                        SetPhase(Phase.Splash);
                    }
                    else if ((d < 1.5 && pos.Y - target.Y > 1.5) || phaseTime > 15) GoAround(phaseTime > 15 ? "glide too long" : $"overshot {pos.Y - target.Y:0.0} high"); // go round
                    break;
                }

                case Phase.Splash:
                    // skid to a stop on the water, wings still up, then fold them and float; a wader settles onto the bottom
                    SetAnim(phaseTime < 0.5 ? "land" : (Wade ? "idle-stand" : "swim"));
                    speed *= Math.Exp((Wade ? -8 : -4) * dt);
                    if (Wade && !WadeAt(flyHeading, 0.7)) speed = 0;   // don't skid off the shallows into deep water
                    onWater = !Wade;
                    if (Wade) vy = -3;
                    if (phaseTime >= SplashSeconds)
                    {
                        heading = flyHeading; NewTarget(null); pos.Roll = 0; badSpots.Clear(); spotChanges = 0;
                        SetPhase(Phase.Water);
                        return;
                    }
                    break;
            }

            if (entity.CollidedHorizontally && (phase == Phase.Fly || phase == Phase.Glide))
            {
                // clipped something the scan missed: stop pushing into it and climb straight up
                speed = Math.Min(speed, MinPullSpeed); vy = SteepClimb;
                if (phase == Phase.Glide) GoAround("clipped something");
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
            var pos = entity.Pos;
            // with a spot: high enough to come down at the angle its way in needs, from here
            double baseY = target?.Y ?? pos.Y, alt = 18;
            if (target != null) { double d = HorDist(target); alt = Math.Min(40, Math.Max(GameMath.Clamp(d * 0.25, 6, 22), approachSlope * Math.Min(d, LaneLength + 8) + 2)); }
            double ground = GroundAhead();
            if (target == null && ground > 0) baseY = ground;
            return Math.Max(baseY + alt, ground + GroundClear);
        }

        /// <summary>The highest ground or tree top within 24 blocks straight ahead (0 if none is loaded).</summary>
        private double GroundAhead()
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            double ground = 0;
            for (int d = 0; d <= 24; d += 6)
            {
                int h = ba.GetRainMapHeightAt((int)Math.Floor(pos.X + Math.Sin(flyHeading) * d), (int)Math.Floor(pos.Z + Math.Cos(flyHeading) * d));
                if (h > 0) ground = Math.Max(ground, h + 1);
            }
            return ground;
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

        /// <summary>
        /// Approach bookkeeping, every tick while there is a spot: time spent, and on the first call the lane. True when
        /// the spot should be given up (too long, too many aborted glides, or no lane in at a manageable slope).
        /// </summary>
        private bool TrackApproach(float dt)
        {
            approachTime += dt;
            if (!laneChosen) { laneChosen = true; ChooseLane(); }
            return approachTime > ApproachGiveUp || goArounds >= MaxGoArounds || approachSlope > MaxApproachSlope;
        }

        /// <summary>
        /// The lane in: of 16 directions out from the spot, the one whose way in needs the gentlest descent, with a
        /// slight preference for the goose's own side so it doesn't fly round the lake for a marginally better one.
        /// </summary>
        private void ChooseLane()
        {
            var pos = entity.Pos;
            double bearing = Math.Atan2(pos.X - target.X, pos.Z - target.Z), best = double.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                double th = i * Math.PI / 8, ux = Math.Sin(th), uz = Math.Cos(th);
                double slope = ApproachSlope(ux, uz, LaneLength + 8);
                double score = slope + 0.15 * (1 - Math.Cos(GameMath.AngleRadDistance((float)bearing, (float)th)));
                if (score < best) { best = score; approachSlope = slope; laneX = ux; laneZ = uz; }
            }
        }

        /// <summary>Where to steer while there is a spot: out to the lane's outer end until on the lane, then a point
        /// LaneLook further in along it (laneEnd: aiming at the fixed outer end).</summary>
        private void LaneAim(out double ax, out double az, out bool laneEnd)
        {
            var pos = entity.Pos;
            double px = pos.X - target.X, pz = pos.Z - target.Z;
            double t = px * laneX + pz * laneZ, lat = Math.Abs(px * laneZ - pz * laneX);
            laneEnd = t < 10 || lat > Math.Max(6, t * 0.35);
            double a = laneEnd ? LaneLength : GameMath.Clamp(t - LaneLook, 0, LaneLength);
            ax = target.X + laneX * a; az = target.Z + laneZ * a;
        }

        /// <summary>
        /// The steepest descent (blocks down per block forward) a straight line into the spot along (ux, uz) must have
        /// to keep Clearance above every bank or tree top within maxDist of the spot, across the wingspan. Further out,
        /// the look-ahead climb (AvoidTerrain) deals with the ground. Open water never counts.
        /// </summary>
        private double ApproachSlope(double ux, double uz, double maxDist)
        {
            var ba = entity.World.BlockAccessor;
            double worst = 0;
            var tmp = new BlockPos(0, 0, 0);
            // every half block: a 1-thick wall crossed at an angle can fall between whole-block samples
            for (double s = 1.5; s <= maxDist; s += 0.5)
                for (int side = -1; side <= 1; side++)
                {
                    int x = (int)Math.Floor(target.X + ux * s + uz * side * HalfSpan), z = (int)Math.Floor(target.Z + uz * s - ux * side * HalfSpan);
                    int h = ba.GetRainMapHeightAt(x, z);
                    if (h <= 0 || h + 1 <= target.Y + 0.2) continue;              // unloaded, or no higher than the spot
                    tmp.Set(x, h, z);
                    if (ba.GetBlock(tmp, BlockLayersAccess.Fluid)?.IsLiquid() == true) continue;
                    worst = Math.Max(worst, (h + 1 + Clearance - target.Y) / s);
                }
            return worst;
        }

        private double ClimbRate(double wantY) => GameMath.Clamp((wantY - entity.Pos.Y) * 0.8, -SinkMax, phaseTime < 2 ? ClimbMax * 0.8 : ClimbMax);

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
            NewTarget(target);
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
        public override bool DepartingInFlight => phase == Phase.Fly && departing;
        /// <summary>Why the last glide was aborted (diagnostics and the in-engine tests).</summary>
        public string LastGoAround { get; private set; }

        private void GoAround(string why) { goArounds++; LastGoAround = why; SetPhase(Phase.Fly); }

        /// <summary>For diagnostics and the in-engine tests: Water, Run, Fly, Glide or Splash, and the landing spot.</summary>
        public string PhaseName => phase.ToString();
        protected override string StateText => $"{phase}, flees within {fleeRange * rangeMult:0} and takes off within {takeoffRange * rangeMult:0} (times your sensing)";
        public Vec3d Target => target;
        public double FlyHeading => flyHeading;
        public double FlySpeed => speed;

        public override void OnEntityDeath(DamageSource damageSourceForDeath)
        {
            if (entity.World.Side == EnumAppSide.Server) FlockRegistry.Instance?.MemberDied(entity, SpeciesData?.WarinessFactor ?? 1);
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

        /// <summary>Landing spots for this bird's habitat: open water, or water one block deep for waders.</summary>
        private List<Vec3d> FindSpots(Vec3d from, double tx, double tz) => Wade
            ? LakeSearch.FindWadeSpots(entity.World.BlockAccessor, from, tx, tz, MinLake, rand)
            : LakeSearch.FindSpots(entity.World.BlockAccessor, from, tx, tz, MinLake, rand, SpeciesData?.Liquid ?? "water");

        /// <summary>
        /// A wading bird at rest: mostly standing hunched, now and then stalking slowly through the shallows or striking
        /// at a fish, and alert (neck up) while backing away from someone too close. It keeps to water one block deep:
        /// a step that would leave it turns instead, and a bird that is out of it walks back to the nearest. The vertical
        /// is left to the game's physics (it stands on the bottom).
        /// </summary>
        private void TickWade(float dt, bool fleeing)
        {
            var pos = entity.Pos;
            double speedNow = 0;
            bool wading = LakeSearch.IsWadeable(entity.World.BlockAccessor, (int)Math.Floor(pos.X), (int)Math.Floor(pos.Z), out _);
            // in over its head (a landing a block off the shallows, say): float up and paddle back to them
            if (entity.Swimming) pos.Motion.Y = SurfaceMotion();
            if (!wading)
            {
                heading = TowardWade(heading);
                speedNow = StalkSpeed; SetAnim("walk");
            }
            else if (fleeing)
            {
                if (!WadeAt(heading, 0.8)) heading = TowardWade(heading);
                speedNow = WadeAt(heading, 0.8) ? StalkSpeed * 1.3 : 0;
                SetAnim("alert");
            }
            else
            {
                if ((activityTime -= dt) <= 0)
                {
                    double r = rand.NextDouble();
                    if (r < 0.55) { activity = "rest"; activityTime = 8 + (float)rand.NextDouble() * 17; }
                    else if (r < 0.85) { activity = "stalk"; activityTime = 2 + (float)rand.NextDouble() * 4; heading += (rand.NextDouble() - 0.5) * 2; }
                    else { activity = "strike"; activityTime = 1.0f; }
                }
                if (activity == "stalk")
                {
                    if (!WadeAt(heading, 0.8)) heading = TowardWade(heading);
                    speedNow = WadeAt(heading, 0.8) ? StalkSpeed : 0;
                    SetAnim(speedNow > 0 ? "walk" : "idle-stand");
                }
                else SetAnim(activity == "strike" ? "strike" : "idle-stand");
            }
            pos.Motion.X = Math.Sin(heading) * speedNow / 60;
            pos.Motion.Z = Math.Cos(heading) * speedNow / 60;
            if (entity.CollidedHorizontally) heading += Math.PI * (0.5 + rand.NextDouble());
            pos.Yaw = (float)heading;
        }

        /// <summary>Wadeable water dist blocks along heading, at the bird's own level (or up to two above, for one in over
        /// its head and looking for the shallows).</summary>
        private bool WadeAt(double towards, double dist)
        {
            var pos = entity.Pos;
            int x = (int)Math.Floor(pos.X + Math.Sin(towards) * dist), z = (int)Math.Floor(pos.Z + Math.Cos(towards) * dist);
            int dy = LakeSearch.IsWadeable(entity.World.BlockAccessor, x, z, out int y) ? y - (int)Math.Floor(pos.Y + 0.1) : int.MinValue;
            return dy >= -1 && dy <= (entity.Swimming ? 2 : 1);
        }

        /// <summary>The heading itself if wadeable water is just ahead, else toward the nearest within 6 blocks, else a turn.</summary>
        private double TowardWade(double current)
        {
            if (WadeAt(current, 0.8)) return current;
            for (int dist = 1; dist <= 6; dist++)
                for (int i = 0; i < 8; i++)
                {
                    double h = current + (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * Math.PI / 4;
                    if (WadeAt(h, dist)) return h;
                }
            return current + Math.PI * (0.5 + rand.NextDouble());
        }

        /// <summary>Its water (fresh, or the sea for sea birds) at the goose's level (or one below, for a goose standing
        /// ashore) dist blocks along heading.</summary>
        private bool WaterAt(double towards, double dist)
        {
            var ba = entity.World.BlockAccessor;
            string liquid = SpeciesData?.Liquid ?? "water";
            int x = (int)Math.Floor(entity.Pos.X + Math.Sin(towards) * dist), z = (int)Math.Floor(entity.Pos.Z + Math.Cos(towards) * dist);
            int y = (int)Math.Floor(entity.Pos.Y);
            for (int dy = 0; dy >= -1; dy--)
            {
                var b = ba.GetBlock(new BlockPos(x, y + dy, z), BlockLayersAccess.Fluid);
                if (b != null && b.IsLiquid() && b.LiquidCode == liquid) return true;
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

    }
}
