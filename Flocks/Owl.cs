using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Flocks
{
    /// <summary>
    /// Server-side behaviour for tawny owls (flocks:owl), birds of the woods that live in one territory all year.
    /// By day an owl roosts in a tree, on a branch deep in the crown near the trunk, hidden in the leaves, and sits
    /// tight: only someone coming very close flushes it, and it flies to another tree. At dusk it moves out to a hunting
    /// perch in the open, on the edge of a crown, a fence post or a wall, over open ground, and watches; when a mouse (preyCodes) is in
    /// reach it glides down onto it, silently, talons first, and swallows it there. Fed, it doesn't hunt again for a
    /// while (vanilla's "saturated"). It changes hunting perches every few minutes, hoots and calls through the night, and
    /// goes back to roost at dawn.
    /// Trees: leaves have no collision box in this game, but the branches inside a crown are branchy leaves, which do.
    /// A perch is the first block with a collision box under a column's leaves (a branch, or the trunk's top), so the owl
    /// can always drop onto it straight down through the foliage (PerchBelowLeaves). Branchy leaves are drawn as leaf
    /// blocks, so an owl there can't be seen: right for a roost, not for hunting, which it does from the open.
    /// Owl flight is silent: no wingbeat sounds. Damage: arrows and melee hurt; thrown spears are dodged (EntityBehaviorBird).
    /// </summary>
    public class EntityBehaviorOwl : EntityBehaviorBird
    {
        public const string Code = "flocks.owl";
        private const string StateKey = "flocksOwl";
        private enum State { Roost, Perch, Takeoff, Fly, Land, Pounce, Feed }
        private enum Kind { Roost, Hunt }

        // tuning (entity attributes)
        private float roostTakeoffRange = 4, nightTakeoffRange = 9, territory = 48;
        private double flySpeed = 7, pounceSpeed = 9, huntRange = 18, perchMin = 60, perchMax = 180, saturatedSeconds = 1200;
        private string[] preyCodes = { "flocks:mouse" };

        /// <summary>Out at DuskAt, back at DawnAt (game hours): after the crows have gone to roost, before they wake.</summary>
        public const double DuskAt = 19.5, DawnAt = 5.5;
        /// <summary>Perches are looked for within SearchRange; a flushed owl goes at least SafeRoost from the threat, and a
        /// new hunting perch is at least MinHop from the old one.</summary>
        private const double SearchRange = 32, SafeRoost = 16, MinHop = 6;

        private State state = State.Roost;
        private float stateTime, stateLength = 60, shotCheck, nextCheck, huntCheck = 2, callIn = 10, hootFor;
        private bool restored;
        private Kind kind = Kind.Roost;
        private double homeX, homeZ, senseMul = 1;
        private Vec3d target, roost;
        private Entity prey, meal;
        private long fedUntilMs;

        public EntityBehaviorOwl(Entity entity) : base(entity) { }
        public override string PropertyName() => Code;
        protected override Vec3d Destination => target;
        protected override float AnimSpeed(string code) => code switch { "takeoff" => 1.8f, "fly-flap" => 1.1f, "land" => 1.2f, _ => 1f };

        // ---- for diagnostics and the in-engine tests
        public string StateName => state.ToString();
        public bool Saturated => entity.World.ElapsedMilliseconds < fedUntilMs;
        public Vec3d Target => target;
        public Vec3d RoostSpot => roost;
        public Entity Prey => prey;
        /// <summary>Mice caught.</summary>
        public int Catches { get; private set; }
        public int Pounces { get; private set; }
        /// <summary>Hoots and calls made.</summary>
        public int CallsMade { get; private set; }
        protected override string StateText => $"{state}{(Saturated ? ", fed" : "")}, {(Night() ? "night: hunting" : "day: roosting")}, takes off within {(state == State.Roost ? roostTakeoffRange : nightTakeoffRange):0} (times your sensing)";

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            roostTakeoffRange = attributes["roostTakeoffRange"].AsFloat(roostTakeoffRange);
            nightTakeoffRange = attributes["nightTakeoffRange"].AsFloat(nightTakeoffRange);
            territory = attributes["territory"].AsFloat(territory);
            flySpeed = attributes["flySpeed"].AsDouble(flySpeed);
            pounceSpeed = attributes["pounceSpeed"].AsDouble(pounceSpeed);
            huntRange = attributes["huntRange"].AsDouble(huntRange);
            perchMin = attributes["perchSecondsMin"].AsDouble(perchMin);
            perchMax = attributes["perchSecondsMax"].AsDouble(perchMax);
            saturatedSeconds = attributes["saturatedSeconds"].AsDouble(saturatedSeconds);
            var codes = attributes["preyCodes"].AsArray<string>(null);
            if (codes != null) preyCodes = codes;
        }

        private bool Night() => Night(entity.World);
        public static bool Night(IWorldAccessor world)
        {
            double h = world.Calendar.HourOfDay;
            return h >= DuskAt || h < DawnAt;
        }

        public override void OnGameTick(float dt)
        {
            if (!OnServer || entity.Alive && HoldIfFrozen()) return;   // HoldIfFrozen: /flocks lineup
            if (!entity.Alive) { entity.Pos.Roll = 0; return; }
            if (!restored) { restored = true; Restore(); }
            LetSpearsPass(dt);
            TrackPosition(dt);
            IdleCalls();
            bool sitting = state == State.Roost || state == State.Perch || state == State.Feed;
            entity.SimulationRange = sitting ? GlobalConstants.DefaultSimulationRange : FlightRange;
            stateTime += dt;

            if ((shotCheck -= dt) <= 0)
            {
                shotCheck = 0.1f;
                if (IncomingShot(out double shotAway, out Entity shooter, out Vec3d from)) { Flush(shooter?.Pos.XYZ ?? from, shotAway); return; }
            }
            if (!sitting) { TickAir(dt); return; }

            // someone close (measured along the ground: a roost is up in a tree): by day it sits tight until they are
            // nearly under it; at night it is warier
            if ((nextCheck -= dt) <= 0)
            {
                nextCheck = 0.25f;
                float range = state == State.Roost ? roostTakeoffRange : nightTakeoffRange;
                var p = NearestPlayer(Math.Max(range * 3, 24), out senseMul);
                if (p != null && p.Pos.HorDistanceTo(entity.Pos.XYZ) < range * senseMul)
                {
                    Flush(p.Pos.XYZ, Math.Atan2(entity.Pos.X - p.Pos.X, entity.Pos.Z - p.Pos.Z));
                    return;
                }
            }
            if (state == State.Feed) TickFeed(dt); else TickSit(dt);
        }

        // ------------------------------------------------------------------------------------------------ sitting

        private void TickSit(float dt)
        {
            var pos = entity.Pos;
            pos.Motion.X = 0; pos.Motion.Z = 0; pos.Roll = 0;
            // hold on: a perch in the leaves (no collision box) carries it no more than it does a crow
            if (target != null) pos.Motion.Y = GameMath.Clamp((target.Y - pos.Y) * 0.2, -0.05, 0.05);
            bool night = Night();
            if (state == State.Roost)
            {
                SetAnim("idle-stand");
                if (night && stateTime > 2) GoHunt();
                return;
            }
            // hunting perch, at night
            if (!night) { GoRoost(); return; }
            Calls(dt);
            SetAnim(hootFor > 0 ? "hoot" : "idle-stand");
            if (!Saturated && (huntCheck -= dt) <= 0)
            {
                huntCheck = 1.5f;
                prey = FindPrey();
                if (prey != null) { Pounces++; SetState(State.Pounce); speed = 2; return; }
            }
            if (stateTime > stateLength) GoHunt();
        }

        /// <summary>Night calls from the hunting perch: mostly the hoot, sometimes the sharp "ke-wick".</summary>
        private void Calls(float dt)
        {
            if (hootFor > 0) hootFor -= dt;
            if ((callIn -= dt) > 0) return;
            callIn = 20 + (float)rand.NextDouble() * 55;
            CallsMade++;
            if (rand.NextDouble() < 0.75) { entity.PlayEntitySound("hoot"); hootFor = 5; }
            else entity.PlayEntitySound("kewick");
        }

        /// <summary>On the ground over its catch: swallows it whole, then back up to a perch.</summary>
        private void TickFeed(float dt)
        {
            var pos = entity.Pos;
            pos.Motion.X = 0; pos.Motion.Z = 0; pos.Roll = 0;
            SetAnim("eat");
            if (meal != null && meal.World != null)
                pos.Yaw = (float)(flyHeading = Math.Atan2(meal.Pos.X - pos.X, meal.Pos.Z - pos.Z));
            if (stateTime < stateLength) return;
            if (meal != null && meal.World != null)
                (entity.World as IServerWorldAccessor)?.DespawnEntity(meal, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            meal = null;
            if (Night()) GoHunt(); else GoRoost();
        }

        // ------------------------------------------------------------------------------------------------ going places

        /// <summary>Off to a hunting perch near here (another one if it is on one already).</summary>
        private void GoHunt()
        {
            var from = entity.Pos.XYZ;
            var spot = FindPerch(Kind.Hunt, from, from.X, from.Z, 0, state == State.Perch ? MinHop : 0);
            if (spot == null) { stateLength = (float)(perchMin + rand.NextDouble() * (perchMax - perchMin)); if (state == State.Roost) SetState(State.Perch); return; }
            TakeOff(spot, Kind.Hunt);
        }

        /// <summary>Back to its roost at dawn (or another one if that is gone).</summary>
        private void GoRoost()
        {
            var ba = entity.World.BlockAccessor;
            if (roost == null || !RoostStill(ba, roost)) roost = FindPerch(Kind.Roost, new Vec3d(homeX, entity.Pos.Y, homeZ), homeX, homeZ, 0, 0);
            if (roost == null) { roost = entity.Pos.XYZ; SetState(State.Roost); return; }
            if (entity.Pos.XYZ.HorizontalSquareDistanceTo(roost.X, roost.Z) < 0.5 && Math.Abs(entity.Pos.Y - roost.Y) < 0.5) { SetState(State.Roost); return; }
            TakeOff(roost.Clone(), Kind.Roost);
        }

        /// <summary>Something too close, a shot or a hit: off to another tree away from it.</summary>
        private void Flush(Vec3d threat, double away)
        {
            if (state == State.Takeoff || state == State.Fly || state == State.Land) return;
            double tx = threat?.X ?? entity.Pos.X - Math.Sin(away) * 10, tz = threat?.Z ?? entity.Pos.Z - Math.Cos(away) * 10;
            prey = null; meal = null;
            var k = Night() ? Kind.Hunt : Kind.Roost;
            var spot = FindPerch(k, entity.Pos.XYZ, tx, tz, SafeRoost, MinHop)
                       ?? FindPerch(k == Kind.Roost ? Kind.Hunt : Kind.Roost, entity.Pos.XYZ, tx, tz, SafeRoost, MinHop);
            if (spot == null)
            {
                // nowhere: well away along the line from the threat, down on whatever is there
                double x = entity.Pos.X + Math.Sin(away) * 30, z = entity.Pos.Z + Math.Cos(away) * 30;
                int top = entity.World.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(x), (int)Math.Floor(z));
                spot = new Vec3d(x, top > 0 ? top + 1 : entity.Pos.Y, z);
            }
            if (k == Kind.Roost) roost = spot.Clone();
            if (rand.NextDouble() < 0.4) entity.PlayEntitySound("alarm");
            TakeOff(spot, k);
        }

        private void TakeOff(Vec3d spot, Kind k)
        {
            target = spot; kind = k; speed = 0;
            flyHeading = Math.Atan2(spot.X - entity.Pos.X, spot.Z - entity.Pos.Z);
            SetState(State.Takeoff);
        }

        // ------------------------------------------------------------------------------------------------ air

        private void TickAir(float dt)
        {
            var pos = entity.Pos;
            double vy = 0, cap = flySpeed;
            bool avoid = true;
            switch (state)
            {
                case State.Takeoff:
                {
                    SetAnim("takeoff");
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 2, dt);
                    // From a roost inside a crown: straight up through the leaves first (only leaves lie over a roost,
                    // which is how it got in), so it doesn't fly into the trunk or a branch on the way out.
                    int over = entity.World.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(pos.X), (int)Math.Floor(pos.Z));
                    if (over + 1 > pos.Y + 0.1 && stateTime < 6)
                    {
                        speed = 0; vy = 3.5; avoid = false;
                        break;
                    }
                    speed = Math.Min(4, speed + 7 * dt);
                    vy = 3;
                    if (stateTime > 0.6) SetState(State.Fly);
                    break;
                }

                case State.Fly:
                {
                    double d = HorDist(target);
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 1.8, dt);
                    speed += GameMath.Clamp(flySpeed - speed, -4 * dt, 4 * dt);
                    // low over the ground, rising to clear the crown it is making for
                    double want = Math.Max(Above(target) + GameMath.Clamp(d * 0.25, 0.5, 6), GroundAhead() + 2.5);
                    vy = GameMath.Clamp((want - pos.Y) * 0.9, -2.5, 3.5);
                    SetAnim(vy < -0.8 ? "fly-soar" : "fly-flap");
                    if (d < 3.5) SetState(State.Land);
                    else if (stateTime > 40) Replan();
                    break;
                }

                case State.Land:
                {
                    // over the spot, above everything solid around it, then straight down through the leaves onto it
                    avoid = false;
                    double d = HorDist(target);
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 3, dt);
                    if (d > 0.45)
                    {
                        speed = GameMath.Clamp(d * 1.8, 0.5, Math.Max(speed, 0.5));
                        vy = GameMath.Clamp((Above(target) + 0.3 - pos.Y) * 2.5, -2.5, 3);
                        SetAnim("fly-flap");
                    }
                    else
                    {
                        speed = d * 3;
                        vy = GameMath.Clamp((target.Y - pos.Y) * 2.5, -3, 1.5);
                        SetAnim("land");
                        if (Math.Abs(pos.Y - target.Y) < 0.3 || (entity.OnGround && pos.Y - target.Y < 0.6)) { Arrive(); return; }
                    }
                    if (entity.CollidedHorizontally) vy = 3;   // caught on a branch: up and over
                    if (stateTime > 14) Replan();
                    break;
                }

                case State.Pounce:
                {
                    if (prey == null || !prey.Alive || prey.World == null || stateTime > 8) { Missed(); return; }
                    // straight down the line, a little ahead of where it is going; talons forward at the end
                    avoid = false;
                    var tp = prey.Pos.XYZ.AddCopy(prey.Pos.Motion.X * 60 * 0.2, 0.1, prey.Pos.Motion.Z * 60 * 0.2);
                    double dx = tp.X - pos.X, dy = tp.Y - pos.Y, dz = tp.Z - pos.Z;
                    double h = Math.Sqrt(dx * dx + dz * dz), d = Math.Sqrt(h * h + dy * dy);
                    flyHeading = Math.Atan2(dx, dz);
                    speed = Math.Min(pounceSpeed, speed + 10 * dt);
                    speed = Math.Min(speed, 2 + d * 3);
                    vy = dy / Math.Max(d, 0.01) * speed;
                    cap = speed * h / Math.Max(d, 0.01);
                    SetAnim(d > 3 ? "fly-soar" : "land");
                    if (h < 0.9 && Math.Abs(dy) < 1.0 || (entity.OnGround && h < 1.3)) { Strike(); return; }
                    if (entity.CollidedHorizontally || (entity.OnGround && stateTime > 1.5)) { Missed(); return; }
                    break;
                }
            }
            if (avoid) { AvoidTerrain(ref vy, ref cap, dt); speed = Math.Min(speed, cap); }
            if (entity.CollidedHorizontally && avoid) { speed = Math.Min(speed, MinPullSpeed); vy = SteepClimb; }
            double hs = state == State.Pounce ? cap : speed;
            pos.Motion.Set(Math.Sin(flyHeading) * hs / 60, vy / 60, Math.Cos(flyHeading) * hs / 60);
            pos.Yaw = (float)flyHeading;
            double pitch = Math.Atan2(vy, Math.Max(hs, 0.5));
            pos.Roll += (float)((-GameMath.Clamp(pitch, -0.8, 0.9) - pos.Roll) * Math.Min(1, dt * 5));
        }

        /// <summary>Down on its spot: roosting by day, watching from a hunting perch at night.</summary>
        private void Arrive()
        {
            var pos = entity.Pos;
            speed = 0; pos.Roll = 0; pos.Motion.Set(0, 0, 0);
            stateLength = (float)(perchMin + rand.NextDouble() * (perchMax - perchMin));
            huntCheck = 1;
            SetState(kind == Kind.Roost ? State.Roost : State.Perch);
        }

        /// <summary>The spot can't be reached: another, or the ground below.</summary>
        private void Replan()
        {
            var spot = FindPerch(kind, entity.Pos.XYZ, entity.Pos.X, entity.Pos.Z, 0, 2);
            if (spot == null || spot.SquareDistanceTo(target) < 1)
            {
                int top = entity.World.BlockAccessor.GetRainMapHeightAt((int)Math.Floor(entity.Pos.X), (int)Math.Floor(entity.Pos.Z));
                spot = new Vec3d(entity.Pos.X, top > 0 ? top + 1 : entity.Pos.Y, entity.Pos.Z);
            }
            target = spot;
            SetState(State.Fly);
        }

        private void Strike()
        {
            var victim = prey; prey = null;
            victim.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Entity, SourceEntity = entity, CauseEntity = entity, Type = EnumDamageType.PiercingAttack }, 10);
            if (victim.Alive) { Missed(); return; }
            Catches++;
            meal = victim;
            entity.Pos.Motion.Set(0, entity.Pos.Motion.Y, 0);
            speed = 0;
            stateLength = 6 + (float)rand.NextDouble() * 4;
            fedUntilMs = entity.World.ElapsedMilliseconds + (long)(saturatedSeconds * 1000);
            SetState(State.Feed);
        }

        /// <summary>The mouse got away (or something was in the way): back up to a perch.</summary>
        private void Missed()
        {
            prey = null;
            var spot = FindPerch(Kind.Hunt, entity.Pos.XYZ, entity.Pos.X, entity.Pos.Z, 0, 0);
            if (spot == null) { Replan(); return; }
            TakeOff(spot, Kind.Hunt);
        }

        /// <summary>The height to come over a spot at: above every solid block in the 3x3 columns around it (the rain
        /// map's top, leaves included), and never below the spot itself.</summary>
        private double Above(Vec3d spot)
        {
            var ba = entity.World.BlockAccessor;
            int sx = (int)Math.Floor(spot.X), sz = (int)Math.Floor(spot.Z), top = 0;
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++) top = Math.Max(top, ba.GetRainMapHeightAt(sx + dx, sz + dz));
            return Math.Max(spot.Y, top + 1.2);
        }

        /// <summary>The highest ground or tree top within 16 blocks straight ahead (0 if none is loaded).</summary>
        private double GroundAhead()
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            double ground = 0;
            for (int d = 0; d <= 16; d += 4)
            {
                int h = ba.GetRainMapHeightAt((int)Math.Floor(pos.X + Math.Sin(flyHeading) * d), (int)Math.Floor(pos.Z + Math.Cos(flyHeading) * d));
                if (h > 0) ground = Math.Max(ground, h + 1);
            }
            return ground;
        }

        // ------------------------------------------------------------------------------------------------ prey

        /// <summary>The nearest mouse within huntRange, below the perch, with nothing solid between (leaves don't count:
        /// owls hunt by ear as much as by eye).</summary>
        private Entity FindPrey()
        {
            var pos = entity.Pos;
            Entity best = null; double bestD = double.MaxValue;
            foreach (var e in entity.World.GetEntitiesAround(pos.XYZ, (float)huntRange, 16, e => e.Alive && e != entity && IsPrey(e)))
            {
                if (pos.Y - e.Pos.Y < 1.5) continue;
                double d = e.Pos.HorDistanceTo(pos.XYZ);
                if (d > huntRange || d >= bestD) continue;
                if (!LineClear(pos.XYZ.AddCopy(0, 0.4, 0), e.Pos.XYZ.AddCopy(0, 0.2, 0))) continue;
                best = e; bestD = d;
            }
            return best;
        }

        private bool IsPrey(Entity e)
        {
            if (e.Code == null) return false;
            foreach (var c in preyCodes) if (WildcardUtil.Match(new AssetLocation(c), e.Code)) return true;
            return false;
        }

        /// <summary>No block with a collision box on the straight line from a to b (past the first 0.8 blocks, which is
        /// the owl's own perch).</summary>
        private bool LineClear(Vec3d a, Vec3d b)
        {
            var ba = entity.World.BlockAccessor; var tmp = new BlockPos(0, 0, 0);
            double len = a.DistanceTo(b);
            for (double t = 0.8; t < len - 0.3; t += 0.4)
            {
                double f = t / len;
                tmp.Set((int)Math.Floor(a.X + (b.X - a.X) * f), (int)Math.Floor(a.Y + (b.Y - a.Y) * f), (int)Math.Floor(a.Z + (b.Z - a.Z) * f));
                var bl = ba.GetBlock(tmp);
                if (bl?.CollisionBoxes != null && bl.CollisionBoxes.Length > 0) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------------ finding perches

        /// <summary>
        /// A perch within SearchRange of from and the territory, at least safe from the threat (tx, tz) and minHop from
        /// from. Roost: a branch (or the trunk's top) under at least two blocks of leaves, near the trunk, the better the
        /// more cover: hidden; failing that a tree top. Hunt: in the open over open ground, where it can be seen: on top of
        /// a crown's edge 2-10 blocks up (holding its height on the leaves, as crows do), a fence post, or a wall or crag
        /// top. One of the best few, so it moves about.
        /// Feet position (on top of the block). Null if none.
        /// </summary>
        private Vec3d FindPerch(Kind k, Vec3d from, double tx, double tz, double safe, double minHop)
        {
            var ba = entity.World.BlockAccessor;
            int fx = (int)Math.Floor(from.X), fz = (int)Math.Floor(from.Z);
            var found = new List<(double score, Vec3d at)>();
            var fallback = new List<(double score, Vec3d at)>();
            var tmp = new BlockPos(0, 0, 0);
            for (int dx = -(int)SearchRange; dx <= SearchRange; dx++)
                for (int dz = -(int)SearchRange; dz <= SearchRange; dz++)
                {
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > SearchRange || d < minHop) continue;
                    int x = fx + dx, z = fz + dz;
                    if ((x + 0.5 - tx) * (x + 0.5 - tx) + (z + 0.5 - tz) * (z + 0.5 - tz) < safe * safe) continue;
                    if ((x + 0.5 - homeX) * (x + 0.5 - homeX) + (z + 0.5 - homeZ) * (z + 0.5 - homeZ) > territory * territory) continue;
                    double s = Score(ba, x, z, k, tmp, out int y, out bool fallbackOnly);
                    if (double.IsNaN(s)) continue;
                    var at = new Vec3d(x + 0.5, y + 1, z + 0.5);
                    (fallbackOnly ? fallback : found).Add((s - d * 0.08, at));
                }
            var list = found.Count > 0 ? found : fallback;
            if (list.Count == 0) return null;
            list.Sort((p, q) => q.score.CompareTo(p.score));
            return list[rand.Next(Math.Min(4, list.Count))].at;
        }

        /// <summary>How good column (x, z) is as a perch of the kind (higher is better), NaN if it isn't one; y: the block
        /// to stand on. fallbackOnly: a tree top for a roost, used only when there is no branch.</summary>
        private static double Score(IBlockAccessor ba, int x, int z, Kind k, BlockPos tmp, out int y, out bool fallbackOnly)
        {
            fallbackOnly = false;
            if (!PerchBelowLeaves(ba, x, z, tmp, out y, out int top, out bool branch)) return double.NaN;
            int cover = top - y;                                     // leaf blocks over its head
            if (k == Kind.Roost)
            {
                if (branch && cover >= 2) return 10 + Math.Min(cover, 5) + (NearTrunk(ba, x, y, z, tmp) ? 6 : 0);
                // no branch: the top of a tree's crown will do
                fallbackOnly = true;
                if (!IsTreeTop(ba, x, z, top, tmp)) return double.NaN;
                y = top;
                return 2;
            }
            // Hunting: in the open, where it can be seen and can see (Dright: hidden by day, visible at night). Not on a
            // branch inside the crown: branchy leaves are drawn as leaf blocks, and an owl there is hidden.
            int open = OpenAround(ba, x, z);
            if (open < 2) return double.NaN;
            tmp.Set(x, top, z);
            var tb = ba.GetBlock(tmp);
            if (tb?.BlockMaterial == EnumBlockMaterial.Leaves || tb?.Code?.Path?.StartsWith("leavesbranchy") == true)
            {
                // on the outside of a crown, at its edge over open ground, 2-10 blocks up
                int up = top - GroundBelow(ba, x, top, z, tmp);
                if (up < 2 || up > 10 || !CrownEdge(ba, x, z, top)) return double.NaN;
                y = top;
                return 14 + open - Math.Abs(up - 5) * 0.3;
            }
            // a fence post, or the top of a wall, a pillar or a boulder
            if (!branch && cover == 0 && (IsFence(ba, x, y, z, tmp) || Prominent(ba, x, z, y))) return 8 + open;
            return double.NaN;
        }

        /// <summary>
        /// The block an owl coming straight down column (x, z) lands on: the first from the top with a collision box,
        /// through leaves and plants only. top: the rain map's top there. branch: it is a branch inside a crown (branchy
        /// leaves, or wood with leaves above it). False over water, or if nothing is loaded.
        /// </summary>
        public static bool PerchBelowLeaves(IBlockAccessor ba, int x, int z, BlockPos tmp, out int y, out int top, out bool branch)
        {
            y = 0; branch = false;
            top = ba.GetRainMapHeightAt(x, z);
            if (top <= 0) return false;
            for (int yy = top; yy > top - 40 && yy > 0; yy--)
            {
                tmp.Set(x, yy, z);
                if (ba.GetBlock(tmp, BlockLayersAccess.Fluid)?.IsLiquid() == true) return false;
                var b = ba.GetBlock(tmp);
                if (b == null) return false;
                if (b.CollisionBoxes == null || b.CollisionBoxes.Length == 0) continue;
                y = yy;
                branch = yy < top && (b.Code?.Path?.StartsWith("leavesbranchy") == true || b.BlockMaterial == EnumBlockMaterial.Wood);
                return true;
            }
            return false;
        }

        /// <summary>The column is at the edge of a crown: a neighbouring column's top is 2 or more blocks lower.</summary>
        private static bool CrownEdge(IBlockAccessor ba, int x, int z, int top)
        {
            foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int h = ba.GetRainMapHeightAt(x + ox, z + oz);
                if (h > 0 && h <= top - 2) return true;
            }
            return false;
        }

        private static bool IsFence(IBlockAccessor ba, int x, int y, int z, BlockPos tmp)
        {
            tmp.Set(x, y, z);
            string path = ba.GetBlock(tmp)?.Code?.Path;
            return path != null && path.StartsWith("fence") && !path.StartsWith("fencegate");
        }

        /// <summary>The first solid non-tree block under y: the ground.</summary>
        private static int GroundBelow(IBlockAccessor ba, int x, int y, int z, BlockPos tmp)
        {
            for (int yy = y - 1; yy > y - 40 && yy > 0; yy--)
            {
                tmp.Set(x, yy, z);
                var b = ba.GetBlock(tmp);
                if (b == null || b.CollisionBoxes == null || b.CollisionBoxes.Length == 0) continue;
                if (b.BlockMaterial == EnumBlockMaterial.Leaves || b.BlockMaterial == EnumBlockMaterial.Wood || b.Code?.Path?.StartsWith("leavesbranchy") == true) continue;
                return yy;
            }
            return y;
        }

        private static bool NearTrunk(IBlockAccessor ba, int x, int y, int z, BlockPos tmp)
        {
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        tmp.Set(x + dx, y + dy, z + dz);
                        var b = ba.GetBlock(tmp);
                        if (b?.BlockMaterial == EnumBlockMaterial.Wood && b.Code?.Path?.StartsWith("log") == true) return true;
                    }
            return false;
        }

        /// <summary>Of 12 columns 3-8 blocks around, how many are open ground (soil, sand, gravel; nothing over them).</summary>
        private static int OpenAround(IBlockAccessor ba, int x, int z)
        {
            int n = 0;
            foreach (int r in new[] { 3, 6, 8 })
                foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    if (EntityBehaviorCorvid.OpenGround(ba, x + ox * r, z + oz * r, out _)) n++;
            return n;
        }

        private static bool IsTreeTop(IBlockAccessor ba, int x, int z, int top, BlockPos tmp)
        {
            tmp.Set(x, top, z);
            if (ba.GetBlock(tmp)?.BlockMaterial != EnumBlockMaterial.Leaves) return false;
            for (int y = top - 1; y > top - 30 && y > 0; y--)
            {
                tmp.Set(x, y, z);
                var b = ba.GetBlock(tmp);
                if (b == null || b.Id == 0 || b.BlockMaterial == EnumBlockMaterial.Leaves || b.BlockMaterial == EnumBlockMaterial.Wood
                    || b.CollisionBoxes == null || b.CollisionBoxes.Length == 0) continue;
                return top - y >= 4;
            }
            return false;
        }

        /// <summary>A solid top standing 2 or more blocks over the ground around it and over at least two of its four
        /// neighbours (a wall, a pillar, a boulder: not merely the edge of a cliff).</summary>
        private static bool Prominent(IBlockAccessor ba, int x, int z, int y)
        {
            int sides = 0;
            foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (ba.GetRainMapHeightAt(x + ox, z + oz) <= y - 2) sides++;
            if (sides < 2) return false;
            double around = 0; int n = 0;
            foreach (var (ox, oz) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3), (2, 2), (-2, 2), (2, -2), (-2, -2) })
            {
                int h = ba.GetRainMapHeightAt(x + ox, z + oz);
                if (h > 0) { around += h; n++; }
            }
            return n >= 6 && y - around / n >= 2;
        }

        /// <summary>The roost is still a roost (nobody cut the tree down or built over it).</summary>
        private bool RoostStill(IBlockAccessor ba, Vec3d at)
        {
            var tmp = new BlockPos(0, 0, 0);
            return PerchBelowLeaves(ba, (int)Math.Floor(at.X), (int)Math.Floor(at.Z), tmp, out int y, out _, out _) && y + 1 == (int)Math.Round(at.Y);
        }

        /// <summary>Whether a tawny owl could live here: a roost (a branch under leaves) within 24 blocks (spawning).</summary>
        public static bool HasRoostNear(IBlockAccessor ba, Vec3d pos)
        {
            var tmp = new BlockPos(0, 0, 0);
            int fx = (int)Math.Floor(pos.X), fz = (int)Math.Floor(pos.Z);
            for (int dx = -24; dx <= 24; dx += 2)
                for (int dz = -24; dz <= 24; dz += 2)
                    if (PerchBelowLeaves(ba, fx + dx, fz + dz, tmp, out int y, out int top, out bool branch) && branch && top - y >= 2) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------------ saving

        private void SetState(State s)
        {
            state = s; stateTime = 0;
            if (s != State.Feed) meal = null;
            if (s != State.Pounce) prey = null;
            Persist();
        }

        private void Persist()
        {
            var t = new TreeAttribute();
            t.SetInt("state", (int)state); t.SetInt("kind", (int)kind);
            t.SetDouble("homeX", homeX); t.SetDouble("homeZ", homeZ);
            if (target != null) { t.SetDouble("tx", target.X); t.SetDouble("ty", target.Y); t.SetDouble("tz", target.Z); }
            if (roost != null) { t.SetDouble("rx", roost.X); t.SetDouble("ry", roost.Y); t.SetDouble("rz", roost.Z); }
            entity.WatchedAttributes[StateKey] = t;
        }

        private void Restore()
        {
            if (entity.WatchedAttributes[StateKey] is ITreeAttribute t)
            {
                homeX = t.GetDouble("homeX"); homeZ = t.GetDouble("homeZ");
                kind = (Kind)t.GetInt("kind");
                target = t.HasAttribute("tx") ? new Vec3d(t.GetDouble("tx"), t.GetDouble("ty"), t.GetDouble("tz")) : null;
                roost = t.HasAttribute("rx") ? new Vec3d(t.GetDouble("rx"), t.GetDouble("ry"), t.GetDouble("rz")) : null;
                var s = (State)t.GetInt("state");
                // in the air (or after a catch): carry on to its spot; sitting: stay
                if (s == State.Roost || s == State.Perch) state = s;
                else if (target != null) { state = State.Fly; speed = flySpeed; }
                else state = State.Perch;
                flyHeading = entity.Pos.Yaw;
                stateLength = (float)(perchMin + rand.NextDouble() * (perchMax - perchMin));
                Persist();
                return;
            }
            // new: its territory is here; off to roost by day, or to hunt at night
            homeX = entity.Pos.X; homeZ = entity.Pos.Z;
            flyHeading = entity.Pos.Yaw;
            target = entity.Pos.XYZ;
            state = State.Perch;
            if (Night()) { stateLength = 0; Persist(); return; }    // TickSit moves it to a hunting perch
            roost = FindPerch(Kind.Roost, entity.Pos.XYZ, homeX, homeZ, 0, 0);
            if (roost != null) TakeOff(roost.Clone(), Kind.Roost);
            else { state = State.Roost; Persist(); }
        }

        /// <summary>Let out of a cage somewhere new (EntityBehaviorBird.OnEntitySpawn): no old state, so the first tick
        /// starts it as a new bird here.</summary>
        protected override void Relocated() => entity.WatchedAttributes.RemoveAttribute(StateKey);

        public override void OnEntityLoaded()
        {
            if (entity.WatchedAttributes[StateKey] is ITreeAttribute t)
            {
                var s = (State)t.GetInt("state");
                if (s != State.Roost && s != State.Perch && s != State.Feed) entity.SimulationRange = FlightRange;
            }
        }

        // ------------------------------------------------------------------------------------------------ damage

        public override void OnEntityReceiveDamage(DamageSource source, ref float damage)
        {
            if (damage <= 0 || source == null || !OnServer) return;
            var from = AttackerAndAway(source, out double away);
            if (Dodges(source)) { damage = 0; Flush(from?.Pos.XYZ, away); return; }
            if (from != null) entity.World.RegisterCallback(_ => { if (entity.Alive) Flush(from.Pos.XYZ, away); }, 1);
        }
    }
}
