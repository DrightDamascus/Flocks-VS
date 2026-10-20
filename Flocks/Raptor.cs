using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Flocks
{
    /// <summary>
    /// Server-side behaviour for birds of prey (flocks:eagle). An eagle keeps a territory around where it first appeared.
    /// It perches on the most prominent high point around (a peak, a ridge, a tree top), scanning; it soars in circles
    /// on a thermal, climbing slowly to 35-65 blocks above the ground; it glides off to the next thermal; and when it
    /// spots prey below (preyCodes: hares, mallards) it closes in at height until the prey is below it, then stoops,
    /// wings swept back, accelerating to 38 blocks/s, strikes, and mantles over the kill on the ground (walking onto
    /// it if the strike knocked it aside) before going back up. A miss pulls up out of the dive.
    /// Players: a perched or feeding eagle takes off when one comes close (sensed as vanilla animals sense players);
    /// a soaring one ignores them. Shots that come close and hits make it take off or veer away; thrown spears are
    /// dodged, arrows and melee are not (EntityBehaviorBird).
    /// </summary>
    public class EntityBehaviorRaptor : EntityBehaviorBird
    {
        public const string Code = "flocks.raptor";
        private const string StateKey = "flocksRaptor";
        private enum State { Perch, Takeoff, Soar, Glide, Stoop, Feed, ToPerch }

        // tuning (entity attributes)
        private float territory = 200, fleeRange = 26, takeoffRange = 16;
        private double soarSpeed = 10, soarAltMin = 35, soarAltMax = 65, stoopSpeed = 38;
        /// <summary>Fed, it doesn't hunt for this long (real seconds), like vanilla's foxes, wolves and bears ("saturated",
        /// 1200 s after eating). Starting to feed on a kill counts as a meal; a missed stoop doesn't.</summary>
        private double saturatedSeconds = 1200;
        private long fedUntilMs;
        /// <summary>Fed recently: not hunting (diagnostics and the in-engine tests).</summary>
        public bool Saturated => entity.World.ElapsedMilliseconds < fedUntilMs;
        private string[] preyCodes = { "game:hare-*", "flocks:mallard" };
        private const double HuntRange = 48, CircleMin = 16, CircleMax = 26;

        private State state = State.Perch;
        private float stateTime, stateLength = 30, shotCheck, nextCheck, huntCheck = 5;
        private bool restored;
        private double homeX, homeZ, thermalX, thermalZ, circleR = 20, circleDir = 1, soarAlt = 50;
        private Vec3d perchSpot;
        private Entity prey, meal;
        private bool diving, toMeal, alarmRolled, carrying;
        /// <summary>Chance that waterfowl spot a stooping eagle in time and flush (it can still catch one in the air).</summary>
        private const double SpottedChance = 0.6;
        private double senseMul = 1;
        /// <summary>Feeding: how far in front of its feet the bill reaches when mantling (blocks), so it stands with its
        /// head over the kill rather than on it.</summary>
        private const double MealReach = 0.5;

        public EntityBehaviorRaptor(Entity entity) : base(entity) { }
        public override string PropertyName() => Code;
        protected override Vec3d Destination => perchSpot ?? prey?.Pos.XYZ;
        protected override float AnimSpeed(string code) => code switch { "takeoff" => 1.6f, "fly-flap" => 1.1f, "land" => 1.2f, _ => 1f };

        /// <summary>For diagnostics and the in-engine tests.</summary>
        public string StateName => state.ToString();
        protected override string StateText => $"{state}{(Saturated ? $", fed (not hunting for {(fedUntilMs - entity.World.ElapsedMilliseconds) / 1000} s)" : ", hunting")}, takes off from a perch within {takeoffRange:0} (times your sensing)";
        public Entity Prey => prey;
        public Entity Meal => meal;
        public float StateTime => stateTime;
        public bool Diving => diving;
        public Vec3d PerchSpot => perchSpot;

        /// <summary>Stoop on this prey now (tests). False if feeding.</summary>
        public bool Hunt(Entity target)
        {
            if (state == State.Feed || target == null) return false;
            prey = target; speed = soarSpeed;
            SetState(State.Stoop);
            return true;
        }

        /// <summary>Fly to the best perch around now (tests, and a possible admin command). False if there is none.</summary>
        public bool GoPerch()
        {
            if (state == State.Perch || state == State.Feed || !FindPerch()) return false;
            SetState(State.ToPerch);
            return true;
        }

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            territory = attributes["territory"].AsFloat(territory);
            fleeRange = attributes["fleeRange"].AsFloat(fleeRange);
            takeoffRange = attributes["takeoffRange"].AsFloat(takeoffRange);
            soarSpeed = attributes["soarSpeed"].AsDouble(soarSpeed);
            soarAltMin = attributes["soarAltitudeMin"].AsDouble(soarAltMin);
            soarAltMax = attributes["soarAltitudeMax"].AsDouble(soarAltMax);
            stoopSpeed = attributes["stoopSpeed"].AsDouble(stoopSpeed);
            saturatedSeconds = attributes["saturatedSeconds"].AsDouble(saturatedSeconds);
            var codes = attributes["preyCodes"].AsArray<string>(null);
            if (codes != null) preyCodes = codes;
        }

        public override void OnGameTick(float dt)
        {
            if (entity.World.Side != EnumAppSide.Server) return;
            if (entity.Alive && HoldIfFrozen()) return;   // /flocks lineup
            if (!entity.Alive) { entity.Pos.Roll = 0; return; }
            if (!restored) { restored = true; Restore(); }
            LetSpearsPass(dt);
            TrackPosition(dt);
            IdleCalls();
            bool grounded = state == State.Perch || state == State.Feed;
            entity.SimulationRange = grounded ? GlobalConstants.DefaultSimulationRange : FlightRange;
            stateTime += dt;

            if ((shotCheck -= dt) <= 0)
            {
                shotCheck = 0.1f;
                if (IncomingShot(out double shotAway, out _, out _)) { Startle(shotAway); return; }
            }
            if (grounded) { TickGround(dt); return; }
            TickAir(dt);
        }

        // ------------------------------------------------------------------------------------------------ ground

        private void TickGround(float dt)
        {
            var pos = entity.Pos;
            pos.Motion.X = 0; pos.Motion.Z = 0;               // the vertical is left to physics: it stands on its perch
            pos.Roll = 0;
            if ((nextCheck -= dt) <= 0)
            {
                nextCheck = 0.25f;
                var p = NearestPlayer(fleeRange, out senseMul);
                if (p != null && p.Pos.HorDistanceTo(pos.XYZ) < takeoffRange * senseMul)
                {
                    TakeOff(Math.Atan2(pos.X - p.Pos.X, pos.Z - p.Pos.Z));
                    return;
                }
            }
            if (state == State.Feed)
            {
                if (stateTime > stateLength) { meal = null; TakeOff(rand.NextDouble() * Math.PI * 2); return; }
                // onto the kill (the strike may have knocked it a block or two away), then mantle over it facing it
                if (meal != null && meal.World != null)
                {
                    double dx = meal.Pos.X - pos.X, dz = meal.Pos.Z - pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
                    double face = Math.Atan2(dx, dz);
                    pos.Yaw = (float)face; flyHeading = face;
                    // The kill isn't at its feet's level (it fell off a ledge, or was knocked up onto one), or it is
                    // more than a short walk away: fly over to it. In the water or far off: leave it.
                    if (Math.Abs(meal.Pos.Y - pos.Y) > 0.7 || d > 4)
                    {
                        if (meal.Swimming || meal.FeetInLiquid || d > 24 || Math.Abs(meal.Pos.Y - pos.Y) > 24) { meal = null; TakeOff(face); return; }
                        if (!meal.OnGround && stateTime < 3) return;   // still falling: wait for it to land
                        speed = 3;
                        perchSpot = MealSpot(face);
                        SetState(State.ToPerch); toMeal = true;
                        entity.PlayEntitySound("takeoffwings");
                        return;
                    }
                    if (d > MealReach + 0.25 && stateTime < 10)
                    {
                        double step = Math.Min(2.5, (d - MealReach) * 4);
                        pos.Motion.X = Math.Sin(face) * step / 60; pos.Motion.Z = Math.Cos(face) * step / 60;
                        SetAnim("walk");
                        return;
                    }
                }
                SetAnim("eat");
                return;
            }
            SetAnim("idle-stand");
            if (stateTime > stateLength) TakeOff(rand.NextDouble() * Math.PI * 2);
        }

        /// <summary>Something came too close (a shot, a hit, a player): off the perch away from it, or veer off if flying.</summary>
        private void Startle(double away)
        {
            if (state == State.Perch || state == State.Feed) { TakeOff(away); return; }
            if (state == State.Stoop) prey = null;
            NewThermal(entity.Pos.X + Math.Sin(away) * 80, entity.Pos.Z + Math.Cos(away) * 80);
            SetState(State.Glide);
        }

        private void TakeOff(double towards)
        {
            flyHeading = towards; speed = 0;
            SetState(State.Takeoff);
            entity.PlayEntitySound("takeoffwings");
            if (rand.NextDouble() < 0.5) entity.PlayEntitySound("call");
        }

        // ------------------------------------------------------------------------------------------------ air

        private void TickAir(float dt)
        {
            var pos = entity.Pos;
            double vy = 0, cap = soarSpeed;
            bool avoid = true;
            switch (state)
            {
                case State.Takeoff:
                    SetAnim("takeoff");
                    speed = Math.Min(8, speed + 6 * dt);
                    vy = 3.5;
                    if (stateTime > 1.4)
                    {
                        NewThermal(pos.X + Math.Sin(flyHeading) * 30, pos.Z + Math.Cos(flyHeading) * 30);
                        SetState(State.Soar);
                    }
                    break;

                case State.Soar:
                {
                    // circle the thermal: aim along the tangent, nudged in or out to hold the radius
                    double dx = pos.X - thermalX, dz = pos.Z - thermalZ, r = Math.Sqrt(dx * dx + dz * dz);
                    double tangent = Math.Atan2(dx, dz) + circleDir * (Math.PI / 2 + GameMath.Clamp((r - circleR) / circleR, -0.6, 0.6));
                    Steer(r > circleR * 2.5 ? Math.Atan2(-dx, -dz) : tangent, TurnRate * 0.8, dt);
                    double want = GroundAt(thermalX, thermalZ) + soarAlt;
                    vy = GameMath.Clamp((want - pos.Y) * 0.3, -1.5, 1.8);   // the thermal lifts it; no flapping needed
                    speed += GameMath.Clamp(soarSpeed - speed, -3 * dt, 3 * dt);
                    SetAnim(vy > 1.6 ? "fly-flap" : "fly-soar");
                    if ((huntCheck -= dt) <= 0)
                    {
                        huntCheck = 5;
                        prey = Saturated ? null : FindPrey();
                        if (prey != null) { SetState(State.Stoop); entity.PlayEntitySound("call"); break; }
                    }
                    if (stateTime > stateLength)
                    {
                        if (rand.NextDouble() < 0.5 && FindPerch()) SetState(State.ToPerch);
                        else
                        {
                            double a = rand.NextDouble() * Math.PI * 2, d = 60 + rand.NextDouble() * 90;
                            double nx = pos.X + Math.Sin(a) * d, nz = pos.Z + Math.Cos(a) * d;
                            if ((nx - homeX) * (nx - homeX) + (nz - homeZ) * (nz - homeZ) > territory * territory) { nx = homeX; nz = homeZ; }
                            NewThermal(nx, nz);
                            SetState(State.Glide);
                        }
                    }
                    break;
                }

                case State.Glide:
                {
                    double dx = thermalX - pos.X, dz = thermalZ - pos.Z;
                    Steer(Math.Atan2(dx, dz), TurnRate, dt);
                    speed += GameMath.Clamp(soarSpeed + 2 - speed, -3 * dt, 3 * dt);
                    double floor = GroundAt(pos.X, pos.Z) + 20;
                    vy = pos.Y > floor ? -0.6 : GameMath.Clamp((floor - pos.Y) * 0.5, 0, 3);
                    SetAnim(vy > 1.6 ? "fly-flap" : "fly-soar");
                    if (dx * dx + dz * dz < 100 || stateTime > 40) SetState(State.Soar);
                    break;
                }

                case State.Stoop:
                {
                    if (prey == null || !prey.Alive || stateTime > 20) { PullUp(); break; }
                    // aim a little ahead of where it is going; measured from the eagle's middle, not its feet
                    var tp = prey.Pos.XYZ.AddCopy(prey.Pos.Motion.X * 60 * 0.25, 0.3, prey.Pos.Motion.Z * 60 * 0.25);
                    double dx = tp.X - pos.X, dy = tp.Y - (pos.Y + 0.5), dz = tp.Z - pos.Z, d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    double h = Math.Sqrt(dx * dx + dz * dz);
                    if (!diving)
                    {
                        // Still too far out for a steep dive: close in at height, flapping hard, until the prey is no
                        // further out than it is below. A long shallow dive would run into whatever is in between.
                        Steer(Math.Atan2(dx, dz), TurnRate * 2, dt);
                        speed += GameMath.Clamp(soarSpeed * 1.6 - speed, -6 * dt, 8 * dt);
                        vy = 0;
                        SetAnim("fly-flap");
                        cap = speed;
                        if (h < -dy * 1.0 + 3 || stateTime > 12) { diving = true; entity.PlayEntitySound("call"); }
                        break;
                    }
                    // the stoop: wings swept back, straight down the line, accelerating hard; talons forward only in
                    // the last few blocks
                    flyHeading = Math.Atan2(dx, dz);
                    if (!alarmRolled && d < 14)
                    {
                        // ducks and geese often see it coming
                        alarmRolled = true;
                        if (rand.NextDouble() < SpottedChance) prey.GetBehavior<EntityBehaviorWaterfowl>()?.Alarm(entity);
                    }
                    speed = Math.Min(Math.Min(stoopSpeed, speed + 45 * dt), 7 + d * 5);
                    vy = dy / Math.Max(d, 0.01) * speed;
                    SetAnim(d < 4 ? "land" : "stoop");
                    avoid = false;
                    cap = speed * h / Math.Max(d, 0.01);           // horizontal share of the dive speed
                    if (d < 2.0 || (d < 3 && (entity.CollidedHorizontally || entity.OnGround))) { Strike(); break; }
                    if (entity.CollidedHorizontally || (prey.Pos.Y > pos.Y + 2 && h < 3)) PullUp();
                    break;
                }

                case State.ToPerch:
                {
                    if (toMeal)
                    {
                        // following its kill: it may still be sliding or falling
                        if (meal == null || meal.World == null || meal.Swimming || meal.FeetInLiquid) { toMeal = false; meal = null; NewThermal(pos.X, pos.Z); SetState(State.Takeoff); break; }
                        perchSpot = MealSpot(Math.Atan2(meal.Pos.X - pos.X, meal.Pos.Z - pos.Z));
                    }
                    double dx = perchSpot.X - pos.X, dz = perchSpot.Z - pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
                    Steer(Math.Atan2(dx, dz), TurnRate * 1.5, dt);
                    if (d > 8)
                    {
                        // fly to a point above it
                        speed += GameMath.Clamp(soarSpeed - speed, -4 * dt, 3 * dt);
                        vy = GameMath.Clamp((perchSpot.Y + 6 - pos.Y) * 0.6, -3, 3);
                        SetAnim(vy > 1.6 ? "fly-flap" : "fly-soar");
                    }
                    else
                    {
                        // drop onto it: the perch is the obstacle the look-ahead would climb over, so it is off here.
                        // Down a slope that meets the top only a block out, so it comes in over the perch's shoulders
                        // (a tree's lower branches) rather than level with the top into them.
                        avoid = false;
                        speed = Math.Max(0.8, Math.Min(speed, d * 0.9));
                        double aim = perchSpot.Y + Math.Max(0, d - 1) * 0.7;
                        vy = GameMath.Clamp((aim - pos.Y) * 0.9, -3.5, 2.5);
                        SetAnim("land");
                        // On it: nearly over the spot, or standing within reach of it with something solid under its middle.
                        // (Within 1.5 blocks alone let it stop with its feet over the edge of a wall and slip off.)
                        bool supported = entity.OnGround && SolidUnder(pos.X, pos.Y - 0.1, pos.Z);
                        bool onPerch = Math.Abs(pos.Y - perchSpot.Y) < 0.4 && (d < 0.5 || (d < 1.5 && supported));
                        // caught on something short of it: hop up and come in again
                        if (entity.OnGround && !onPerch) vy = 3;
                        else if (onPerch && (entity.OnGround || pos.Y <= perchSpot.Y + 0.3))
                        {
                            speed = 0;
                            if (toMeal || carrying) { stateLength = 15 + (float)rand.NextDouble() * 15; SetState(State.Feed); return; }
                            stateLength = 60 + (float)rand.NextDouble() * 120;
                            SetState(State.Perch);
                            return;
                        }
                    }
                    if (stateTime > 40) { NewThermal(pos.X, pos.Z); SetState(State.Soar); }
                    break;
                }
            }

            if (avoid) { AvoidTerrain(ref vy, ref cap, dt); speed = Math.Min(speed, cap); }
            if (entity.CollidedHorizontally && avoid) { speed = Math.Min(speed, MinPullSpeed); vy = SteepClimb; }
            double hs = state == State.Stoop ? cap : speed;
            pos.Motion.Set(Math.Sin(flyHeading) * hs / 60, vy / 60, Math.Cos(flyHeading) * hs / 60);
            pos.Yaw = (float)flyHeading;
            double pitch = Math.Atan2(vy, Math.Max(hs, 0.5));
            double wantRoll = -GameMath.Clamp(pitch, -1.2, 0.9);
            pos.Roll += (float)((wantRoll - pos.Roll) * Math.Min(1, dt * 5));
        }

        /// <summary>Where to stand to feed on the kill, coming at it from heading: just short of it, head over it. On the
        /// kill itself if that spot is in a wall (a kill at the foot of the ledge it fell from).</summary>
        private Vec3d MealSpot(double heading)
        {
            var spot = new Vec3d(meal.Pos.X - Math.Sin(heading) * MealReach, meal.Pos.Y, meal.Pos.Z - Math.Cos(heading) * MealReach);
            var b = entity.World.BlockAccessor.GetBlock(new BlockPos((int)Math.Floor(spot.X), (int)Math.Floor(spot.Y + 0.1), (int)Math.Floor(spot.Z)));
            return b?.CollisionBoxes != null && b.CollisionBoxes.Length > 0 ? meal.Pos.XYZ : spot;
        }

        private void Strike()
        {
            var victim = prey; prey = null;
            bool waterfowl = victim.GetBehavior<EntityBehaviorWaterfowl>() != null;
            victim.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = entity, CauseEntity = entity, Type = EnumDamageType.PiercingAttack }, 30);
            if (!victim.Alive && !(victim.Swimming || victim.FeetInLiquid))
            {
                // down on the kill, mantling over it (TickGround walks it onto the kill)
                meal = victim;
                speed = 0; stateLength = 20 + (float)rand.NextDouble() * 20;
                SetState(State.Feed);
                return;
            }
            if (waterfowl)
            {
                // the rest of the group flushes (a kill on the lake is a disturbance)
                foreach (var e in entity.World.GetEntitiesAround(victim.Pos.XYZ, 16, 8, e => e.Alive && e.Code == victim.Code))
                {
                    e.GetBehavior<EntityBehaviorWaterfowl>()?.Alarm(entity);
                    break;   // one is enough: it takes its group with it
                }
                if (!victim.Alive && FindPerch())
                {
                    // snatched off the water: carried to a perch and eaten there
                    (entity.World as IServerWorldAccessor)?.DespawnEntity(victim, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    carrying = true;
                    SetState(State.ToPerch); carrying = true;
                    return;
                }
            }
            PullUp();   // it lived, or nowhere to take it: back up
        }

        private void PullUp()
        {
            prey = null;
            NewThermal(entity.Pos.X, entity.Pos.Z);
            speed = Math.Min(speed, soarSpeed);
            SetState(State.Takeoff);
        }

        /// <summary>The nearest prey within HuntRange horizontally, at least 8 blocks below and in the open (nothing over it).</summary>
        private Entity FindPrey()
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            Entity best = null; double bestD = double.MaxValue;
            int seen = 0, high = 0, covered = 0;
            foreach (var e in entity.World.GetEntitiesAround(pos.XYZ, (float)HuntRange, 80, e => e.Alive && e != entity && IsPrey(e)))
            {
                seen++;
                if (pos.Y - e.Pos.Y < 8) { high++; continue; }
                int top = ba.GetRainMapHeightAt((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Z));
                if (top + 1 > e.Pos.Y + 1.5) { covered++; continue; }   // under trees or a roof: not seen
                double d = e.Pos.HorDistanceTo(pos.XYZ);
                if (d < bestD) { bestD = d; best = e; }
            }
            LastHunt = $"{seen} prey in range, {high} too close below, {covered} under cover, chose {(best == null ? "none" : best.Code.ToShortString())}";
            return best;
        }

        /// <summary>What the last look for prey found (diagnostics and the in-engine tests).</summary>
        public string LastHunt { get; private set; } = "no look yet";

        private static readonly AssetLocation Chickens = new("game", "chicken-*");

        private bool IsPrey(Entity e)
        {
            if (e.Code == null) return false;
            if (ResidentGeese.Config.EaglesHuntChickens && WildcardUtil.Match(Chickens, e.Code)) return true;   // server config
            foreach (var c in preyCodes) if (WildcardUtil.Match(new AssetLocation(c), e.Code)) return true;
            return false;
        }

        /// <summary>
        /// A perch: the highest commanding point within 64 blocks (sampled every 3): of the spots whose top stands at
        /// least 3 blocks above the average of the ground 4 blocks around (and isn't water), the highest, the more
        /// prominent on a tie. (By prominence alone, the edge of any cliff would beat the summit above it.) False if none.
        /// </summary>
        private bool FindPerch()
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            var cands = new List<(double prom, int x, int z, int top)>();
            int fx = (int)Math.Floor(pos.X), fz = (int)Math.Floor(pos.Z);
            for (int dx = -63; dx <= 63; dx += 3)
                for (int dz = -63; dz <= 63; dz += 3)
                {
                    int x = fx + dx, z = fz + dz;
                    int top = SolidTop(ba, x, z);
                    if (top <= 0 || ba.GetBlock(new BlockPos(x, top, z), BlockLayersAccess.Fluid)?.IsLiquid() == true) continue;
                    double around = 0; int n = 0;
                    foreach (var (ox, oz) in new[] { (4, 0), (-4, 0), (0, 4), (0, -4), (3, 3), (-3, 3), (3, -3), (-3, -3) })
                    {
                        int h = ba.GetRainMapHeightAt(x + ox, z + oz);
                        if (h > 0) { around += h; n++; }
                    }
                    if (n < 6) continue;
                    double prom = top - around / n;
                    if (prom >= 3) cands.Add((prom, x, z, top));
                }
            if (cands.Count == 0) return false;
            cands.Sort((a, b) => a.top != b.top ? b.top.CompareTo(a.top) : b.prom.CompareTo(a.prom));
            var best = cands[0];
            perchSpot = new Vec3d(best.x + 0.5, best.top + 1, best.z + 0.5);
            return true;
        }

        private bool SolidUnder(double x, double y, double z)
        {
            var b = entity.World.BlockAccessor.GetBlock(new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)));
            return b?.CollisionBoxes != null && b.CollisionBoxes.Length > 0;
        }

        /// <summary>The highest block it can stand on: the rain map's top, or the first solid block below it if that is a
        /// plant or the like (it would perch on air). 0 if none within a few blocks.</summary>
        private static int SolidTop(IBlockAccessor ba, int x, int z)
        {
            int top = ba.GetRainMapHeightAt(x, z);
            var p = new BlockPos(x, top, z);
            for (int i = 0; i < 4 && p.Y > 0; i++, p.Y--)
            {
                var b = ba.GetBlock(p);
                if (b?.CollisionBoxes != null && b.CollisionBoxes.Length > 0) return p.Y;
            }
            return 0;
        }

        private void NewThermal(double x, double z)
        {
            thermalX = x; thermalZ = z;
            circleR = CircleMin + rand.NextDouble() * (CircleMax - CircleMin);
            circleDir = rand.NextDouble() < 0.5 ? 1 : -1;
            soarAlt = soarAltMin + rand.NextDouble() * (soarAltMax - soarAltMin);
        }

        private double GroundAt(double x, double z)
        {
            int h = entity.World.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z));
            return h > 0 ? h + 1 : entity.Pos.Y - soarAlt;
        }

        private void SetState(State s)
        {
            if (s == State.Feed && state != State.Feed) fedUntilMs = entity.World.ElapsedMilliseconds + (long)(saturatedSeconds * 1000);
            state = s; stateTime = 0;
            if (s == State.Stoop) { diving = false; alarmRolled = false; }
            if (s != State.ToPerch && s != State.Feed) carrying = false;
            toMeal = false;      // set again by whoever starts a flight to the kill
            if (s != State.Feed && s != State.ToPerch) meal = null;
            if (s == State.Soar) { stateLength = 60 + (float)rand.NextDouble() * 90; huntCheck = 3; }
            Persist();
        }

        // ------------------------------------------------------------------------------------------------ saving

        private void Persist()
        {
            var t = new TreeAttribute();
            t.SetInt("state", (int)state);
            t.SetDouble("homeX", homeX); t.SetDouble("homeZ", homeZ);
            entity.WatchedAttributes[StateKey] = t;
        }

        private void Restore()
        {
            if (entity.WatchedAttributes[StateKey] is ITreeAttribute t)
            {
                homeX = t.GetDouble("homeX"); homeZ = t.GetDouble("homeZ");
                var s = (State)t.GetInt("state");
                // anything in the air carries on soaring from here; a perched or feeding bird stays put
                state = s == State.Perch || s == State.Feed ? State.Perch : State.Soar;
            }
            else
            {
                // new: perched if it stands on something (OnGround isn't set before the first physics tick)
                homeX = entity.Pos.X; homeZ = entity.Pos.Z;
                var below = entity.World.BlockAccessor.GetBlock(new BlockPos((int)Math.Floor(entity.Pos.X), (int)Math.Floor(entity.Pos.Y - 0.2), (int)Math.Floor(entity.Pos.Z)));
                state = entity.OnGround || (below != null && below.Id != 0 && !below.IsLiquid()) ? State.Perch : State.Soar;
            }
            NewThermal(entity.Pos.X, entity.Pos.Z);
            stateLength = state == State.Perch ? 20 + (float)rand.NextDouble() * 60 : 60;
            flyHeading = entity.Pos.Yaw; speed = state == State.Soar ? soarSpeed : 0;
            Persist();
        }

        /// <summary>Let out of a cage somewhere new (EntityBehaviorBird.OnEntitySpawn): no old state, so the first tick
        /// starts it as a new bird here.</summary>
        protected override void Relocated() => entity.WatchedAttributes.RemoveAttribute(StateKey);

        public override void OnEntityLoaded()
        {
            if (entity.WatchedAttributes[StateKey] is ITreeAttribute t)
            {
                var s = (State)t.GetInt("state");
                if (s != State.Perch && s != State.Feed) entity.SimulationRange = FlightRange;
            }
        }

        // ------------------------------------------------------------------------------------------------ damage

        public override void OnEntityReceiveDamage(DamageSource source, ref float damage)
        {
            if (entity.World.Side != EnumAppSide.Server || damage <= 0 || source == null) return;
            var from = source.GetCauseEntity() ?? source.SourceEntity;
            double away = from != null ? Math.Atan2(entity.Pos.X - from.Pos.X, entity.Pos.Z - from.Pos.Z) : rand.NextDouble() * Math.PI * 2;
            if (Dodges(source)) { damage = 0; Startle(away); return; }
            if (from != null) entity.World.RegisterCallback(_ => { if (entity.Alive) Startle(away); }, 1);
        }
    }
}
