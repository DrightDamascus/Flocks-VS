using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Flocks
{
    /// <summary>
    /// Server-side behaviour for crows (flocks:crow), the first bird that lives on land. A group forages together on open
    /// ground around its patch: walking about, pecking, looking round, and walking off from anyone who comes near. When
    /// someone comes too close, a shot flies past or one is hit, the whole group flies up into the nearest trees (or onto
    /// a wall or a crag) away from the threat, sits there a while, and comes back down to feed once nobody is near, on
    /// its old patch or a new one. At dusk the group goes up to roost and comes down again in the morning.
    /// Crop raiding (server config CropRaiding): now and then a crow flies over to a crop within raidRange (it finds
    /// farmland through vanilla's POIRegistry, as hares and deer do) and eats it the way they do: through the farmland's
    /// own IAnimalFoodSource.ConsumeOnePortion, so the crop dies and still gives its seed back. Never within ScarecrowRadius of a standing straw dummy (Scarecrows), where crows don't come down to feed
    /// either. Disturbances count toward the flock's wariness as for the waterfowl, for the place rather than a lake:
    /// flushed again and again, the flock leaves the area for a couple of days (FlockRegistry).
    /// Damage: arrows kill; spears and melee are dodged (EntityBehaviorBird), and either way the group flushes.
    /// Leaves have no collision box in this game, so a perched crow holds itself at the perch's height.
    /// </summary>
    public class EntityBehaviorCorvid : EntityBehaviorBird
    {
        public const string Code = "flocks.corvid";
        private const string StateKey = "flocksCorvid";
        private enum State { Forage, Takeoff, Fly, Land, Perch }

        // tuning (entity attributes)
        private float fleeRange = 18, takeoffRange = 10;
        private double flySpeed = 9, walkSpeed = 1.1, perchMin = 25, perchMax = 70, raidRange = 24;
        private string species = "crow";
        private Species speciesData;
        private Species SpeciesData => speciesData ??= entity.Api?.ModLoader.GetModSystem<FlocksModSystem>()?.SpeciesByCode.GetValueOrDefault(species);
        private static FlocksServerConfig Cfg => ResidentGeese.Config;

        /// <summary>Foraging keeps within PatchRadius of the patch. A perch is at least SafePerch from the threat, new
        /// feeding ground SafeGround from it and MinHop from the crow. A crow pecks at a crop for PeckSeconds.</summary>
        private const double PatchRadius = 8, SafePerch = 20, SafeGround = 28, MinHop = 10, PeckSeconds = 6, SearchRange = 48;
        /// <summary>The group goes to roost at RoostAt and comes down at WakeAt (game hours).</summary>
        private const double RoostAt = 19.5, WakeAt = 6;

        private State state = State.Forage;
        private float stateTime, stateLength = 30, shotCheck, nextCheck, activityTime, raidCheck = 2, roostCheck, landTries, cropTime, scareCheck;
        private bool restored, departing, roosting, toPerch, sounded;
        private string activity = "look";
        private double walkHeading, patchX, patchZ, rangeMult = 1, senseMul = 1, threatX, threatZ, nextRaid = double.NaN;
        private Vec3d target;
        private BlockPos crop;
        private Entity scarer;
        private long FlockId => entity.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0);

        /// <summary>Where each flock feeds next (game hours set): the first crow down picks it, the rest follow, so a group
        /// that scattered into the trees comes back down together.</summary>
        private static readonly Dictionary<long, (List<Vec3d> spots, double hours)> patches = new();
        /// <summary>Crops off limits until the given game hour: one a crow is on its way to or pecking at, so two crows
        /// don't go for the same one.</summary>
        private static readonly Dictionary<long, double> cropBusy = new();
        private static long CropKey(BlockPos p) => ((long)p.X << 36) ^ ((long)p.Z << 10) ^ p.Y;

        public EntityBehaviorCorvid(Entity entity) : base(entity) { }
        public override string PropertyName() => Code;
        protected override float AnimSpeed(string code) => code switch { "takeoff" => 2.2f, "fly-flap" => 1.5f, "land" => 1.3f, _ => 1f };
        public override bool DepartingInFlight => departing && (state == State.Fly || state == State.Takeoff);

        // ---- for diagnostics and the in-engine tests
        public string StateName => state.ToString();
        protected override string StateText => $"{state}{(roosting ? " (roosting)" : "")}, walks off within {fleeRange * rangeMult:0} and takes off within {takeoffRange * rangeMult:0} (times your sensing)";
        public BlockPos Crop => crop;
        public Vec3d Target => target;
        public bool Roosting => roosting;
        public double PatchX => patchX;
        public double PatchZ => patchZ;
        /// <summary>Crops this crow has eaten.</summary>
        public int Raids { get; private set; }
        /// <summary>Look for a crop to raid now, whatever the time since the last (tests).</summary>
        public void RaidNow() { nextRaid = entity.World.Calendar.TotalHours; raidCheck = 0; }

        public override void Initialize(EntityProperties properties, JsonObject attributes)
        {
            base.Initialize(properties, attributes);
            fleeRange = attributes["fleeRange"].AsFloat(fleeRange);
            takeoffRange = attributes["takeoffRange"].AsFloat(takeoffRange);
            flySpeed = attributes["flySpeed"].AsDouble(flySpeed);
            walkSpeed = attributes["walkSpeed"].AsDouble(walkSpeed);
            perchMin = attributes["perchSecondsMin"].AsDouble(perchMin);
            perchMax = attributes["perchSecondsMax"].AsDouble(perchMax);
            raidRange = attributes["raidRange"].AsDouble(raidRange);
            species = attributes["species"].AsString(species);
            walkHeading = rand.NextDouble() * Math.PI * 2;
        }

        public override void OnGameTick(float dt)
        {
            if (entity.World.Side != EnumAppSide.Server) return;
            if (!entity.Alive) { entity.Pos.Roll = 0; return; }
            if (!restored) { restored = true; Restore(); FlockRegistry.Instance?.Adopt(entity, species); }
            LetSpearsPass(dt);
            bool grounded = state == State.Forage || state == State.Perch;
            entity.SimulationRange = grounded ? GlobalConstants.DefaultSimulationRange : FlightRange;
            stateTime += dt;
            if (!grounded) { TickAir(dt); return; }

            if ((shotCheck -= dt) <= 0)
            {
                shotCheck = 0.1f;
                if (IncomingShot(out double shotAway, out Entity shooter, out Vec3d from)) { FlushGroup(shotAway, shooter, from); return; }
            }
            // the nearest player, 4 times a second; a skittish flock (wariness) flees and takes off from further away
            if ((nextCheck -= dt) <= 0)
            {
                nextCheck = 0.25f;
                rangeMult = FlockRegistry.Instance?.Skittish(FlockId) == true ? (FlockRegistry.Instance?.SkittishRangeFactor ?? 1) : 1;
                scarer = NearestPlayer((float)(fleeRange * rangeMult), out senseMul);
            }
            if (scarer != null && scarer.Pos.DistanceTo(entity.Pos) < takeoffRange * rangeMult * senseMul)
            {
                FlushGroup(Math.Atan2(entity.Pos.X - scarer.Pos.X, entity.Pos.Z - scarer.Pos.Z), scarer, null);
                return;
            }
            if (state == State.Forage) TickForage(dt); else TickPerch(dt);
        }

        // ------------------------------------------------------------------------------------------------ ground

        private bool Night()
        {
            double h = entity.World.Calendar.HourOfDay;
            return h >= RoostAt || h < WakeAt;
        }

        private void TickForage(float dt)
        {
            var pos = entity.Pos;
            pos.Roll = 0;
            double now = entity.World.Calendar.TotalHours;
            // a scarecrow nearby: the group leaves for ground out of its reach (not a disturbance: it never wears off)
            if ((scareCheck -= dt) <= 0)
            {
                scareCheck = 1;
                var dummy = Scarecrows.Nearest(entity.World, pos.XYZ, Cfg.ScarecrowRadius);
                if (dummy != null && LeaveScarecrow(dummy)) return;
            }
            if (Night() && (roostCheck -= dt) <= 0)
            {
                roostCheck = 15;
                if (GoRoost()) return;
            }
            double speedNow = 0;
            if (scarer != null)
            {
                // someone near, not yet too near: walk off, away from them, keeping an eye on them
                walkHeading = Math.Atan2(pos.X - scarer.Pos.X, pos.Z - scarer.Pos.Z);
                speedNow = walkSpeed * 1.4; crop = null;
                SetAnim("walk");
            }
            else if (crop != null) TickRaid(dt, now, ref speedNow);
            else
            {
                if (Cfg.CropRaiding && (raidCheck -= dt) <= 0)
                {
                    raidCheck = 5;
                    // a crow new to the place gets going within the first raid interval
                    if (double.IsNaN(nextRaid)) nextRaid = now + rand.NextDouble() * Cfg.CropRaidHoursMin;
                    else if (now >= nextRaid)
                    {
                        var c = FindCrop();
                        if (c != null) { StartRaid(c); return; }
                    }
                }
                // ordinary foraging: peck, look round, stroll, keeping near the patch
                if ((activityTime -= dt) <= 0)
                {
                    double r = rand.NextDouble();
                    if (r < 0.45) { activity = "peck"; activityTime = 3 + (float)rand.NextDouble() * 5; }
                    else if (r < 0.75) { activity = "walk"; activityTime = 1.5f + (float)rand.NextDouble() * 3; walkHeading += (rand.NextDouble() - 0.5) * 2.5; }
                    else { activity = "look"; activityTime = 2 + (float)rand.NextDouble() * 4; }
                    double px = patchX - pos.X, pz = patchZ - pos.Z;
                    if (px * px + pz * pz > PatchRadius * PatchRadius) { activity = "walk"; walkHeading = Math.Atan2(px, pz); }
                }
                if (activity == "walk") { speedNow = walkSpeed; SetAnim("walk"); }
                else SetAnim(activity == "peck" ? "eat" : "idle-stand");
            }
            if (speedNow > 0 && !CanWalk(walkHeading))
            {
                walkHeading += Math.PI * (0.5 + rand.NextDouble());
                speedNow = 0; activityTime = Math.Min(activityTime, 0.5f);
            }
            pos.Motion.X = Math.Sin(walkHeading) * speedNow / 60;
            pos.Motion.Z = Math.Cos(walkHeading) * speedNow / 60;
            pos.Yaw = (float)walkHeading;
        }

        /// <summary>At a crop: walk the last bit and peck at it until it gives, then off again. Too far to walk (a fence in
        /// the way, say): fly over to it.</summary>
        private void TickRaid(float dt, double now, ref double speedNow)
        {
            var pos = entity.Pos;
            if (Stage(entity.World.BlockAccessor.GetBlock(crop)) < 1 || Scarecrows.Protects(entity.World, crop.ToVec3d().Add(0.5, 0, 0.5), Cfg.ScarecrowRadius))
            {
                crop = null; return;
            }
            double dx = crop.X + 0.5 - pos.X, dz = crop.Z + 0.5 - pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
            walkHeading = Math.Atan2(dx, dz);
            if (d > 0.5)
            {
                if (d > 3 || cropTime < -8) { StartRaid(crop); return; }   // stuck short of it: fly over
                cropTime -= dt;
                speedNow = walkSpeed; SetAnim("walk");
                return;
            }
            if (cropTime < 0) cropTime = 0;
            SetAnim("eat");
            if ((cropTime += dt) >= PeckSeconds)
            {
                PeckCrop(crop);
                crop = null;
                nextRaid = now + Cfg.CropRaidHoursMin + rand.NextDouble() * Math.Max(0, Cfg.CropRaidHoursMax - Cfg.CropRaidHoursMin);
            }
        }

        /// <summary>Off to a crop: walk if it is just there, else fly over and land beside it.</summary>
        private void StartRaid(BlockPos c)
        {
            crop = c; cropTime = 0;
            cropBusy[CropKey(c)] = entity.World.Calendar.TotalHours + 0.5;
            double d = HorDist(new Vec3d(c.X + 0.5, c.Y, c.Z + 0.5));
            if (d <= 2.5) return;
            TakeOff(new Vec3d(c.X + 0.5, c.Y, c.Z + 0.5), false, false, entity.Pos.X, entity.Pos.Z, 0, keepCrop: true);
        }

        /// <summary>Eats the crop as vanilla's hares and deer do: the farmland's own ConsumeOnePortion turns it into a
        /// dead crop (eaten) that still drops its seed.</summary>
        private void PeckCrop(BlockPos at)
        {
            var ba = entity.World.BlockAccessor;
            if (Stage(ba.GetBlock(at)) < 1) return;
            if (ba.GetBlockEntity(at.DownCopy()) is not IAnimalFoodSource food) return;
            food.ConsumeOnePortion(entity);
            Raids++;
            double now = entity.World.Calendar.TotalHours;
            cropBusy.Remove(CropKey(at));
            if (cropBusy.Count > 512) foreach (var k in new List<long>(cropBusy.Keys)) if (cropBusy[k] < now) cropBusy.Remove(k);
        }

        /// <summary>A crop's growth stage (its code's last part), or 0 for anything that isn't a crop.</summary>
        private static int Stage(Block b) => b?.CropProps != null && int.TryParse(b.LastCodePart(), out int s) ? s : 0;

        /// <summary>
        /// The nearest crop within raidRange, not near a scarecrow nor in an abandoned area.
        /// Farmland registers with vanilla's POIRegistry as an animal food source (hares and deer eat crops too); its
        /// position is the crop's block. WalkPois leaves out the last chunk column of its range, so ask for a chunk more
        /// and filter here.
        /// </summary>
        private BlockPos FindCrop()
        {
            var reg = entity.Api.ModLoader.GetModSystem<POIRegistry>();
            if (reg == null) return null;
            var ba = entity.World.BlockAccessor; var me = entity.Pos.XYZ;
            var dummies = Scarecrows.Around(entity.World, me, raidRange + Cfg.ScarecrowRadius);
            double now = entity.World.Calendar.TotalHours;
            BlockPos best = null; double bestD = double.MaxValue;
            reg.WalkPois(me, (float)raidRange + 32, poi =>
            {
                if (poi is not IAnimalFoodSource || poi.Type != "food") return true;
                var p = poi.Position;
                double d = Math.Sqrt((p.X - me.X) * (p.X - me.X) + (p.Z - me.Z) * (p.Z - me.Z));
                if (d > raidRange || d >= bestD) return true;
                var at = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
                if (Stage(ba.GetBlock(at)) < 1) return true;
                if (cropBusy.TryGetValue(CropKey(at), out double until) && until > now) return true;   // another crow is on it
                if (Scarecrows.Protects(dummies, p.X, p.Z, Cfg.ScarecrowRadius) || FlockRegistry.Instance?.IsAbandoned(p.X, p.Z) == true) return true;
                best = at; bestD = d;
                return true;
            });
            return best;
        }

        /// <summary>Whether it can walk on along heading: ground (not water) at about its own level just ahead.</summary>
        private bool CanWalk(double heading)
        {
            var pos = entity.Pos; var ba = entity.World.BlockAccessor;
            int x = (int)Math.Floor(pos.X + Math.Sin(heading) * 0.7), z = (int)Math.Floor(pos.Z + Math.Cos(heading) * 0.7);
            int top = ba.GetRainMapHeightAt(x, z);
            if (top <= 0) return false;
            double dy = top + 1 - pos.Y;
            if (dy > 1.1 || dy < -2) return false;
            return ba.GetBlock(new BlockPos(x, top, z), BlockLayersAccess.Fluid)?.IsLiquid() != true;
        }

        private void TickPerch(float dt)
        {
            var pos = entity.Pos;
            pos.Motion.X = 0; pos.Motion.Z = 0; pos.Roll = 0;
            // hold on: leaves don't carry a bird in this game, so it keeps itself at the perch's height
            if (target != null) pos.Motion.Y = GameMath.Clamp((target.Y - pos.Y) * 0.2, -0.05, 0.05);
            SetAnim("idle-stand");
            if (roosting)
            {
                if (!Night()) { roosting = false; ComeDown(); }
                return;
            }
            if (Night()) { roosting = true; return; }   // stay up for the night
            if (stateTime > stateLength) ComeDown();
        }

        /// <summary>Back down to feed: the flock's patch if one of the group already picked it, else the old patch if
        /// nobody is near it, else new ground away from whoever is about. Waits on the perch if there's nowhere.</summary>
        private void ComeDown()
        {
            double now = entity.World.Calendar.TotalHours;
            List<Vec3d> spots = null;
            if (patches.TryGetValue(FlockId, out var p) && now - p.hours < 0.5) spots = p.spots;
            if (spots == null)
            {
                var player = NearestPlayer((float)(fleeRange * 2), out _);
                double tx = player?.Pos.X ?? entity.Pos.X, tz = player?.Pos.Z ?? entity.Pos.Z;
                var old = new Vec3d(patchX, entity.Pos.Y, patchZ);
                bool oldOk = (patchX != 0 || patchZ != 0) && (player == null || player.Pos.HorDistanceTo(old) > fleeRange * 1.5)
                             && !Scarecrows.Protects(entity.World, old, Cfg.ScarecrowRadius);
                spots = oldOk ? GroundSpots(entity.World, old, tx, tz, 0, 0, rand, centreOnly: true) : null;
                spots ??= GroundSpots(entity.World, entity.Pos.XYZ, tx, tz, SafeGround, MinHop, rand);
                if (spots == null || spots.Count == 0) { stateLength += 20; return; }
                patches[FlockId] = (spots, now);
            }
            TakeOff(spots[rand.Next(spots.Count)].Clone(), false, false, entity.Pos.X, entity.Pos.Z, 0);
        }

        // ------------------------------------------------------------------------------------------------ the group

        /// <summary>This crow and the rest of its group nearby that are on the ground or perched.</summary>
        private List<EntityBehaviorCorvid> Group()
        {
            var list = new List<EntityBehaviorCorvid> { this };
            long id = FlockId;
            foreach (var e in entity.World.GetEntitiesAround(entity.Pos.XYZ, 24, 16, e => e != entity && e.Alive && e.Code.Equals(entity.Code)
                         && e.WatchedAttributes.GetLong(FlockRegistry.FlockIdKey, 0) == id))
            {
                var b = e.GetBehavior<EntityBehaviorCorvid>();
                if (b != null && (b.state == State.Forage || b.state == State.Perch)) list.Add(b);
            }
            return list;
        }

        /// <summary>
        /// Something scared the group: it flies up into the trees (or onto high ground) away from the threat, each crow
        /// to its own perch near the others; with no perch about, to open ground away from it. A disturbance like any
        /// other: disturbed often enough, the flock leaves the area for good.
        /// </summary>
        private void FlushGroup(double away, Entity threat, Vec3d threatAt)
        {
            var pos = entity.Pos;
            double tx = threat?.Pos.X ?? threatAt?.X ?? pos.X, tz = threat?.Pos.Z ?? threatAt?.Z ?? pos.Z;
            var group = Group();
            if (FlockRegistry.Instance?.Disturb(FlockId, SpeciesData?.WarinessFactor ?? 1, pos.XYZ) == true)
            {
                foreach (var b in group) b.Depart(away + (rand.NextDouble() - 0.5) * 0.6, b == this ? 0 : (float)(0.1 + rand.NextDouble() * 0.5));
                return;
            }
            bool perch = true;
            var spots = FindPerches(pos.XYZ, tx, tz, group.Count, SafePerch);
            if (spots == null) { perch = false; spots = GroundSpots(entity.World, pos.XYZ, tx, tz, SafeGround, MinHop, rand); }
            if (spots == null || spots.Count == 0)
            {
                foreach (var b in group) b.Depart(away, b == this ? 0 : (float)(0.1 + rand.NextDouble() * 0.5));
                return;
            }
            patches.Remove(FlockId);
            for (int i = 0; i < group.Count; i++)
                group[i].TakeOff(spots[i % spots.Count].Clone(), perch, false, tx, tz, i == 0 ? 0 : (float)(0.08 + rand.NextDouble() * 0.5));
        }

        /// <summary>A straw dummy stands within ScarecrowRadius: the group flies off to feeding ground out of its reach
        /// (GroundSpots leaves the scarecrow's ground out), or failing that up into trees away from it. False if there is
        /// nowhere to go.</summary>
        private bool LeaveScarecrow(Vec3d dummy)
        {
            var group = Group();
            bool perch = false;
            var spots = GroundSpots(entity.World, entity.Pos.XYZ, dummy.X, dummy.Z, Cfg.ScarecrowRadius + 4, MinHop, rand);
            if (spots == null || spots.Count == 0) { perch = true; spots = FindPerches(entity.Pos.XYZ, dummy.X, dummy.Z, group.Count, Cfg.ScarecrowRadius + 4); }
            if (spots == null || spots.Count == 0) return false;
            patches.Remove(FlockId);
            for (int i = 0; i < group.Count; i++)
                if (group[i].state == State.Forage)
                    group[i].TakeOff(spots[i % spots.Count].Clone(), perch, false, dummy.X, dummy.Z, i == 0 ? 0 : (float)(0.1 + rand.NextDouble() * 0.6));
            if (rand.NextDouble() < 0.5) entity.PlayEntitySound("takeoffcall");
            return true;
        }

        /// <summary>Dusk: the group goes up into the trees for the night. False if there's nowhere to roost.</summary>
        private bool GoRoost()
        {
            var group = Group();
            var spots = FindPerches(entity.Pos.XYZ, entity.Pos.X, entity.Pos.Z, group.Count, 0);
            if (spots == null) return false;
            for (int i = 0; i < group.Count; i++)
                if (group[i].state == State.Forage)
                    group[i].TakeOff(spots[i % spots.Count].Clone(), true, true, entity.Pos.X, entity.Pos.Z, i == 0 ? 0 : (float)(rand.NextDouble() * 3));
            return true;
        }

        /// <summary>Takes off for spot: a perch (perch true; roost: for the night) or ground to feed on. tx, tz: the threat
        /// (or where it was). keepCrop: off to raid a crop, which stays its aim.</summary>
        public void TakeOff(Vec3d spot, bool perch, bool roost, double tx, double tz, float delay, bool keepCrop = false)
        {
            if (state != State.Forage && state != State.Perch) return;
            target = spot; toPerch = perch; roosting = roost; threatX = tx; threatZ = tz; landTries = 0; sounded = false;
            if (!keepCrop) crop = null;
            flyHeading = Math.Atan2(spot.X - entity.Pos.X, spot.Z - entity.Pos.Z); speed = 0;
            SetState(State.Takeoff);
            stateTime = -delay;
        }

        /// <summary>Leaves the area for good (disturbed too often, or nowhere to go): flies off along heading, climbing,
        /// and is removed once far from every player (FlockRegistry.TryDepart).</summary>
        private void Depart(double heading, float delay)
        {
            if (state != State.Forage && state != State.Perch) return;
            departing = true; crop = null; toPerch = false; roosting = false; sounded = false;
            var pos = entity.Pos;
            target = new Vec3d(pos.X + Math.Sin(heading) * 500, pos.Y + 30, pos.Z + Math.Cos(heading) * 500);
            flyHeading = heading; speed = 0;
            SetState(State.Takeoff);
            stateTime = -delay;
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
                    if (stateTime < 0) { pos.Motion.X = 0; pos.Motion.Z = 0; return; }   // the alarm hasn't reached this one yet
                    if (!sounded)
                    {
                        sounded = true;
                        if (rand.NextDouble() < 0.6) entity.PlayEntitySound("takeoffwings");
                        if (rand.NextDouble() < 0.4) entity.PlayEntitySound("takeoffcall");
                    }
                    SetAnim("takeoff");
                    speed = Math.Min(5, speed + 8 * dt);
                    vy = 4;
                    if (target != null) Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 2, dt);
                    if (stateTime > 0.7) SetState(State.Fly);
                    break;

                case State.Fly:
                {
                    if (target == null) { Replan(); break; }
                    double d = HorDist(target);
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 1.5, dt);
                    speed += GameMath.Clamp(flySpeed - speed, -4 * dt, 4 * dt);
                    double ground = GroundAhead();
                    double want = departing ? ground + 30 : Math.Max(target.Y + GameMath.Clamp(d * 0.35, 2, 14), ground + 4);
                    vy = GameMath.Clamp((want - pos.Y) * 0.8, -3, 4);
                    SetAnim(vy < -1 && d < 30 ? "fly-soar" : "fly-flap");
                    if (departing)
                    {
                        if (stateTime > 3) { FlockRegistry.Instance?.TryDepart(entity); if (!entity.Alive) return; }
                        break;
                    }
                    if (d < 6 + speed * 0.3) { SetState(State.Land); break; }
                    if (stateTime > 45) Replan();
                    break;
                }

                case State.Land:
                {
                    // someone standing at its spot, or a scarecrow by it: come down somewhere else
                    if ((nextCheck -= dt) <= 0)
                    {
                        nextCheck = 0.5f;
                        if (!toPerch && Scarecrows.Nearest(entity.World, target, Cfg.ScarecrowRadius) is Vec3d sc)
                        {
                            threatX = sc.X; threatZ = sc.Z;
                            Replan(); break;
                        }
                        var p = NearestPlayer((float)(takeoffRange * 2.5), out double mul);
                        if (p != null && p.Pos.HorDistanceTo(target) < takeoffRange * mul)
                        {
                            threatX = p.Pos.X; threatZ = p.Pos.Z;
                            if (toPerch) { var perches = FindPerches(pos.XYZ, threatX, threatZ, 1, SafePerch); if (perches != null) { target = perches[0]; landTries = 0; SetState(State.Fly); break; } }
                            Replan(); break;
                        }
                    }
                    // down onto the spot: the perch or the ground near it is what the look-ahead would climb over, so
                    // it is off here; down a slope that meets the spot a block out
                    avoid = false;
                    double d = HorDist(target);
                    Steer(Math.Atan2(target.X - pos.X, target.Z - pos.Z), TurnRate * 2, dt);
                    speed = Math.Max(0.6, Math.Min(speed, d * 1.0 + 0.4));
                    double aim = target.Y + Math.Max(0, d - 1) * 0.7;
                    vy = GameMath.Clamp((aim - pos.Y) * 2.5, -3.5, 3.5);
                    SetAnim("land");
                    // a perch within a block will do: the perch hold (TickPerch) lifts it the rest of the way
                    bool onSpot = d < 1.2 && Math.Abs(pos.Y - target.Y) < (toPerch ? 1.0 : 0.5);
                    if (onSpot && (entity.OnGround || pos.Y <= target.Y + 0.25 || toPerch)) { Arrive(); return; }
                    if ((entity.OnGround && !onSpot && d > 1.5) || entity.CollidedHorizontally) vy = 3;   // caught on something: up and over
                    if (stateTime > 10) { if (++landTries >= 3) Replan(); else SetState(State.Fly); }
                    break;
                }
            }
            if (avoid) { AvoidTerrain(ref vy, ref cap, dt); speed = Math.Min(speed, cap); }
            if (entity.CollidedHorizontally && avoid) { speed = Math.Min(speed, MinPullSpeed); vy = SteepClimb; }
            pos.Motion.Set(Math.Sin(flyHeading) * speed / 60, vy / 60, Math.Cos(flyHeading) * speed / 60);
            pos.Yaw = (float)flyHeading;
            double pitch = Math.Atan2(vy, Math.Max(speed, 0.5));
            pos.Roll += (float)((-GameMath.Clamp(pitch, -0.6, 0.9) - pos.Roll) * Math.Min(1, dt * 5));
        }

        /// <summary>Down on the spot: perched (for a while, or the night), or feeding there.</summary>
        private void Arrive()
        {
            var pos = entity.Pos;
            speed = 0; landTries = 0; pos.Roll = 0;
            pos.Motion.Set(0, 0, 0);
            if (toPerch)
            {
                stateLength = (float)(perchMin + rand.NextDouble() * (perchMax - perchMin));
                SetState(State.Perch);
            }
            else
            {
                patchX = target.X; patchZ = target.Z; activityTime = 0;
                SetState(State.Forage);
            }
        }

        /// <summary>Its spot can't be reached: somewhere else to come down, near here, away from the threat.</summary>
        private void Replan()
        {
            var spots = GroundSpots(entity.World, entity.Pos.XYZ, threatX, threatZ, SafeGround, 0, rand);
            if (spots == null || spots.Count == 0) { departing = true; target = entity.Pos.XYZ.AddCopy(Math.Sin(flyHeading) * 500, 30, Math.Cos(flyHeading) * 500); SetState(State.Fly); return; }
            target = spots[rand.Next(spots.Count)].Clone(); toPerch = false; roosting = false; landTries = 0;
            SetState(State.Fly);
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

        // ------------------------------------------------------------------------------------------------ finding places

        /// <summary>
        /// Perches for a group of n, within SearchRange of from and at least safe from the threat: the tops of trees
        /// (leaves at least 4 blocks over the ground under them), or failing that any solid top standing 3 or more blocks
        /// over the ground around it (a wall, a crag, a roof). The nearest, with spots toward the threat counted further;
        /// the group spreads over tops within 5 blocks of the best. Null if there are none.
        /// </summary>
        private List<Vec3d> FindPerches(Vec3d from, double tx, double tz, int n, double safe)
        {
            var ba = entity.World.BlockAccessor;
            int fx = (int)Math.Floor(from.X), fz = (int)Math.Floor(from.Z);
            double ax = from.X - tx, az = from.Z - tz, al = Math.Sqrt(ax * ax + az * az);
            var cands = new List<(double score, int x, int z, int top)>();
            var tmp = new BlockPos(0, 0, 0);
            for (int dx = -(int)SearchRange; dx <= SearchRange; dx += 2)
                for (int dz = -(int)SearchRange; dz <= SearchRange; dz += 2)
                {
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > SearchRange) continue;
                    int x = fx + dx, z = fz + dz;
                    if ((x + 0.5 - tx) * (x + 0.5 - tx) + (z + 0.5 - tz) * (z + 0.5 - tz) < safe * safe) continue;
                    if (FlockRegistry.Instance?.IsAbandoned(x + 0.5, z + 0.5) == true) continue;
                    if (!PerchAt(ba, x, z, tmp, out int top, out bool tree)) continue;
                    double toward = al > 0.1 && d > 0.1 ? -(dx * ax + dz * az) / (d * al) : 0;   // 1 = straight at the threat
                    cands.Add((d * (1.2 + 0.6 * toward) - (tree ? 8 : 0), x, z, top));
                }
            if (cands.Count == 0) return null;
            cands.Sort((p, q) => p.score.CompareTo(q.score));
            var best = cands[0];
            // every perch column within 5 blocks of the best (the search above only looks at every second one), nearest
            // first, so each crow of the group gets a spot of its own and they don't push each other off
            var near = new List<(double d, Vec3d at)>();
            for (int dx = -5; dx <= 5; dx++)
                for (int dz = -5; dz <= 5; dz++)
                {
                    int x = best.x + dx, z = best.z + dz;
                    if (dx * dx + dz * dz > 25 || (x + 0.5 - tx) * (x + 0.5 - tx) + (z + 0.5 - tz) * (z + 0.5 - tz) < safe * safe) continue;
                    if (PerchAt(ba, x, z, tmp, out int top, out _)) near.Add((dx * dx + dz * dz, new Vec3d(x + 0.5, top + 1, z + 0.5)));
                }
            near.Sort((p, q) => p.d.CompareTo(q.d));
            var spots = new List<Vec3d>();
            foreach (var (_, at) in near) { if (spots.Count >= Math.Max(1, n)) break; spots.Add(at); }
            if (spots.Count == 0) spots.Add(new Vec3d(best.x + 0.5, best.top + 1, best.z + 0.5));
            return spots;
        }

        /// <summary>A perch at the top of column (x, z): a tree crown, or a solid top standing out over its surroundings.</summary>
        private static bool PerchAt(IBlockAccessor ba, int x, int z, BlockPos tmp, out int top, out bool tree)
        {
            tree = false;
            top = ba.GetRainMapHeightAt(x, z);
            if (top <= 0) return false;
            tmp.Set(x, top, z);
            if (ba.GetBlock(tmp, BlockLayersAccess.Fluid)?.IsLiquid() == true) return false;
            var b = ba.GetBlock(tmp);
            if (b == null || b.Id == 0) return false;
            if (b.BlockMaterial == EnumBlockMaterial.Leaves)
            {
                // a tree: leaves and wood all the way down to the ground at least 4 blocks below
                for (int y = top - 1; y > top - 30 && y > 0; y--)
                {
                    tmp.Set(x, y, z);
                    var under = ba.GetBlock(tmp);
                    if (under == null || under.Id == 0 || under.BlockMaterial == EnumBlockMaterial.Leaves || under.BlockMaterial == EnumBlockMaterial.Wood
                        || under.CollisionBoxes == null || under.CollisionBoxes.Length == 0) continue;
                    tree = top - y >= 4;
                    return tree;
                }
                return false;
            }
            if (b.CollisionBoxes == null || b.CollisionBoxes.Length == 0) return false;
            double around = 0; int n = 0;
            foreach (var (ox, oz) in new[] { (4, 0), (-4, 0), (0, 4), (0, -4), (3, 3), (-3, 3), (3, -3), (-3, -3) })
            {
                int h = ba.GetRainMapHeightAt(x + ox, z + oz);
                if (h > 0) { around += h; n++; }
            }
            return n >= 6 && top - around / n >= 3;
        }

        /// <summary>
        /// Open feeding ground for a group, within 64 blocks of from: soil, sand or gravel open to the sky (no trees over
        /// it), fairly level, at least safe from the threat and minHop from from, not near a scarecrow, not in an
        /// abandoned area. One of the 8 nearest, with ground toward the threat counted further, and the ground within 4
        /// blocks of it at about its level. centreOnly: just around from itself (null if it isn't such ground).
        /// Feet positions (on top of the ground). Null if there is none.
        /// </summary>
        public static List<Vec3d> GroundSpots(IWorldAccessor world, Vec3d from, double tx, double tz, double safe, double minHop, Random rand, bool centreOnly = false)
        {
            var ba = world.BlockAccessor;
            var cfg = ResidentGeese.Config;
            int fx = (int)Math.Floor(from.X), fz = (int)Math.Floor(from.Z);
            var dummies = Scarecrows.Around(world, from, 64 + cfg.ScarecrowRadius);
            double ax = from.X - tx, az = from.Z - tz, al = Math.Sqrt(ax * ax + az * az);
            var found = new List<(double score, int x, int z, int top)>();
            int range = centreOnly ? 0 : 64;
            for (int dx = -range; dx <= range; dx += 3)
                for (int dz = -range; dz <= range; dz += 3)
                {
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d > Math.Max(range, 0.5) || (!centreOnly && d < minHop)) continue;
                    int x = fx + dx, z = fz + dz;
                    if ((x + 0.5 - tx) * (x + 0.5 - tx) + (z + 0.5 - tz) * (z + 0.5 - tz) < safe * safe) continue;
                    if (!OpenGround(ba, x, z, out int top) || !Level(ba, x, z, top)) continue;
                    if (Scarecrows.Protects(dummies, x + 0.5, z + 0.5, cfg.ScarecrowRadius)) continue;
                    if (FlockRegistry.Instance?.IsAbandoned(x + 0.5, z + 0.5) == true) continue;
                    double toward = al > 0.1 && d > 0.1 ? -(dx * ax + dz * az) / (d * al) : 0;
                    found.Add((d * (1.5 + 0.75 * toward), x, z, top));
                }
            if (found.Count == 0) return null;
            found.Sort((p, q) => p.score.CompareTo(q.score));
            var c = found[rand.Next(Math.Min(8, found.Count))];
            var spots = new List<Vec3d>();
            for (int dx = -4; dx <= 4; dx++)
                for (int dz = -4; dz <= 4; dz++)
                {
                    if (dx * dx + dz * dz > 16) continue;
                    int x = c.x + dx, z = c.z + dz;
                    if (OpenGround(ba, x, z, out int top) && Math.Abs(top - c.top) <= 1 && !Scarecrows.Protects(dummies, x + 0.5, z + 0.5, cfg.ScarecrowRadius))
                        spots.Add(new Vec3d(x + 0.5, top + 1, z + 0.5));
                }
            return spots;
        }

        /// <summary>The column's top is open ground a crow feeds on (soil, sand, gravel; under a plant or a crop, the
        /// ground below it): top = that ground block.</summary>
        public static bool OpenGround(IBlockAccessor ba, int x, int z, out int top)
        {
            top = ba.GetRainMapHeightAt(x, z);
            if (top <= 0) return false;
            var p = new BlockPos(x, top, z);
            if (ba.GetBlock(p, BlockLayersAccess.Fluid)?.IsLiquid() == true) return false;
            var b = ba.GetBlock(p);
            if (b != null && (b.CollisionBoxes == null || b.CollisionBoxes.Length == 0) && b.BlockMaterial != EnumBlockMaterial.Leaves)
            {
                p.Y--; top--;                                                           // a plant or a crop on the ground
                if (ba.GetBlock(p, BlockLayersAccess.Fluid)?.IsLiquid() == true) return false;
                b = ba.GetBlock(p);
            }
            if (b == null) return false;
            var m = b.BlockMaterial;
            return m == EnumBlockMaterial.Soil || m == EnumBlockMaterial.Sand || m == EnumBlockMaterial.Gravel;
        }

        /// <summary>No step of more than a block to the four neighbouring columns.</summary>
        private static bool Level(IBlockAccessor ba, int x, int z, int top)
        {
            foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (Math.Abs(ba.GetRainMapHeightAt(x + ox, z + oz) - top) > 1) return false;
            return true;
        }

        // ------------------------------------------------------------------------------------------------ saving

        private void SetState(State s) { state = s; stateTime = 0; Persist(); }

        private void Persist()
        {
            var t = new TreeAttribute();
            t.SetInt("state", (int)state);
            t.SetDouble("patchX", patchX); t.SetDouble("patchZ", patchZ);
            t.SetBool("perch", toPerch); t.SetBool("roost", roosting); t.SetBool("depart", departing);
            if (target != null) { t.SetDouble("tx", target.X); t.SetDouble("ty", target.Y); t.SetDouble("tz", target.Z); }
            entity.WatchedAttributes[StateKey] = t;
        }

        private void Restore()
        {
            if (entity.WatchedAttributes[StateKey] is ITreeAttribute t)
            {
                var s = (State)t.GetInt("state");
                patchX = t.GetDouble("patchX"); patchZ = t.GetDouble("patchZ");
                toPerch = t.GetBool("perch"); roosting = t.GetBool("roost"); departing = t.GetBool("depart");
                target = t.HasAttribute("tx") ? new Vec3d(t.GetDouble("tx"), t.GetDouble("ty"), t.GetDouble("tz")) : null;
                // in the air: carry on to its spot; on the ground or a perch: stay
                state = s == State.Forage || s == State.Perch ? s : (target != null ? State.Fly : State.Forage);
                if (state == State.Perch) stateLength = (float)(perchMin + rand.NextDouble() * (perchMax - perchMin));
                flyHeading = entity.Pos.Yaw; speed = state == State.Fly ? flySpeed : 0;
            }
            else
            {
                // new: it feeds where it appeared
                patchX = entity.Pos.X; patchZ = entity.Pos.Z;
                state = State.Forage;
            }
            Persist();
        }

        public override void OnEntityLoaded()
        {
            if (entity.WatchedAttributes[StateKey] is ITreeAttribute t)
            {
                var s = (State)t.GetInt("state");
                if (s != State.Forage && s != State.Perch) entity.SimulationRange = FlightRange;
            }
        }

        // ------------------------------------------------------------------------------------------------ damage

        public override void OnEntityReceiveDamage(DamageSource source, ref float damage)
        {
            if (entity.World.Side != EnumAppSide.Server || damage <= 0 || source == null) return;
            var from = source.GetCauseEntity() ?? source.SourceEntity;
            double away = from != null ? Math.Atan2(entity.Pos.X - from.Pos.X, entity.Pos.Z - from.Pos.Z) : rand.NextDouble() * Math.PI * 2;
            if (Dodges(source))
            {
                damage = 0;
                if (state == State.Forage || state == State.Perch) FlushGroup(away, from, null);
                return;
            }
            if (from == null) return;
            // a real hit: the group flies off (this one too if it survives; health runs after us)
            var threat = source.GetCauseEntity() ?? from;
            entity.World.RegisterCallback(_ =>
            {
                if (entity.Alive) { if (state == State.Forage || state == State.Perch) FlushGroup(away, threat, null); return; }
                foreach (var e in entity.World.GetEntitiesAround(entity.Pos.XYZ, 24, 16, e => e.Code.Equals(entity.Code) && e.Alive))
                {
                    var b = e.GetBehavior<EntityBehaviorCorvid>();
                    if (b != null && (b.state == State.Forage || b.state == State.Perch)) { b.FlushGroup(away, threat, null); return; }
                }
            }, 1);
        }

        public override void OnEntityDeath(DamageSource damageSourceForDeath)
        {
            if (entity.World.Side == EnumAppSide.Server) FlockRegistry.Instance?.MemberDied(entity, SpeciesData?.WarinessFactor ?? 1);
        }
    }
}
