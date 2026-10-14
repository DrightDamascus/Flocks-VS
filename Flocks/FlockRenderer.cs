using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    public class Bird
    {
        public Vec3d Offset; // slot relative to the leader in the flock's own frame: X = along (+ ahead), Y = up, Z = across
        public float Phase;  // animation phase offset (s)
        public float YawJitter; // each bird weaves a few degrees off the flock heading
        public int FlapBeats, SoarLoops; // wingbeats before gliding, soar loops before flapping again
    }

    public class Flock
    {
        public Species Species;
        public Vec3d Pos;                             // leader position
        public double BaseHeading, Heading, TurnRate; // radians, rad/s; dir = (sin, 0, cos)
        public double Wander1, Wander2;               // phases of the slow heading drift
        public double Speed, Travel, Length, Age;
        public double CruiseY, TargetY, ClimbRate;
        public double Tail, HalfWidth, Drop;          // formation extent: behind the leader, to either side, below the leader
        public double NextSample;
        public double NextCall;                       // flock age (s) of its next distant call
        public List<Bird> Birds = new();
    }

    /// <summary>Client-only renderer: draws baked bird meshes at flock positions. No entities, nothing sent to the server.</summary>
    public class FlockRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly List<Flock> flocks = new();
        private readonly Random rand = new();
        private readonly Matrixf mat = new();

        /// <summary>The lowest bird keeps at least this far above the highest ground under or near the flock.</summary>
        public double Clearance = 35;
        /// <summary>How far ahead (blocks) a flock looks for rising ground.</summary>
        public double LookAhead = 320;
        public double MaxClimb = 6, MaxSink = 1.5; // blocks/s
        /// <summary>Slow course drift: two sines, ~12 deg over ~60 s and ~5 deg over ~23 s.</summary>
        public double WanderBig = 12 * GameMath.DEG2RAD, WanderSmall = 5 * GameMath.DEG2RAD;

        /// <summary>Volume of the flying flocks' distant calls (client config FlockCallVolume; 0 = silent).</summary>
        public float CallVolume = 1f;
        /// <summary>Calls fade out linearly over this many blocks from the calling bird.</summary>
        public float CallRange = 220;

        public FlockRenderer(ICoreClientAPI capi) { this.capi = capi; }
        public double RenderOrder => 0.45;
        public int RenderRange => 400;
        public int Count => flocks.Count;

        /// <summary>heading: radians, dir = (sin, 0, cos), so 0 = south (+Z), pi = north. altitude: blocks above ground.</summary>
        public void Send(Species sp, int count, double? heading = null, double? altitude = null)
        {
            var plr = capi.World.Player.Entity.Pos;
            double ang = heading ?? rand.NextDouble() * Math.PI * 2;
            var dir = new Vec3d(Math.Sin(ang), 0, Math.Cos(ang));
            var perp = new Vec3d(dir.Z, 0, -dir.X);
            int ground = capi.World.BlockAccessor.GetRainMapHeightAt((int)plr.X, (int)plr.Z);
            double y = Math.Min(ground + (altitude ?? sp.AltitudeMin + rand.NextDouble() * (sp.AltitudeMax - sp.AltitudeMin)), capi.World.BlockAccessor.MapSizeY + 40);
            double side = (rand.NextDouble() - 0.5) * 40; // pass slightly off-centre
            // V slots scale with wingspan: each rank sits back by about half a span and out by most of one,
            // so neighbouring wingtips stay just clear of each other
            double span = sp.SpanBlocks, back = span * 0.55, outward = span * 0.8;
            // enter and leave where a bird is ~2 px wide, so the flock shrinks to specks instead of popping in or out
            double reach = VisibleRange(sp), alt = y - plr.Y;
            double horiz = Math.Sqrt(Math.Max(reach * reach - alt * alt, 100 * 100));
            double looseR = span * (1.5 + Math.Sqrt(count)); // a loose group spreads over a disc this wide
            var f = new Flock
            {
                Species = sp, Speed = sp.SpeedMin + rand.NextDouble() * (sp.SpeedMax - sp.SpeedMin),
                BaseHeading = ang, Wander1 = rand.NextDouble() * Math.PI * 2, Wander2 = rand.NextDouble() * Math.PI * 2,
                Pos = new Vec3d(plr.X - dir.X * horiz + perp.X * side, y, plr.Z - dir.Z * horiz + perp.Z * side),
                CruiseY = y, TargetY = y,
            };
            f.Heading = WanderedHeading(f);
            for (int i = 0; i < count; i++)
            {
                double along, up, across;
                if (sp.Loose)
                {
                    // uniform over a disc centred one radius behind the leader, with some height spread
                    double r = looseR * Math.Sqrt(rand.NextDouble()), a = rand.NextDouble() * Math.PI * 2;
                    along = -looseR + r * Math.Cos(a); across = r * Math.Sin(a); up = (rand.NextDouble() - 0.5) * span * 2;
                }
                else
                {
                    int rank = (i + 1) / 2; int s = i == 0 ? 0 : (i % 2 == 0 ? 1 : -1);
                    along = -rank * back + (rand.NextDouble() - 0.5) * span * 0.2;
                    across = s * rank * outward + (rand.NextDouble() - 0.5) * span * 0.2;
                    up = rank * 0.2 + (rand.NextDouble() - 0.5);
                }
                f.Tail = Math.Max(f.Tail, -along); f.HalfWidth = Math.Max(f.HalfWidth, Math.Abs(across)); f.Drop = Math.Max(f.Drop, -up);
                f.Birds.Add(new Bird
                {
                    Offset = new Vec3d(along, up, across), Phase = (float)rand.NextDouble() * 3,
                    YawJitter = (float)((rand.NextDouble() * 2 - 1) * 3 * GameMath.DEG2RAD),
                    FlapBeats = rand.Next(sp.FlapBeatsMin, sp.FlapBeatsMax + 1), SoarLoops = rand.Next(sp.SoarLoopsMin, sp.SoarLoopsMax + 1)
                });
            }
            f.Length = 2 * horiz + f.Tail + span;
            // if it starts over known high ground, start already clear of it rather than climbing out of a hillside
            SampleTerrain(f);
            f.Pos.Y = Math.Max(f.Pos.Y, f.TargetY);
            flocks.Add(f);
        }

        /// <summary>Every CallGapMin-Max seconds one random bird calls, positioned at that bird, quiet and range-limited
        /// so a flock high up and far off is faint or silent. The first call waits a few seconds after the flock appears.</summary>
        private void Call(Flock f)
        {
            var sp = f.Species;
            if (sp.CallSound == null || sp.CallVariants <= 0 || CallVolume <= 0) return;
            if (f.NextCall == 0) f.NextCall = 3 + rand.NextDouble() * sp.CallGapMax;
            if (f.Age < f.NextCall) return;
            f.NextCall = f.Age + sp.CallGapMin + rand.NextDouble() * (sp.CallGapMax - sp.CallGapMin);
            var b = f.Birds[rand.Next(f.Birds.Count)];
            double dx = Math.Sin(f.Heading), dz = Math.Cos(f.Heading);
            double x = f.Pos.X + dx * b.Offset.X + dz * b.Offset.Z, y = f.Pos.Y + b.Offset.Y, z = f.Pos.Z + dz * b.Offset.X - dx * b.Offset.Z;
            var loc = new AssetLocation("flocks", sp.CallSound + (1 + rand.Next(sp.CallVariants)));
            capi.World.PlaySoundAt(loc, x, y, z, null, true, CallRange, 0.6f * CallVolume);
        }

        private double WanderedHeading(Flock f)
            => f.BaseHeading + WanderBig * Math.Sin(f.Age * 0.105 + f.Wander1) + WanderSmall * Math.Sin(f.Age * 0.27 + f.Wander2);

        /// <summary>
        /// Sets TargetY: cruising height, or higher where the ground ahead, under, behind (for the V's tail) or to either
        /// side of the flock needs it. Ground further ahead counts for less by however much the flock can climb before
        /// it gets there, so it starts climbing just early enough. Unloaded chunks read as height 0 and are skipped;
        /// they are too far away to be drawn anyway.
        /// </summary>
        private void SampleTerrain(Flock f)
        {
            var ba = capi.World.BlockAccessor;
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
            f.TargetY = Math.Min(need, ba.MapSizeY + 100);
        }

        private void Move(Flock f, float dt)
        {
            f.Age += dt;
            double h = WanderedHeading(f);
            f.TurnRate = (h - f.Heading) / dt;
            f.Heading = h;
            f.Pos.X += Math.Sin(h) * f.Speed * dt;
            f.Pos.Z += Math.Cos(h) * f.Speed * dt;
            f.Travel += f.Speed * dt;
            if (f.Age >= f.NextSample) { SampleTerrain(f); f.NextSample = f.Age + 0.3; }
            // ease toward the target height: climb briskly, sink back to cruise gently
            double want = GameMath.Clamp((f.TargetY - f.Pos.Y) * 0.4, -MaxSink, MaxClimb);
            f.ClimbRate += (want - f.ClimbRate) * Math.Min(1, dt * 1.5);
            f.Pos.Y += f.ClimbRate * dt;
        }

        /// <summary>
        /// Distance at which one bird covers about 2 pixels of screen height, from the species' wingspan as drawn,
        /// window height and vertical FOV. Clamped to 300-1500 blocks (the game's far plane is 3000).
        /// </summary>
        public double VisibleRange(Species sp)
        {
            int fovDeg = capi.Settings.Int["fieldOfView"];
            double fov = (fovDeg > 0 ? fovDeg : 70) * GameMath.DEG2RAD;
            double pxPerBlockAtOne = capi.Render.FrameHeight / 2.0 / Math.Tan(fov / 2);
            return GameMath.Clamp(sp.SpanBlocks * pxPerBlockAtOne / 2, 300, 1500);
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (flocks.Count == 0) return;
            bool paused = capi.IsGamePaused || dt <= 0; // render frames keep coming while paused; freeze the flock
            var rapi = capi.Render; var cam = capi.World.Player.Entity.CameraPos;
            var prog = rapi.PreparedStandardShader((int)cam.X, (int)cam.Y + 80, (int)cam.Z);
            prog.ViewMatrix = rapi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;
            // Like vanilla's entity pass: wings and tails are single planes that must show from both sides,
            // and their feather edges are transparent pixels that must be cut out rather than drawn solid.
            prog.AlphaTest = 0.05f;
            rapi.GlDisableCullFace();
            for (int fi = flocks.Count - 1; fi >= 0; fi--)
            {
                var f = flocks[fi]; var sp = f.Species;
                if (!paused) { Move(f, dt); Call(f); }
                if (f.Travel > f.Length || !sp.Meshes.Ok) { flocks.RemoveAt(fi); continue; }
                double dx = Math.Sin(f.Heading), dz = Math.Cos(f.Heading);
                // Bank into turns: heading increasing is a left turn, and +RotateX drops the left (+Z) wing.
                // Lift the nose while climbing: +RotateZ drops the head (-X), so climbing gives a negative angle.
                // Both are exaggerated a little so they read from the ground.
                float roll = (float)GameMath.Clamp(f.TurnRate * 8, -0.3, 0.3);
                float pitch = (float)GameMath.Clamp(-f.ClimbRate * 0.05, -0.25, 0.25);
                for (int i = 0; i < f.Birds.Count; i++)
                {
                    var b = f.Birds[i];
                    float t = (float)f.Age + b.Phase;
                    var o = b.Offset;
                    double x = f.Pos.X + dx * o.X + dz * o.Z + Math.Sin(t * 0.7) * 0.4;
                    double y = f.Pos.Y + o.Y + Math.Sin(t * 0.9) * 0.3;
                    double z = f.Pos.Z + dz * o.X - dx * o.Z + Math.Cos(t * 0.6) * 0.4;
                    var mesh = PoseFor(sp, b, t);
                    // bird shapes have their head at -X; RotateY(a) turns -X into (-cos a, sin a), which equals dir for a = heading + pi/2
                    float yaw = (float)(f.Heading + Math.PI / 2 + b.YawJitter * Math.Sin(t * 0.4));
                    prog.ModelMatrix = mat.Identity().Translate(x - cam.X, y - cam.Y, z - cam.Z).RotateY(yaw).RotateX(roll).RotateZ(pitch)
                        .Scale(sp.Scale, sp.Scale, sp.Scale).Values;
                    rapi.RenderMultiTextureMesh(mesh, "tex");
                }
            }
            rapi.GlEnableCullFace();
            prog.Stop();
        }

        /// <summary>
        /// Per-bird loop: FlapBeats whole wingbeats, ease into the glide, SoarLoops whole soar loops, ease back.
        /// Every segment ends on frame 0 of its cycle, which is where the blend meshes start and end, so there is no pop.
        /// </summary>
        private static MultiTextureMeshRef PoseFor(Species sp, Bird b, float t)
        {
            var m = sp.Meshes;
            float flap = b.FlapBeats * sp.FlapSeconds, soar = b.SoarLoops * sp.SoarSeconds, blend = sp.BlendSeconds;
            float u = t % (flap + soar + 2 * blend);
            if (u < flap) return Cycle(m.Flap, u / sp.FlapSeconds);
            u -= flap;
            if (u < blend) return Ease(m.Blend, u / blend);
            u -= blend;
            if (u < soar) return Cycle(m.Soar, u / sp.SoarSeconds);
            u -= soar;
            return Ease(m.Blend, 1 - u / blend);
        }

        private static MultiTextureMeshRef Cycle(MultiTextureMeshRef[] frames, float cycles)
            => frames[(int)((cycles - Math.Floor(cycles)) * frames.Length) % frames.Length];

        private static MultiTextureMeshRef Ease(MultiTextureMeshRef[] blend, float w)
        {
            w = GameMath.Clamp(w, 0, 1); w = w * w * (3 - 2 * w);
            return blend[(int)Math.Round(w * (blend.Length - 1))];
        }

        public void Dispose() { }
    }
}
