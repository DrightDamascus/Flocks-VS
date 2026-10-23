using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    public class Bird
    {
        public Vec3d Offset; // slot relative to the leader in the flock's own frame: X = along (+ ahead), Y = up, Z = across
        public float Phase;  // where this bird is in its wingbeat and weave (s), so a flock doesn't move in step
        public float YawJitter; // each bird weaves a few degrees off the flock heading
        public int FlapBeats, SoarLoops; // wingbeats before gliding, soar loops before flapping again
        /// <summary>The real bird flying this slot (SkyFlocks), or 0 while the bird is only drawn.</summary>
        public long EntityId;
    }

    /// <summary>
    /// A passing flock. The server moves it (SkyFlocks) and tells the clients, which draw it (FlockRenderer); near a
    /// player its birds are real entities that fly their slots around it, so the flock itself goes on moving either way.
    /// </summary>
    public class Flock
    {
        /// <summary>The server's id for it; Seed picks each bird's wingbeat and weave, the same on every client.</summary>
        public long Id; public int Seed;
        public Species Species;
        public Vec3d Pos;                             // leader position
        public double BaseHeading, Heading, TurnRate; // radians, rad/s; dir = (sin, 0, cos)
        public double Wander1, Wander2;               // phases of the slow heading drift
        public double Speed, Travel, Length, Age;
        public double CruiseY, TargetY, ClimbRate;
        public double Tail, HalfWidth, Drop, Rise;    // formation extent: behind the leader, to either side, below and above the leader
        public double NextSample;
        public double NextCall;                       // when (flock Age, s) one of its birds calls next; 0 = not yet set
        public List<Bird> Birds = new();

        /// <summary>Where a formation slot is in the world, with the flock as it is now: slot X runs along the heading,
        /// Z across it, Y up.</summary>
        public Vec3d SlotInWorld(Vec3d slot)
        {
            double hx = Math.Sin(Heading), hz = Math.Cos(Heading);
            return new Vec3d(Pos.X + hx * slot.X + hz * slot.Z, Pos.Y + slot.Y, Pos.Z + hz * slot.X - hx * slot.Z);
        }

        /// <summary>The middle of the formation: the leader, back half its length.</summary>
        public Vec3d Centre => new Vec3d(Pos.X - Math.Sin(Heading) * Tail * 0.5, Pos.Y + (Rise - Drop) * 0.5, Pos.Z - Math.Cos(Heading) * Tail * 0.5);
    }

    /// <summary>
    /// How a passing flock flies: a straight heading with a slow drift, a cruising height clear of the ground ahead and
    /// of the other flocks up at the time. The server runs it with the terrain (SkyFlocks); clients run it between the
    /// server's updates without, taking the server's target height instead (FlockRenderer).
    /// </summary>
    public static class SkyFlight
    {
        /// <summary>The lowest bird keeps at least this far above the highest ground under or near the flock.</summary>
        public const double Clearance = 35;
        /// <summary>
        /// Air traffic: several flocks may be in the sky at once, kept apart in height. A new flock cruises at least
        /// Separation blocks clear of every flock already up (its lowest bird over their highest, or the other way round),
        /// and in flight, when two flocks come within ConflictRange of each other (plus their lengths) without that much
        /// height between them, the newer one climbs over the older and comes back down to its cruise once clear.
        /// </summary>
        public const double Separation = 15, ConflictRange = 120;
        /// <summary>How far ahead (blocks) a flock looks for rising ground.</summary>
        public const double LookAhead = 320;
        public const double MaxClimb = 6, MaxSink = 1.5; // blocks/s
        /// <summary>Slow course drift: two sines, ~12 deg over ~60 s and ~5 deg over ~23 s.</summary>
        public const double WanderBig = 12 * GameMath.DEG2RAD, WanderSmall = 5 * GameMath.DEG2RAD;

        /// <summary>
        /// A new flock of count birds, setting off startRange blocks back along heading from 'from' (passing a little to
        /// one side of it) at height y, clear in height of every flock in 'others'. The caller adds it to them.
        /// </summary>
        public static Flock Create(long id, Species sp, int count, Vec3d from, double heading, double y, double startRange,
            Random rand, List<Flock> others, IBlockAccessor ba)
        {
            double fx = Math.Sin(heading), fz = Math.Cos(heading);   // along the track; (fz, -fx) is across it
            double offTrack = (rand.NextDouble() - 0.5) * 40;
            var f = new Flock
            {
                Id = id, Seed = rand.Next(), Species = sp, Speed = sp.SpeedMin + rand.NextDouble() * (sp.SpeedMax - sp.SpeedMin),
                BaseHeading = heading, Wander1 = rand.NextDouble() * Math.PI * 2, Wander2 = rand.NextDouble() * Math.PI * 2,
                Pos = new Vec3d(from.X - fx * startRange + fz * offTrack, y, from.Z - fz * startRange - fx * offTrack),
                CruiseY = y, TargetY = y,
            };
            f.Heading = WanderedHeading(f);
            for (int i = 0; i < count; i++)
            {
                var b = new Bird { Offset = SlotFor(sp, i, count, rand) };
                Extend(f, b.Offset);
                f.Birds.Add(b);
            }
            Dress(f);
            f.Length = 2 * startRange + f.Tail + sp.SpanBlocks;
            // a cruising height clear of every flock already in the sky
            for (int pass = 0; pass <= others.Count; pass++)
            {
                Flock clash = null;
                foreach (var o in others) if (Overlap(f.CruiseY, f, o)) { clash = o; break; }
                if (clash == null) break;
                f.CruiseY = clash.Pos.Y + clash.Rise + Separation + f.Drop;
            }
            f.TargetY = f.Pos.Y = f.CruiseY;
            // if it starts over known high ground, start already clear of it rather than climbing out of a hillside
            SampleTerrain(f, ba, others);
            f.Pos.Y = Math.Max(f.Pos.Y, f.TargetY);
            return f;
        }

        /// <summary>Each bird's wingbeat and weave, from the flock's seed, so every client draws them alike.</summary>
        public static void Dress(Flock f)
        {
            var sp = f.Species;
            for (int i = 0; i < f.Birds.Count; i++)
            {
                var r = new Random(f.Seed * 31 + i);
                var b = f.Birds[i];
                b.Phase = (float)r.NextDouble() * 3;
                b.YawJitter = (float)((r.NextDouble() * 2 - 1) * 3 * GameMath.DEG2RAD);
                b.FlapBeats = r.Next(sp.FlapBeatsMin, sp.FlapBeatsMax + 1);
                b.SoarLoops = r.Next(sp.SoarLoopsMin, sp.SoarLoopsMax + 1);
            }
        }

        /// <summary>
        /// Formation slot i of count, relative to the leader (X along, Y up, Z across). V slots scale with wingspan:
        /// each rank sits back by about half a span and out by most of one, so neighbouring wingtips stay just clear.
        /// A loose group spreads uniformly over a disc one radius behind the leader, with some height spread.
        /// </summary>
        private static Vec3d SlotFor(Species sp, int i, int count, Random rand)
        {
            double span = sp.SpanBlocks;
            if (sp.Loose)
            {
                double looseR = span * (1.5 + Math.Sqrt(count));
                double r = looseR * Math.Sqrt(rand.NextDouble()), a = rand.NextDouble() * Math.PI * 2;
                return new Vec3d(-looseR + r * Math.Cos(a), (rand.NextDouble() - 0.5) * span * 2, r * Math.Sin(a));
            }
            int rank = (i + 1) / 2; int side = i == 0 ? 0 : (i % 2 == 0 ? 1 : -1);
            return new Vec3d(-rank * span * 0.55 + (rand.NextDouble() - 0.5) * span * 0.2, rank * 0.2 + (rand.NextDouble() - 0.5),
                             side * rank * span * 0.8 + (rand.NextDouble() - 0.5) * span * 0.2);
        }

        public static void Extend(Flock f, Vec3d o)
        {
            f.Tail = Math.Max(f.Tail, -o.X); f.HalfWidth = Math.Max(f.HalfWidth, Math.Abs(o.Z));
            f.Drop = Math.Max(f.Drop, -o.Y); f.Rise = Math.Max(f.Rise, o.Y);
        }

        /// <summary>Whether flock f at leader height y would come within Separation, in height, of flock o.</summary>
        private static bool Overlap(double y, Flock f, Flock o)
            => y - f.Drop < o.Pos.Y + o.Rise + Separation && y + f.Rise > o.Pos.Y - o.Drop - Separation;

        public static double WanderedHeading(Flock f)
            => f.BaseHeading + WanderBig * Math.Sin(f.Age * 0.105 + f.Wander1) + WanderSmall * Math.Sin(f.Age * 0.27 + f.Wander2);

        /// <summary>
        /// Sets TargetY: cruising height, or higher where the ground ahead, under, behind (for the V's tail) or to either
        /// side of the flock needs it. Ground further ahead counts for less by however much the flock can climb before
        /// it gets there, so it starts climbing just early enough. Unloaded chunks read as height 0 and are skipped;
        /// nobody is near enough to see a flock there anyway. Then clear of the flocks that were up before this one.
        /// </summary>
        public static void SampleTerrain(Flock f, IBlockAccessor ba, List<Flock> others)
        {
            double dx = Math.Sin(f.Heading), dz = Math.Cos(f.Heading);
            double climbPerBlock = MaxClimb * 0.6 / f.Speed;
            double need = f.CruiseY;
            for (double d = -f.Tail; d <= LookAhead; d += 16)
            {
                for (int s = -1; s <= 1; s++)
                {
                    double x = f.Pos.X + dx * d + dz * s * f.HalfWidth, z = f.Pos.Z + dz * d - dx * s * f.HalfWidth;
                    int h = ba.GetRainMapHeightAt((int)x, (int)z);
                    if (h <= 0) continue;
                    need = Math.Max(need, h + Clearance + f.Drop - Math.Max(0, d) * climbPerBlock);
                }
            }
            // keep clear of the flocks that were up before this one: over any that come near at about its height
            foreach (var o in others)
            {
                if (o == f) break;
                double hx = o.Pos.X - f.Pos.X, hz = o.Pos.Z - f.Pos.Z, near = ConflictRange + o.Tail + f.Tail;
                if (hx * hx + hz * hz > near * near || !Overlap(need, f, o)) continue;
                need = Math.Max(need, o.Pos.Y + o.Rise + Separation + f.Drop);
            }
            f.TargetY = Math.Min(need, ba.MapSizeY + 100);
        }

        /// <summary>One step along: the drifting heading, the speed, and easing toward TargetY (re-sampled from the terrain
        /// every 0.3 s when ba is given; a client leaves it to the server).</summary>
        public static void Move(Flock f, float dt, IBlockAccessor ba, List<Flock> others)
        {
            f.Age += dt;
            double h = WanderedHeading(f);
            f.TurnRate = (h - f.Heading) / dt;
            f.Heading = h;
            f.Pos.X += Math.Sin(h) * f.Speed * dt;
            f.Pos.Z += Math.Cos(h) * f.Speed * dt;
            f.Travel += f.Speed * dt;
            if (ba != null && f.Age >= f.NextSample) { SampleTerrain(f, ba, others); f.NextSample = f.Age + 0.3; }
            // ease toward the target height: climb briskly, sink back to cruise gently
            double want = GameMath.Clamp((f.TargetY - f.Pos.Y) * 0.4, -MaxSink, MaxClimb);
            f.ClimbRate += (want - f.ClimbRate) * Math.Min(1, dt * 1.5);
            f.Pos.Y += f.ClimbRate * dt;
        }
    }
}
