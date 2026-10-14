using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// Server-side behaviour for landed waterfowl (flocks:greylag). Keeps the bird floating at the water surface,
    /// drifts it slowly, paddles away from players that come close, and takes off (flies away, then despawns)
    /// when a player gets very close, when it is shot at, or when its time on the lake runs out.
    /// Vanilla's land-creature AI tasks are not used: their pathfinder avoids water and getoutofwater would drag
    /// the birds ashore, so movement is set here directly and the vanilla physics does the rest.
    /// Damage: arrows and other ranged projectiles hurt it normally. Spears (thrown or held) and melee hits do
    /// nothing except scare it off - the goose dodges, like a fish slipping a spear.
    /// </summary>
    public class EntityBehaviorWaterfowl : EntityBehavior
    {
        public const string Code = "flocks.waterfowl";
        private const string SpawnKey = "flocksSpawnHours";
        /// <summary>Set by /flocks land on its test geese: they leave after lifeHours. Every other goose is a resident
        /// (spawned by vanilla's spawner) and stays until its resident season ends.</summary>
        public const string TestKey = "flocksTestGoose";

        public bool Resident => !entity.WatchedAttributes.HasAttribute(TestKey);
        private float seasonCheck = 5;

        private float fleeRange = 14, takeoffRange = 7, lifeHours = 6, flySeconds = 8, sink = 0.15f;
        private double driftSpeed = 0.004, paddleSpeed = 0.025, flySpeed = 0.07, climbSpeed = 0.045;

        private bool flying;
        private float flyTime, nextCheck;
        private double heading, takeoffHeading;
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
            flySeconds = attributes["flySeconds"].AsFloat(flySeconds);
            sink = attributes["sink"].AsFloat(sink);
            heading = rand.NextDouble() * Math.PI * 2;
        }

        public override void OnGameTick(float dt)
        {
            if (entity.World.Side != EnumAppSide.Server) return;
            var pos = entity.Pos;
            if (!entity.Alive) { if (entity.Swimming || entity.FeetInLiquid) pos.Motion.Y = SurfaceMotion(); return; }

            var attrs = entity.WatchedAttributes;
            double now = entity.World.Calendar.TotalHours;
            if (!attrs.HasAttribute(SpawnKey)) attrs.SetDouble(SpawnKey, now);

            if (flying)
            {
                flyTime += dt;
                pos.Motion.Set(Math.Sin(takeoffHeading) * flySpeed, climbSpeed, Math.Cos(takeoffHeading) * flySpeed);
                pos.Yaw = (float)takeoffHeading;
                if (flyTime > flySeconds) entity.Die(EnumDespawnReason.Expire);
                return;
            }
            if (!Resident && now - attrs.GetDouble(SpawnKey) > lifeHours) { Takeoff(rand.NextDouble() * Math.PI * 2); return; }
            if (Resident && (seasonCheck -= dt) <= 0)
            {
                seasonCheck = 30;
                var bp = pos.AsBlockPos;
                double lat = entity.World.Calendar.OnGetLatitude(bp.Z);
                if (!ResidentGeese.InSeason(entity.World, bp))
                {
                    // autumn: the whole group leaves toward the equator (0 = south, pi = north)
                    TakeoffGroup((lat >= 0 ? 0 : Math.PI) + (rand.NextDouble() - 0.5) * 0.5);
                    return;
                }
            }

            nextCheck -= dt;
            var player = nextCheck <= 0 ? NearestPlayer() : null;
            if (nextCheck <= 0) nextCheck = 0.25f;
            double speed = driftSpeed;
            if (player != null)
            {
                double dx = pos.X - player.Pos.X, dz = pos.Z - player.Pos.Z, d = Math.Sqrt(dx * dx + dz * dz);
                double away = Math.Atan2(dx, dz);
                if (d < takeoffRange) { TakeoffGroup(away); return; }
                if (d < fleeRange) { heading = away; speed = paddleSpeed; }
            }
            else heading += (rand.NextDouble() - 0.5) * 0.15; // slow wandering drift

            bool inWater = entity.Swimming || entity.FeetInLiquid;
            pos.Motion.X = Math.Sin(heading) * speed;
            pos.Motion.Z = Math.Cos(heading) * speed;
            if (inWater) pos.Motion.Y = SurfaceMotion();
            if (entity.CollidedHorizontally) heading += Math.PI * (0.5 + rand.NextDouble()); // turn away from the shore
            pos.Yaw = (float)heading;
            SetAnim(inWater ? "swim" : (speed > driftSpeed ? "walk" : "idle-stand"));
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

        private Entity NearestPlayer() =>
            entity.World.GetNearestEntity(entity.Pos.XYZ, fleeRange, fleeRange, e => e is EntityPlayer && e.Alive);

        /// <summary>This bird and the rest of its group on the lake take off together.</summary>
        private void TakeoffGroup(double away)
        {
            var others = entity.World.GetEntitiesAround(entity.Pos.XYZ, 16, 8, e => e.Code == entity.Code && e.Alive);
            foreach (var e in others)
                e.GetBehavior<EntityBehaviorWaterfowl>()?.Takeoff(away + (rand.NextDouble() - 0.5) * 0.6);
            Takeoff(away);
        }

        public void Takeoff(double towards)
        {
            if (flying || !entity.Alive) return;
            flying = true; flyTime = 0; takeoffHeading = towards;
            SetAnim("fly-flap");
        }

        private void SetAnim(string code)
        {
            if (anim == code) return;
            if (anim != null) entity.AnimManager?.StopAnimation(anim);
            anim = code;
            entity.AnimManager?.StartAnimation(new AnimationMetaData { Code = code, Animation = code, AnimationSpeed = code == "fly-flap" ? 1.6f : 1f }.Init());
        }

        public override void OnEntityReceiveDamage(DamageSource source, ref float damage)
        {
            if (entity.World.Side != EnumAppSide.Server || damage <= 0 || source == null) return;
            if (Dodges(source))
            {
                damage = 0;
                var from = source.GetCauseEntity() ?? source.SourceEntity;
                double away = from != null ? Math.Atan2(entity.Pos.X - from.Pos.X, entity.Pos.Z - from.Pos.Z) : rand.NextDouble() * Math.PI * 2;
                TakeoffGroup(away);
                return;
            }
            if (source.GetCauseEntity() != null || source.SourceEntity != null)
            {
                // a real hit: the rest of the group flies off; this one too if it survives (health runs after us)
                var from = source.GetCauseEntity() ?? source.SourceEntity;
                double away = Math.Atan2(entity.Pos.X - from.Pos.X, entity.Pos.Z - from.Pos.Z);
                entity.World.RegisterCallback(_ => { if (entity.Alive) TakeoffGroup(away); else ScareGroup(away); }, 1);
            }
        }

        private void ScareGroup(double away)
        {
            foreach (var e in entity.World.GetEntitiesAround(entity.Pos.XYZ, 16, 8, e => e.Code == entity.Code && e.Alive && e != entity))
                e.GetBehavior<EntityBehaviorWaterfowl>()?.Takeoff(away + (rand.NextDouble() - 0.5) * 0.6);
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
