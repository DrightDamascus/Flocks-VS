using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Flocks
{
    public class Bird
    {
        public Vec3d Offset; // slot relative to leader (blocks)
        public float Phase;  // animation phase offset (s)
        public int FlapBeats, SoarLoops; // wingbeats before gliding, soar loops before flapping again
    }

    public class Flock
    {
        public Species Species;
        public Vec3d Start; public Vec3d Dir; public double Speed; public double Travel; public double Length;
        public float Yaw; public List<Bird> Birds = new();
        public double Age;
    }

    /// <summary>Client-only renderer: draws baked bird meshes at flock positions. No entities, nothing sent to the server.</summary>
    public class FlockRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly List<Flock> flocks = new();
        private readonly Random rand = new();
        private readonly Matrixf mat = new();

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
            double y = Math.Min(ground + (altitude ?? 60 + rand.NextDouble() * 60), capi.World.BlockAccessor.MapSizeY + 40);
            double side = (rand.NextDouble() - 0.5) * 40; // pass slightly off-centre
            // V slots scale with wingspan: each rank sits back by about half a span and out by most of one,
            // so neighbouring wingtips stay just clear of each other
            double span = sp.SpanBlocks, back = span * 0.55, outward = span * 0.8;
            // enter and leave where a bird is ~2 px wide, so the flock shrinks to specks instead of popping in or out
            double reach = VisibleRange(sp), alt = y - plr.Y;
            double horiz = Math.Sqrt(Math.Max(reach * reach - alt * alt, 100 * 100));
            double looseR = span * (1.5 + Math.Sqrt(count)); // a loose group spreads over a disc this wide
            double tail = sp.Loose ? 2 * looseR : (count / 2) * back + span; // the last bird trails the leader by this much
            var f = new Flock
            {
                Species = sp, Dir = dir, Speed = sp.SpeedMin + rand.NextDouble() * (sp.SpeedMax - sp.SpeedMin), Length = 2 * horiz + tail,
                Start = new Vec3d(plr.X - dir.X * horiz + perp.X * side, y, plr.Z - dir.Z * horiz + perp.Z * side),
                // bird shapes have their head at -X; RotateY(a) turns -X into (-cos a, sin a), which equals dir for a = atan2(x, z) + pi/2
                Yaw = (float)(Math.Atan2(dir.X, dir.Z) + Math.PI / 2)
            };
            for (int i = 0; i < count; i++)
            {
                Vec3d off;
                if (sp.Loose)
                {
                    // uniform over a disc centred one radius behind the leader, with some height spread
                    double r = looseR * Math.Sqrt(rand.NextDouble()), a = rand.NextDouble() * Math.PI * 2;
                    double along = -looseR + r * Math.Cos(a), across = r * Math.Sin(a);
                    off = new Vec3d(dir.X * along + perp.X * across, (rand.NextDouble() - 0.5) * span * 2, dir.Z * along + perp.Z * across);
                }
                else
                {
                    int rank = (i + 1) / 2; int s = i == 0 ? 0 : (i % 2 == 0 ? 1 : -1);
                    off = new Vec3d(-dir.X * rank * back + perp.X * s * rank * outward, rank * 0.2 + (rand.NextDouble() - 0.5), -dir.Z * rank * back + perp.Z * s * rank * outward);
                    off.Add((rand.NextDouble() - 0.5) * span * 0.2, 0, (rand.NextDouble() - 0.5) * span * 0.2);
                }
                f.Birds.Add(new Bird
                {
                    Offset = off, Phase = (float)rand.NextDouble() * 3,
                    FlapBeats = rand.Next(sp.FlapBeatsMin, sp.FlapBeatsMax + 1), SoarLoops = rand.Next(sp.SoarLoopsMin, sp.SoarLoopsMax + 1)
                });
            }
            flocks.Add(f);
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
            if (capi.IsGamePaused) dt = 0; // render frames keep coming while paused; freeze the flock
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
                f.Age += dt; f.Travel = f.Age * f.Speed;
                if (f.Travel > f.Length || !sp.Meshes.Ok) { flocks.RemoveAt(fi); continue; }
                for (int i = 0; i < f.Birds.Count; i++)
                {
                    var b = f.Birds[i];
                    float t = (float)f.Age + b.Phase;
                    double x = f.Start.X + f.Dir.X * f.Travel + b.Offset.X + Math.Sin(t * 0.7) * 0.4;
                    double y = f.Start.Y + b.Offset.Y + Math.Sin(t * 0.9) * 0.3;
                    double z = f.Start.Z + f.Dir.Z * f.Travel + b.Offset.Z + Math.Cos(t * 0.6) * 0.4;
                    var mesh = PoseFor(sp, b, t);
                    prog.ModelMatrix = mat.Identity().Translate(x - cam.X, y - cam.Y, z - cam.Z).RotateY(f.Yaw).Scale(sp.Scale, sp.Scale, sp.Scale).Values;
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
