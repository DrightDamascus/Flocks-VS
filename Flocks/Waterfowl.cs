using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
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
    /// Geese only leave the world when they depart for good (the resident season ends, or a /flocks land test goose's
    /// time is up) or fly a long way without finding any lake, and then only once no player is near enough to see.
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
        /// <summary>Departing geese, and geese that flew GiveUpTravel blocks without finding a lake, despawn once no
        /// player is within VanishRange.</summary>
        private const float VanishRange = 128;
        private const double GiveUpTravel = 1200;

        private Phase phase = Phase.Water;
        private bool restored, runStarted;
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
            heading = rand.NextDouble() * Math.PI * 2;
        }

        public override void OnGameTick(float dt)
        {
            if (entity.World.Side != EnumAppSide.Server) return;
            var pos = entity.Pos;
            if (!entity.Alive) { if (entity.Swimming || entity.FeetInLiquid) pos.Motion.Y = SurfaceMotion(); return; }
            if (!restored) { restored = true; Restore(); }
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
                    SetAnim(phaseTime < 1.4 ? "takeoff" : "fly-flap");
                    speed = Math.Min(cruise, speed + Accel * dt);
                    if (target == null && !departing && (searchTimer -= dt) <= 0)
                    {
                        searchTimer = 4;
                        target = Pick(LakeSearch.FindSpots(entity.World.BlockAccessor, pos.XYZ, threatX, threatZ, Landing.MinPatch, rand));
                        if (target != null) { target.Y -= sink; Persist(); }
                        else if (travel > GiveUpTravel) { departing = true; Persist(); }
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
                    if (target != null)
                    {
                        double d = HorDist(target), drop = pos.Y - target.Y;
                        double err = Math.Abs(GameMath.AngleRadDistance((float)flyHeading, (float)Math.Atan2(target.X - pos.X, target.Z - pos.Z)));
                        if (d <= drop * GlideRatio + 3 && err < 0.5 && phaseTime > 1.5) SetPhase(Phase.Glide);
                    }
                    else if (departing && phaseTime > 6 && NearestPlayer(VanishRange) == null) { entity.Die(EnumDespawnReason.Expire); return; }
                    break;

                case Phase.Glide:
                {
                    double d = HorDist(target);
                    SetAnim(d < 8 ? "land" : "fly-soar");
                    speed = Math.Max(LandSpeed, speed - 3 * dt);
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 1.3, dt);
                    // a straight line down to the spot at the current speed
                    vy = GameMath.Clamp(-(pos.Y - target.Y) / Math.Max(0.3, d / speed), -4, 0.5);
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
                        heading = flyHeading; target = null;
                        SetPhase(Phase.Water);
                        return;
                    }
                    break;
            }

            if (entity.CollidedHorizontally && phase == Phase.Fly) vy = ClimbMax; // clipped a tree or a hillside
            travel += speed * dt;
            pos.Motion.Set(Math.Sin(flyHeading) * speed / 60, onWater ? SurfaceMotion() : vy / 60, Math.Cos(flyHeading) * speed / 60);
            pos.Yaw = (float)flyHeading;
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
            flyHeading = entity.Pos.Yaw; speed = cruise; phaseTime = 2; searchTimer = 0;
            if (phase == Phase.Water) Persist();
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
