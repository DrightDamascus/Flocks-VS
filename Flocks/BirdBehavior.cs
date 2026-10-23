using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// What every Flocks bird behaviour shares (server side): flight state (heading, speed), terrain avoidance (the
    /// look-ahead climb, the hard pull-up and the escape turn), steering, sensing players the way vanilla animals do,
    /// spotting shots that come close, playing animations, and which hits a bird dodges. EntityBehaviorWaterfowl (geese,
    /// ducks, herons) and EntityBehaviorRaptor (eagles) build on it.
    /// </summary>
    public abstract class EntityBehaviorBird : EntityBehavior
    {
        /// <summary>Simulation range while flying: the server ticks an entity, and sends it to players, only within this
        /// of some player (vanilla default 128). Landing flocks arrive from up to ~320 blocks out and departing ones are
        /// removed at 360 (FlockRegistry.DepartRange), so it must cover both.</summary>
        public const int FlightRange = 400;
        /// <summary>Terrain avoidance: feet clear ground and trees by Clearance; obstacles are seen LookSeconds ahead
        /// across the wingspan (HalfSpan either side). A wall too steep to climb at speed makes the bird pull up hard
        /// (SteepClimb) and shed speed, down to a near-vertical hover-climb at MinPullSpeed.</summary>
        protected const double Clearance = 1.5, LookSeconds = 3, HalfSpan = 1.2, SteepClimb = 7, MinPullSpeed = 1.2, PullDecel = 20;
        protected const double TurnRate = 1.4;
        /// <summary>Missed shots: ten times a second the bird looks for fast non-creatures (arrows, spears, stones, other
        /// mods' shot) within ShotScan blocks whose path passes within NearMiss of it in the next 0.3 s. An arrow covers
        /// several blocks between checks, so it is the path that is tested, not the distance.</summary>
        protected const double ShotScan = 24, NearMiss = 4, ShotMinSpeed = 9;   // blocks, blocks, blocks/s

        protected double flyHeading, speed;
        protected string anim;
        protected readonly Random rand = new();

        protected EntityBehaviorBird(Entity entity) : base(entity) { }

        /// <summary>Idle calls (attribute idleGap, seconds): the entity's idleSoundChance is rolled here instead of by the
        /// game, the same way (per tick, chance / 100), but a bird keeps quiet while another of its kind within
        /// BirdCalls.Radius called less than idleGap seconds ago. A lone bird calls as often as before; a flock of twenty
        /// no longer calls twenty times as often. The server config's CallRate scales both.</summary>
        private float idleChance, idleGap = 12;
        /// <summary>Wingbeats per second in ordinary flight (attribute flapSpeed; fly-flap is one beat of 30 frames and the
        /// game plays 30 a second, so this is its animation speed). 0 = the behaviour's own AnimSpeed.</summary>
        private float flapSpeed;
        /// <summary>Takeoff and landing sounds, spaced across a flock (seconds between two of one sound near each other):
        /// a flock of twenty going up gives a couple of alarm calls and a few wingbeats, not twenty of each.</summary>
        protected const double TakeoffCallGap = 1.5, WingsGap = 0.5, SplashGap = 0.25;

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            idleGap = attributes["idleGap"].AsFloat(idleGap);
            flapSpeed = attributes["flapSpeed"].AsFloat(0);
            idleChance = properties.IdleSoundChance;
            entity.IdleSoundChanceModifier = 0;   // the game's own roll is off; IdleCalls rolls instead
        }

        /// <summary>The idle call roll; each behaviour runs it once a tick on the server while the bird is alive.</summary>
        protected void IdleCalls()
        {
            float rate = (float)ResidentGeese.Config.CallRate;
            if (idleChance <= 0 || rate <= 0 || rand.NextDouble() >= idleChance * rate / 100.0) return;
            PlaySpaced("idle", idleGap / rate);
        }

        /// <summary>Plays one of the entity's sounds unless a bird of its kind within BirdCalls.Radius played the same
        /// sound less than gapSeconds ago (so a flock taking off gives a few calls, not one per bird).</summary>
        protected bool PlaySpaced(string sound, double gapSeconds)
        {
            if (!BirdCalls.TryCall(entity, sound, gapSeconds)) return false;
            entity.PlayEntitySound(sound);
            return true;
        }

        private float spearCheck;
        private static readonly System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo> hitListFields = new();

        /// <summary>
        /// Thrown spears never connect (Dright: it should look like they never do, not hit and do nothing). Twenty times a
        /// second the bird puts itself on the hit list of every thrown spear within 24 blocks: vanilla's projectile never
        /// strikes an entity on its list (EntityProjectileBase.entitiesHit, read by reflection since the class isn't in the
        /// API), so the spear flies on past or through it. The near miss still flushes the bird (IncomingShot).
        /// </summary>
        protected void LetSpearsPass(float dt)
        {
            if ((spearCheck -= dt) > 0) return;
            spearCheck = 0.05f;
            long me = entity.EntityId;
            foreach (var e in entity.World.GetEntitiesAround(entity.Pos.XYZ, 24, 16, e => e is not EntityAgent && e.Code?.Path?.StartsWith("spear") == true))
                if (HitList(e) is System.Collections.Generic.List<long> hit && !hit.Contains(me)) hit.Add(me);
        }

        private static System.Collections.Generic.List<long> HitList(Entity projectile)
        {
            var t = projectile.GetType();
            if (!hitListFields.TryGetValue(t, out var f))
            {
                for (var bt = t; bt != null && f == null; bt = bt.BaseType)
                    f = bt.GetField("entitiesHit", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                hitListFields[t] = f;
            }
            return f?.GetValue(projectile) as System.Collections.Generic.List<long>;
        }

        // ---- frozen for a picture (/flocks lineup)

        /// <summary>Set by /flocks lineup: the bird holds this spot, facing, and animation, and does nothing else.</summary>
        public const string FrozenKey = "flocksFrozen";

        /// <summary>A frozen bird (FrozenKey): held at its spot with its pose, no behaviour at all. True if frozen; the
        /// behaviour's tick then stops there (before its first-tick setup, so it joins no flock).</summary>
        protected bool HoldIfFrozen()
        {
            if (entity.WatchedAttributes[FrozenKey] is not ITreeAttribute t) return false;
            var p = entity.Pos;
            p.SetPos(t.GetDouble("x"), t.GetDouble("y"), t.GetDouble("z"));
            p.Motion.Set(0, 0, 0);
            p.Yaw = (float)t.GetDouble("yaw"); p.Roll = 0;
            SetAnim(t.GetString("anim", "idle-stand"));
            return true;
        }

        // ---- cages (the Animal Cages mod) and other moves: a bird spawned away from where it last was starts afresh

        private const string LastPosKey = "flocksLastPos";
        private float posSave;

        /// <summary>Notes where the bird is, once a second, in its saved (not synced) attributes. A bird later spawned
        /// more than RelocatedAt blocks from there was carried off, in a cage, say (OnEntitySpawn).</summary>
        protected void TrackPosition(float dt)
        {
            if ((posSave -= dt) > 0) return;
            posSave = 1;
            var t = new TreeAttribute();
            t.SetDouble("x", entity.Pos.X); t.SetDouble("y", entity.Pos.Y); t.SetDouble("z", entity.Pos.Z);
            entity.Attributes[LastPosKey] = t;
        }

        public const double RelocatedAt = 4;

        /// <summary>A bird let out of a cage comes back with everything it had when caught: its perch, its lake, a flight
        /// in progress. Spawned away from where it last was, it drops that and starts as if new where it is now
        /// (Relocated). Landing flocks and fresh spawns have no last position and are left alone.</summary>
        public override void OnEntitySpawn()
        {
            base.OnEntitySpawn();
            if (entity.World.Side != EnumAppSide.Server || entity.Attributes[LastPosKey] is not ITreeAttribute t) return;
            double dx = t.GetDouble("x") - entity.Pos.X, dy = t.GetDouble("y") - entity.Pos.Y, dz = t.GetDouble("z") - entity.Pos.Z;
            if (dx * dx + dy * dy + dz * dz < RelocatedAt * RelocatedAt) return;
            entity.Attributes.RemoveAttribute(LastPosKey);
            Relocated();
        }

        /// <summary>Forget the saved state (perch, lake, flight): the bird starts as if newly spawned where it is.</summary>
        protected virtual void Relocated() { }

        /// <summary>
        /// Caught in a cage (Animal Cages: the bird is removed as "picked up", just after the cage noted it): the cage
        /// draws the caught animal's model without animations, and ours, unanimated, have their wings spread for flight.
        /// Point the cage held in hand at the bird's standing, wings-folded model instead (shapes/entity/bird/<code>-caged,
        /// tools/make_caged.py). The cage stores the model path as capturedEntityShape.
        /// </summary>
        public override void OnEntityDespawn(EntityDespawnData despawn)
        {
            base.OnEntityDespawn(despawn);
            if (entity.World.Side != EnumAppSide.Server || despawn?.Reason != EnumDespawnReason.PickedUp) return;
            var shape = entity.Properties.Client?.Shape?.Base;
            if (shape == null) return;
            string plain = $"{shape.Domain}:shapes/{shape.Path}.json", caged = $"{shape.Domain}:shapes/{shape.Path}-caged.json";
            if (!entity.World.AssetManager.Exists(new AssetLocation(caged))) return;
            foreach (var p in entity.World.AllOnlinePlayers)
            {
                var slot = p.Entity?.RightHandItemSlot;
                var attrs = slot?.Itemstack?.Attributes;
                if (attrs == null || p.Entity.Pos.DistanceTo(entity.Pos) > 12 || attrs.GetString("capturedEntityShape") != plain) continue;
                attrs.SetString("capturedEntityShape", caged);
                slot.MarkDirty();
            }
        }

        // ---- a bird of a passing flock (SkyFlocks): it flies its slot in the formation the server moves along

        /// <summary>Marks a bird of a passing flock (saved attributes, not synced); the slot is SkyFlocks' to say.</summary>
        public const string PassageKey = "flocksPassage";
        private bool passChecked, passing, passStarted, loadedFromSave, passSoaring;
        private float passShotCheck, passScatter, passCycle;
        private double passScatterHeading;
        private const double PassClimbMax = 5, PassSinkMax = 3, PassAccel = 7;
        /// <summary>The server runs physics only for entities some player's client tracks, and a real bird can be beyond
        /// what any client has loaded (a short view distance): it would hang in the air while its flock flies on. So an
        /// untracked passing bird moves itself (it is high up, with nothing to bump into). The in-engine tests run every
        /// untracked bird's physics themselves and switch this off.</summary>
        public static bool MoveUntracked = true;

        /// <summary>One of a passing flock's real birds (SkyFlocks), flying its slot.</summary>
        public bool Passing => passing;

        /// <summary>
        /// A passing flock's bird, run first thing each tick; true when it was (the behaviour's own tick stops there). It
        /// steers for its slot (a point a little ahead of it, so it never turns back for one it has overshot) and speeds
        /// up or eases off to hold it, at the flock's height; terrain avoidance has the last word. After an alarm it
        /// bursts out (Scatter) for a few seconds first. Its calls are the drawn flock's, played by the clients, so it
        /// makes no idle calls of its own. A bird no flock owns was either loaded from a save (the flock is long gone,
        /// so it goes too) or let out of a cage (it stays, as an ordinary bird of its kind).
        /// </summary>
        protected bool Passage(float dt)
        {
            if (!passChecked) { passChecked = true; passing = entity.Attributes.HasAttribute(PassageKey); }
            if (!passing) return false;
            if (SkyFlocks.Instance == null || !SkyFlocks.Instance.Slot(entity.EntityId, out Flock f, out Bird b))
            {
                if (loadedFromSave) { entity.Die(EnumDespawnReason.Expire); return true; }
                entity.Attributes.RemoveAttribute(PassageKey);
                passing = false;
                return false;
            }
            entity.SimulationRange = FlightRange;
            if (!passStarted) { passStarted = true; flyHeading = f.Heading; speed = f.Speed; }
            LetSpearsPass(dt);
            if ((passShotCheck -= dt) <= 0)
            {
                passShotCheck = 0.1f;
                if (IncomingShot(out _, out _, out _)) SkyFlocks.Instance.Alarm(f);
            }
            var pos = entity.Pos;
            double vy, cap;
            bool hard;
            if (passScatter > 0)
            {
                passScatter -= dt;
                Steer(passScatterHeading, TurnRate * 2, dt);
                cap = f.Speed * 1.3; vy = 3; hard = true;
            }
            else
            {
                var slot = f.SlotInWorld(b.Offset);
                double dx = Math.Sin(f.Heading), dz = Math.Cos(f.Heading);
                double along = (slot.X - pos.X) * dx + (slot.Z - pos.Z) * dz, lead = 8 + Math.Max(0, -along);
                Steer(Math.Atan2(slot.X + dx * lead - pos.X, slot.Z + dz * lead - pos.Z), TurnRate * 1.5, dt);
                cap = GameMath.Clamp(f.Speed + along * 0.8, f.Speed * 0.6, f.Speed * 1.4);
                vy = GameMath.Clamp((slot.Y - pos.Y) * 0.8 + f.ClimbRate, -PassSinkMax, PassClimbMax);
                hard = vy > 3.5;
            }
            AvoidTerrain(ref vy, ref cap, dt);
            speed = speed < cap ? Math.Min(cap, speed + PassAccel * dt) : Math.Max(cap, speed - PullDecel * dt);
            if (entity.CollidedHorizontally) { speed = Math.Min(speed, MinPullSpeed); vy = SteepClimb; }
            // wingbeats and glides in the species' rhythm, as the drawn flock has them
            var sp = f.Species;
            if ((passCycle -= dt) <= 0)
            {
                passSoaring = !passSoaring;
                passCycle = passSoaring ? rand.Next(sp.SoarLoopsMin, sp.SoarLoopsMax + 1) * sp.SoarSeconds
                                        : rand.Next(sp.FlapBeatsMin, sp.FlapBeatsMax + 1) * sp.FlapSeconds;
            }
            SetAnim(hard ? "takeoff" : passSoaring && vy < 1.5 ? "fly-soar" : "fly-flap");
            pos.Motion.Set(Math.Sin(flyHeading) * speed / 60, vy / 60, Math.Cos(flyHeading) * speed / 60);
            if (MoveUntracked && entity.IsTracked == 0) pos.Add(pos.Motion.X * dt * 60, pos.Motion.Y * dt * 60, pos.Motion.Z * dt * 60);
            pos.Yaw = (float)flyHeading;
            // pitched with the climb, as the other flights do (Roll turns about the wing axis; negative is nose-up)
            double pitch = Math.Atan2(vy, Math.Max(speed, 0.5));
            pos.Roll += (float)((-GameMath.Clamp(pitch, -0.45, 1.1) - pos.Roll) * Math.Min(1, dt * 6));
            return true;
        }

        /// <summary>The flock was shot at (SkyFlocks.Alarm): burst out ahead and to one side, beating hard and climbing,
        /// for 2.5-4 s, then close up again.</summary>
        public void Scatter(double flockHeading)
        {
            if (!passing) return;
            passScatter = 2.5f + (float)rand.NextDouble() * 1.5f;
            passScatterHeading = flockHeading + (rand.NextDouble() < 0.5 ? -1 : 1) * (0.4 + rand.NextDouble() * 0.5);
        }

        /// <summary>A passing bird hit: a thrown spear is dodged as always, anything else is taken; either way its flock
        /// is alarmed. True if it was a passing bird (the behaviour's own handling is skipped).</summary>
        protected bool PassageHit(DamageSource source, ref float damage)
        {
            if (!passing) return false;
            if (Dodges(source)) damage = 0;
            if (SkyFlocks.Instance?.Slot(entity.EntityId, out Flock f, out _) == true) SkyFlocks.Instance.Alarm(f);
            return true;
        }

        /// <summary>Loaded from a save: a passing bird's flock is gone by then (Passage removes it). One loaded mid-flight
        /// needs its flight range before its first tick, or it would never tick beyond 128 blocks to be removed.</summary>
        public override void OnEntityLoaded()
        {
            base.OnEntityLoaded();
            loadedFromSave = true;
            if (entity.Attributes.HasAttribute(PassageKey)) entity.SimulationRange = FlightRange;
        }

        /// <summary>Flying away for good, ready to be removed once far from every player (FlockRegistry.TryDepart).</summary>
        public virtual bool DepartingInFlight => false;

        /// <summary>Animation speed the server starts an animation with (the entity file's animations set their own too).</summary>
        protected virtual float AnimSpeed(string code) => 1f;

        /// <summary>
        /// Steepest climb (blocks up per block forward) needed to keep the feet Clearance above every ground or tree
        /// top within dist along towards, sampled every 1.5 blocks down the middle and at each wingtip; -infinity if
        /// nothing is in the way. Open water never counts.
        /// </summary>
        protected double NeededSlope(double towards, double dist) => NeededSlope(towards, dist, out _, out _);

        /// <summary>NeededSlope, also giving the nearest obstacle in the way: how far ahead, and how far the feet must
        /// rise to clear it (0 / infinity when nothing is in the way).</summary>
        protected double NeededSlope(double towards, double dist, out double nearest, out double rise)
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            double sx = Math.Sin(towards), sz = Math.Cos(towards), worst = double.NegativeInfinity;
            nearest = double.PositiveInfinity; rise = 0;
            var tmp = new BlockPos(0, 0, 0);
            for (double d = 1; d <= dist; d += 1.5)
                for (int side = -1; side <= 1; side++)
                {
                    int x = (int)Math.Floor(pos.X + sx * d + sz * side * HalfSpan), z = (int)Math.Floor(pos.Z + sz * d - sx * side * HalfSpan);
                    int h = FloorAt(ba, x, z, pos.Y);
                    if (h <= 0 || h + 1 + Clearance <= pos.Y) continue;                       // unloaded, or well below
                    tmp.Set(x, h, z);
                    if (ba.GetBlock(tmp, BlockLayersAccess.Fluid)?.IsLiquid() == true) continue;
                    // the face may be up to one sample spacing nearer than the sample that sees it
                    worst = Math.Max(worst, (h + 1 + Clearance - pos.Y) / Math.Max(d - 2.1, 0.4));
                    if (d < nearest) { nearest = d; rise = h + 1 + Clearance - pos.Y; }
                    else if (d == nearest) rise = Math.Max(rise, h + 1 + Clearance - pos.Y);
                }
            return worst;
        }

        /// <summary>
        /// Raises vy to clear whatever is ahead, starting early (LookSeconds out) so most climbs are gentle. When the
        /// climb is steeper than SteepClimb allows at the current speed, caps the speed so the climb angle is enough:
        /// the goose pulls up hard and sheds speed, down to a hover-climb. If something is overhead (it flew in under
        /// a crown), climbing can't help: it turns hard toward the most open direction instead.
        /// </summary>
        protected void AvoidTerrain(ref double vy, ref double cap, float dt)
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            int x = (int)Math.Floor(pos.X), z = (int)Math.Floor(pos.Z), above = ba.GetRainMapHeightAt(x, z);
            // under a crown: something solid just overhead (high overhead, like a floating island, is open sky to fly under)
            if (above + 1 > pos.Y + 1.2 && SolidIn(ba, x, z, (int)Math.Floor(pos.Y) + 1, (int)Math.Floor(pos.Y) + Headroom)
                && ba.GetBlock(new BlockPos(x, above, z), BlockLayersAccess.Fluid)?.IsLiquid() != true)
            {
                Steer(MostOpenHeading(), TurnRate * 2.5, dt);
                vy = Math.Min(vy, 0.5); cap = Math.Min(cap, 4);
                return;
            }
            double slope = NeededSlope(flyHeading, Math.Max(8, speed * LookSeconds), out double near, out double rise);
            if (double.IsNegativeInfinity(slope)) return;
            // Too close to climb over in time (a go-round low inside a ring of trees, say): rising takes rise/SteepClimb,
            // reaching it (near - 0.6)/speed. Turn hard for the most open side, slow, and climb while turning.
            if (near < 6 && rise / SteepClimb > (near - 0.6) / Math.Max(speed, 0.5))
            {
                Steer(MostOpenHeading(), TurnRate * 2.5, dt);
                vy = SteepClimb; cap = Math.Min(cap, 3);
                return;
            }
            vy = Math.Max(vy, Math.Min(slope * Math.Max(speed, MinPullSpeed) * 1.35 + 0.6, SteepClimb));
            cap = Math.Min(cap, Math.Max(MinPullSpeed, SteepClimb / (slope * 1.35)));
        }

        /// <summary>Blocks of open air a bird wants over its feet to pass under something (a floating island, an arch).</summary>
        protected const int Headroom = 3;
        /// <summary>The course planner looks this far ahead, every PlanSeconds.</summary>
        protected const double PlanLength = 100, PlanSeconds = 0.5;

        private readonly BlockPos probe = new BlockPos(0, 0, 0);

        protected bool Solid(IBlockAccessor ba, int x, int y, int z)
        {
            probe.Set(x, y, z);
            var cb = ba.GetBlock(probe)?.CollisionBoxes;
            return cb != null && cb.Length > 0;
        }

        protected bool SolidIn(IBlockAccessor ba, int x, int z, int y0, int y1)
        {
            for (int y = y0; y <= y1; y++) if (Solid(ba, x, y, z)) return true;
            return false;
        }

        /// <summary>
        /// What a bird flying with its feet at y has to stay above in this column: the top of the column (the rain map),
        /// unless the column is open at the bird's height with room to pass, as under a floating island. Then it is the
        /// ground below that gap, and the island overhead doesn't count. A gap too low to fly through counts as solid.
        /// </summary>
        protected int FloorAt(IBlockAccessor ba, int x, int z, double y)
        {
            int top = ba.GetRainMapHeightAt(x, z);
            int yi = (int)Math.Floor(y);
            if (top <= yi + Headroom) return top;                     // nothing over the bird's height: the usual case
            if (SolidIn(ba, x, z, yi - 1, yi + Headroom)) return top; // something at its height: all of it is in the way
            int floor = yi - 2;
            while (floor > 0 && floor > yi - 96 && !Solid(ba, x, floor, z)) floor--;
            if (floor + 1 + Clearance > yi) return top;                // just a slot
            // where it is going is up on top of this (a roost on a floating island): over it, not under
            var dest = Destination;
            if (dest != null)
            {
                int underside = yi + Headroom + 1;
                while (underside < top && !Solid(ba, x, underside, z)) underside++;
                if (dest.Y >= underside) return top;
            }
            return floor;
        }

        /// <summary>Where the bird is flying to (a landing spot, a perch, a roost, prey), or null: the way to it may only
        /// pass under an overhang it isn't going up onto (FloorAt).</summary>
        protected virtual Vec3d Destination => null;

        /// <summary>The lowest solid block above the feet within dist straight ahead (and here), at least Headroom up;
        /// int.MaxValue when nothing is overhead. Flying under something, the bird keeps below it.</summary>
        protected int CeilingAhead(double dist)
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            int yi = (int)Math.Floor(pos.Y), ceiling = int.MaxValue;
            for (double d = 0; d <= dist; d += 4)
            {
                int x = (int)Math.Floor(pos.X + Math.Sin(flyHeading) * d), z = (int)Math.Floor(pos.Z + Math.Cos(flyHeading) * d);
                int top = ba.GetRainMapHeightAt(x, z);
                if (top <= yi + Headroom) continue;
                for (int y = yi + 1; y <= top && y < ceiling; y++) if (Solid(ba, x, y, z)) { ceiling = y; break; }
            }
            return ceiling;
        }

        private double detour = double.NaN, climbFloor, climbUntil;
        private float planTimer;

        /// <summary>The height a planned climb over something far ahead wants the feet at (0 when none is planned).</summary>
        protected double PlannedClimb => entity.World.ElapsedMilliseconds / 1000.0 < climbUntil ? climbFloor : 0;

        /// <summary>
        /// The long look-ahead, PlanLength out: the heading to fly instead of course, or course itself. Something at the
        /// bird's height on the way (a floating island, a mountain) is passed the gentler way, decided early: a turn to
        /// the nearest clear heading (side by side, the one it already leans to, else by flock, so a flock keeps
        /// together), or a steady climb over it (PlannedClimb), whichever costs less (a turn of 4 degrees against a
        /// block of climb). Under something with room to spare (FloorAt) the way counts as clear: they fly under.
        /// </summary>
        protected double PlanCourse(double course, float dt)
        {
            if ((planTimer -= dt) > 0) return double.IsNaN(detour) ? course : detour;
            planTimer = (float)PlanSeconds;
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            if (WayClear(ba, course, out double dist, out int top)) { detour = double.NaN; return course; }
            double rise = top + 1 + Clearance + 2 - pos.Y;
            // the nearest clear heading, on the side it leans to first
            double lean = GameMath.AngleRadDistance((float)course, (float)flyHeading);
            int first = Math.Abs(lean) > 0.05 ? Math.Sign(lean) : (entity.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0) % 2 == 0 ? 1 : -1);
            double turnTo = double.NaN, turnDeg = 0;
            for (int step = 1; step <= 6 && double.IsNaN(turnTo); step++)
                foreach (int side in new[] { first, -first })
                {
                    double h = course + side * step * 15 * GameMath.DEG2RAD;
                    if (WayClear(ba, h, out _, out _)) { turnTo = h; turnDeg = step * 15; break; }
                }
            if (!double.IsNaN(turnTo) && turnDeg / 4 < rise) { detour = turnTo; return detour; }
            // over it: climb steadily from here, until well past it
            detour = double.NaN;
            climbFloor = top + 1 + Clearance + 2;
            climbUntil = entity.World.ElapsedMilliseconds / 1000.0 + (dist + 40) / Math.Max(speed, 4);
            return course;
        }

        /// <summary>True when the way along heading is clear at the bird's height for PlanLength (across a flock's width);
        /// else false with the distance to the first thing in the way and its top.</summary>
        private bool WayClear(IBlockAccessor ba, double heading, out double dist, out int top)
        {
            var pos = entity.Pos;
            double sx = Math.Sin(heading), sz = Math.Cos(heading);
            dist = 0; top = 0;
            for (double d = 8; d <= PlanLength; d += 4)
                for (int side = -1; side <= 1; side++)
                {
                    int x = (int)Math.Floor(pos.X + sx * d + sz * side * 2.5), z = (int)Math.Floor(pos.Z + sz * d - sx * side * 2.5);
                    if (ba.GetChunkAtBlockPos(x, (int)pos.Y, z) == null) continue;   // unloaded: nothing known
                    int h = FloorAt(ba, x, z, pos.Y);
                    if (h <= 0 || h + 1 + Clearance <= pos.Y) continue;
                    probe.Set(x, h, z);
                    if (ba.GetBlock(probe, BlockLayersAccess.Fluid)?.IsLiquid() == true) continue;
                    dist = d; top = h;
                    return false;
                }
            return true;
        }

        /// <summary>Of straight on, 45 and 90 degrees either way, and back, the direction whose next 6 blocks need the
        /// gentlest climb.</summary>
        protected double MostOpenHeading()
        {
            double best = flyHeading, bestSlope = double.MaxValue;
            foreach (double off in new[] { 0, 0.8, -0.8, 1.6, -1.6, Math.PI })
            {
                double sl = NeededSlope(flyHeading + off, 6);
                if (sl < bestSlope) { bestSlope = sl; best = flyHeading + off; }
            }
            return best;
        }

        protected void Steer(double want, double rate, float dt)
            => flyHeading += GameMath.Clamp(GameMath.AngleRadDistance((float)flyHeading, (float)want), -rate * dt, rate * dt);

        protected double HorDist(Vec3d p) => HorDist(p.X, p.Z);
        /// <summary>Distance across the ground (ignoring height) from this bird to (x, z).</summary>
        protected double HorDist(double x, double z)
        {
            double dx = x - entity.Pos.X, dz = z - entity.Pos.Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }
        /// <summary>A heading picked at random, any way round.</summary>
        protected double RandomHeading() => rand.NextDouble() * 2 * Math.PI;

        /// <summary>
        /// A shot coming close: a fast-moving non-creature whose straight path over the next 0.3 s passes within NearMiss.
        /// away: the shot's direction of travel (the flock flies on away from the shooter); shooter: who fired it, when
        /// the projectile says (vanilla's EntityProjectile.FiredBy, read by name, since that class isn't in the API);
        /// from: otherwise a point 20 blocks back along its path.
        /// </summary>
        protected bool IncomingShot(out double away, out Entity shooter, out Vec3d from)
        {
            away = 0; shooter = null; from = null;
            var me = entity.Pos.XYZ;
            double minSq = ShotMinSpeed / 60 * (ShotMinSpeed / 60);
            foreach (var e in entity.World.GetEntitiesAround(me, (float)ShotScan, (float)ShotScan / 2,
                         e => e.Alive && e is not EntityAgent && e.Pos.Motion.LengthSq() > minSq))
            {
                var m = e.Pos.Motion;
                double vx = m.X * 60, vy = m.Y * 60, vz = m.Z * 60, v2 = vx * vx + vy * vy + vz * vz;
                double rx = me.X - e.Pos.X, ry = me.Y + 0.3 - e.Pos.Y, rz = me.Z - e.Pos.Z;
                double t = GameMath.Clamp((rx * vx + ry * vy + rz * vz) / v2, 0, 0.3);
                double cx = rx - vx * t, cy = ry - vy * t, cz = rz - vz * t;
                if (cx * cx + cy * cy + cz * cz > NearMiss * NearMiss) continue;
                away = Math.Atan2(vx, vz);
                shooter = e.GetType().GetField("FiredBy")?.GetValue(e) as Entity;
                double v = Math.Sqrt(v2);
                from = new Vec3d(e.Pos.X - vx / v * 20, e.Pos.Y, e.Pos.Z - vz / v * 20);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The nearest player this goose notices within range, sensed the way vanilla animals sense players
        /// (AiTaskBaseTargetable.CanSensePlayer): the player's animalSeekingRange stat scales the range, sneaking on
        /// the ground cuts it to 60%, and creative and spectator players go unnoticed. senseMul: that player's
        /// multiplier, which the flee and takeoff distances use too, so a sneaking hunter gets closer before the flush.
        /// </summary>
        protected Entity NearestPlayer(float range, out double senseMul)
        {
            Entity best = null; double bestD = double.MaxValue; senseMul = 1;
            foreach (var t in TestPlayers)
            {
                if (t == null || !t.Alive || t.World != entity.World) continue;
                double d = t.Pos.DistanceTo(entity.Pos);
                if (d < range && d < bestD) { best = t; bestD = d; }
            }
            foreach (var p in entity.World.AllOnlinePlayers)
            {
                var ep = p.Entity;
                if (ep == null || !ep.Alive || ep.Pos.Dimension != entity.Pos.Dimension) continue;
                var mode = p.WorldData?.CurrentGameMode;
                if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) continue;
                double mul = ep.Stats.GetBlended("animalSeekingRange");
                if (ep.Controls.Sneak && ep.OnGround) mul *= 0.6;
                double d = ep.Pos.DistanceTo(entity.Pos);
                if (d < range * mul && d < bestD) { best = ep; bestD = d; senseMul = mul; }
            }
            return best;
        }

        /// <summary>/flocks near: what this bird is doing and whether it senses the player, for checking in game.</summary>
        public string Diagnose(IPlayer player)
        {
            var ep = player.Entity;
            double d = ep.Pos.DistanceTo(entity.Pos);
            var mode = player.WorldData?.CurrentGameMode;
            var sensed = NearestPlayer(64, out double mul);
            string who = sensed == null ? "nobody within 64" : sensed == ep ? $"you (sensing x{mul:0.00})" : "someone else";
            string state = passing ? "flying in a passing flock" : StateText;
            return $"{entity.Code.ToShortString()} #{entity.EntityId}: {state}, {d:0.0} blocks from you ({mode}); senses {who}";
        }

        /// <summary>What it is doing, for Diagnose.</summary>
        protected virtual string StateText => "";

        /// <summary>The in-engine tests: entities sensed as if they were players (no player can join the test server).</summary>
        public static readonly System.Collections.Generic.List<Entity> TestPlayers = new();

        protected void SetAnim(string code)
        {
            if (code == anim) return;
            string previous = anim;
            anim = code;
            if (previous != null) entity.AnimManager?.StopAnimation(previous);
            float speedMul = code == "fly-flap" && flapSpeed > 0 ? flapSpeed : AnimSpeed(code);
            // Start it with the entity file's settings (blendMode Average, weights, easing). Bare metadata blends as Add,
            // which sums overlapping animations: a stale swim under walk or idle-stand doubled the wing fold, and the
            // wings swung forward under the head (a player's screenshot, 0.21.1). A client that first sees a bird gets
            // these settings with it; later changes it looks up in the entity file by name.
            var meta = entity.Properties.Client?.AnimationsByMetaCode?.TryGetValue(code, out var m) == true ? m.Clone()
                : new AnimationMetaData { Code = code, Animation = code, BlendMode = EnumAnimationBlendMode.Average };
            meta.AnimationSpeed = speedMul;
            entity.AnimManager?.StartAnimation(meta.Init());
        }

        /// <summary>
        /// Only thrown spears are dodged (and LetSpearsPass makes sure they never visibly strike). Arrows, thrown stones,
        /// other mods' shot and melee all hit (Dright: birds can be meleed, just not killed with a thrown spear).
        /// A projectile hit has SourceEntity = the projectile (not an agent); a thrown spear is a projectile whose code
        /// starts with "spear". A melee hit has the attacker itself as the source.
        /// </summary>
        protected static bool Dodges(DamageSource source)
        {
            var src = source.SourceEntity;
            return src != null && src is not EntityAgent && src.Code?.Path?.StartsWith("spear") == true;
        }

        /// <summary>The birds' AI runs on the server only; clients just draw what it sends.</summary>
        protected bool OnServer => entity.World.Side == EnumAppSide.Server;

        /// <summary>A number from the entity file's behaviour attributes; the field keeps its default when it isn't set.</summary>
        protected static void ReadAttr(JsonObject attributes, string key, ref float value) => value = attributes[key].AsFloat(value);
        protected static void ReadAttr(JsonObject attributes, string key, ref double value) => value = attributes[key].AsDouble(value);

        /// <summary>Heading (radians, 0 = +Z, as for Pos.Yaw) that leads from the point (x, z) straight past this bird.</summary>
        protected double HeadingAwayFrom(double x, double z) => Math.Atan2(entity.Pos.X - x, entity.Pos.Z - z);

        /// <summary>Whoever is behind a hit (the shooter rather than their arrow when the game knows them; else what
        /// struck) and the heading away from them; any heading when nothing can be blamed.</summary>
        protected Entity AttackerAndAway(DamageSource source, out double away)
        {
            Entity attacker = source.GetCauseEntity() ?? source.SourceEntity;
            away = attacker == null ? RandomHeading() : HeadingAwayFrom(attacker.Pos.X, attacker.Pos.Z);
            return attacker;
        }
    }
}
