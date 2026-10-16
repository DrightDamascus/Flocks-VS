using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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
                    int h = ba.GetRainMapHeightAt(x, z);
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
            if (above + 1 > pos.Y + 1.2 && ba.GetBlock(new BlockPos(x, above, z), BlockLayersAccess.Fluid)?.IsLiquid() != true)
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

        protected double HorDist(Vec3d p) => Math.Sqrt((p.X - entity.Pos.X) * (p.X - entity.Pos.X) + (p.Z - entity.Pos.Z) * (p.Z - entity.Pos.Z));

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
            EntityPlayer best = null; double bestD = double.MaxValue; senseMul = 1;
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

        protected void SetAnim(string code)
        {
            if (anim == code) return;
            if (anim != null) entity.AnimManager?.StopAnimation(anim);
            anim = code;
            float speedMul = AnimSpeed(code);
            entity.AnimManager?.StartAnimation(new AnimationMetaData { Code = code, Animation = code, AnimationSpeed = speedMul }.Init());
        }

        /// <summary>
        /// Spears and melee are dodged; ranged projectiles (arrows, thrown stones, other mods' shot) are not.
        /// A projectile hit has SourceEntity = the projectile (not an agent); a melee hit has the attacker itself as
        /// the source. A thrown spear is a projectile whose code starts with "spear".
        /// </summary>
        protected static bool Dodges(DamageSource source)
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
