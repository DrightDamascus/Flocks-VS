using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// Client: draws the passing flocks the server reports (SkyFlocks) with baked bird meshes. Between the server's
    /// updates (once a second) each flock flies on here as it does there (SkyFlight, without the terrain: the server's
    /// target height stands in), and each update's difference is eased out over a second or two. A bird whose real
    /// entity is loaded here isn't drawn: the game draws that one.
    /// </summary>
    public class FlockRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly Dictionary<string, Species> species;
        private readonly List<Flock> flocks = new();
        private readonly Dictionary<long, (Vec3d error, double heard)> sync = new();
        private readonly Random rand = new();
        private readonly Matrixf mat = new();
        private static readonly List<Flock> NoOthers = new();

        /// <summary>How loud the passing flocks' calls are: the client config's FlockCallVolume, 0 for none.</summary>
        public readonly float CallVolume;
        /// <summary>A call is heard out to this many blocks from the bird, growing fainter the whole way.</summary>
        public float CallRange = 160;
        /// <summary>A flock the server has said nothing of for this long (seconds) has gone out of range: dropped.</summary>
        private const double Silence = 10;

        /// <summary>The landed goose entity's default size (entities/greylag.json client.size); the sky goose uses the
        /// loaded value, so the two always match.</summary>
        public const double EntityScale = 0.98;

        public FlockRenderer(ICoreClientAPI capi, Dictionary<string, Species> species, float callVolume = 1f)
        {
            this.capi = capi; this.species = species;
            CallVolume = Math.Max(0f, callVolume);
        }

        public double RenderOrder => 0.45;
        public int RenderRange => 400;
        public int Count => flocks.Count;

        /// <summary>Whether the flock is still in the sky (the flock camera follows it until it isn't).</summary>
        public bool Has(Flock f) => flocks.Contains(f);
        public Flock Get(long id) => flocks.Find(f => f.Id == id);

        /// <summary>The server's word on a flock: a new one is set up as it is there; a known one takes the new state
        /// (formation, which birds are real) and is eased toward the reported position.</summary>
        public void Apply(SkyFlockPacket pkt)
        {
            var f = Get(pkt.Id);
            if (pkt.Gone) { if (f != null) { flocks.Remove(f); sync.Remove(f.Id); } return; }
            if (!species.TryGetValue(pkt.Species ?? "", out var sp)) return;
            var at = new Vec3d(pkt.X, pkt.Y, pkt.Z);
            if (f == null)
            {
                f = new Flock { Id = pkt.Id, Seed = pkt.Seed, Species = sp, Pos = at };
                flocks.Add(f);
            }
            f.BaseHeading = pkt.BaseHeading; f.Wander1 = pkt.Wander1; f.Wander2 = pkt.Wander2;
            f.Speed = pkt.Speed; f.ClimbRate = pkt.ClimbRate; f.TargetY = pkt.TargetY;
            f.Travel = pkt.Travel; f.Length = pkt.Length;
            if (Math.Abs(f.Age - pkt.Age) > 0.5) f.Age = pkt.Age;
            f.Heading = SkyFlight.WanderedHeading(f);
            // the gap to where the server has it: small ones eased out, big ones (just heard of it) jumped
            var error = at - f.Pos;
            if (error.Length() > 40) { f.Pos = at; error = new Vec3d(); }
            sync[f.Id] = (error, capi.World.ElapsedMilliseconds / 1000.0);
            int n = (pkt.Slots?.Length ?? 0) / 3;
            if (f.Birds.Count != n) { f.Birds.Clear(); for (int i = 0; i < n; i++) f.Birds.Add(new Bird()); }
            f.Tail = f.HalfWidth = f.Drop = f.Rise = 0;
            for (int i = 0; i < n; i++)
            {
                var b = f.Birds[i];
                b.Offset = new Vec3d(pkt.Slots[i * 3], pkt.Slots[i * 3 + 1], pkt.Slots[i * 3 + 2]);
                b.EntityId = pkt.Entities != null && i < pkt.Entities.Length ? pkt.Entities[i] : 0;
                SkyFlight.Extend(f, b.Offset);
            }
            SkyFlight.Dress(f);
        }

        /// <summary>Leaving the world: the server will tell the next one afresh.</summary>
        public void Clear() { flocks.Clear(); sync.Clear(); }

        /// <summary>
        /// Now and then one bird of the flock calls, from where it is in the formation: every CallGapMin to CallGapMax
        /// seconds, the first a few seconds after the flock shows up. Calls are quiet and carry only CallRange blocks, so
        /// a flock that is high up or far off is faint or not heard at all. Real birds call this way too (they make no
        /// idle calls of their own while in a passing flock).
        /// </summary>
        private void Call(Flock f)
        {
            Species sp = f.Species;
            bool silent = sp.CallSound == null || sp.CallVariants <= 0 || CallVolume <= 0 || f.Birds.Count == 0;
            if (silent) return;
            if (f.NextCall == 0) f.NextCall = f.Age + 3 + rand.NextDouble() * sp.CallGapMax;   // the first call
            if (f.NextCall > f.Age) return;
            f.NextCall = f.Age + sp.CallGapMin + rand.NextDouble() * (sp.CallGapMax - sp.CallGapMin);
            Bird caller = f.Birds[rand.Next(f.Birds.Count)];
            var real = caller.EntityId == 0 ? null : capi.World.GetEntityById(caller.EntityId);
            Vec3d at = real?.Pos.XYZ ?? f.SlotInWorld(caller.Offset);
            var sound = new AssetLocation("flocks", sp.CallSound + (1 + rand.Next(sp.CallVariants)));
            capi.World.PlaySoundAt(sound, at.X, at.Y, at.Z, null, true, CallRange, 0.6f * CallVolume);
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (flocks.Count == 0) return;
            bool paused = capi.IsGamePaused || dt <= 0; // render frames keep coming while paused; freeze the flock
            double now = capi.World.ElapsedMilliseconds / 1000.0;
            IRenderAPI rapi = capi.Render;
            Vec3d cam = capi.World.Player.Entity.CameraPos;
            // lit as the open sky over the camera is (that is where the birds are), not as the ground under it
            int skyY = (int)cam.Y + 80;
            var prog = rapi.PreparedStandardShader((int)cam.X, skyY, (int)cam.Z);
            prog.ViewMatrix = rapi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;
            // Like vanilla's entity pass: wings and tails are single planes that must show from both sides,
            // and their feather edges are transparent pixels that must be cut out rather than drawn solid.
            prog.AlphaTest = 0.05f;
            rapi.GlDisableCullFace();
            for (int fi = flocks.Count - 1; fi >= 0; fi--)
            {
                var f = flocks[fi]; var sp = f.Species;
                sync.TryGetValue(f.Id, out var s);
                if (now - s.heard > Silence || sp.Meshes?.Ok != true) { flocks.RemoveAt(fi); sync.Remove(f.Id); continue; }
                if (!paused)
                {
                    SkyFlight.Move(f, dt, null, NoOthers);
                    if (s.error != null)
                    {
                        double k = 1 - Math.Exp(-dt * 1.5);
                        f.Pos.Add(s.error.X * k, s.error.Y * k, s.error.Z * k);
                        s.error.Mul(1 - k);
                    }
                    Call(f);
                }
                double dx = Math.Sin(f.Heading), dz = Math.Cos(f.Heading);
                // Bank into turns: heading increasing is a left turn, and +RotateX drops the left (+Z) wing.
                // Lift the nose while climbing: +RotateZ drops the head (-X), so climbing gives a negative angle.
                // Both are exaggerated a little so they read from the ground.
                float roll = (float)GameMath.Clamp(f.TurnRate * 8, -0.3, 0.3);
                float pitch = (float)GameMath.Clamp(-f.ClimbRate * 0.05, -0.25, 0.25);
                foreach (Bird b in f.Birds)
                {
                    // a real bird loaded here is drawn by the game itself
                    if (b.EntityId != 0 && capi.World.GetEntityById(b.EntityId) != null) continue;
                    float t = b.Phase + (float)f.Age;
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
