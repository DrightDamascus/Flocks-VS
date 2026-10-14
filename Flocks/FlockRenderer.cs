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
        public float SoarCycle;
    }

    public class Flock
    {
        public Vec3d Start; public Vec3d Dir; public double Speed; public double Travel; public double Length;
        public float Yaw; public List<Bird> Birds = new();
        public double Age;
    }

    /// <summary>Client-only renderer: draws baked hawk meshes at flock positions. No entities, nothing sent to the server.</summary>
    public class FlockRenderer : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly BirdMeshes meshes;
        private readonly List<Flock> flocks = new();
        private readonly Random rand = new();
        private readonly Matrixf mat = new();
        public float BirdScale = 3f;

        public FlockRenderer(ICoreClientAPI capi, BirdMeshes meshes) { this.capi = capi; this.meshes = meshes; }
        public double RenderOrder => 0.45;
        public int RenderRange => 400;
        public int Count => flocks.Count;

        public void Send(int count)
        {
            var plr = capi.World.Player.Entity.Pos;
            double ang = rand.NextDouble() * Math.PI * 2;
            var dir = new Vec3d(Math.Sin(ang), 0, Math.Cos(ang));
            var perp = new Vec3d(dir.Z, 0, -dir.X);
            int ground = capi.World.BlockAccessor.GetRainMapHeightAt((int)plr.X, (int)plr.Z);
            double y = Math.Min(ground + 60 + rand.NextDouble() * 60, capi.World.BlockAccessor.MapSizeY + 40);
            double side = (rand.NextDouble() - 0.5) * 40; // pass slightly off-centre
            var f = new Flock
            {
                Dir = dir, Speed = 18 + rand.NextDouble() * 6, Length = 400,
                Start = new Vec3d(plr.X - dir.X * 190 + perp.X * side, y, plr.Z - dir.Z * 190 + perp.Z * side),
                // hawk shape has its head at -X; RotateY(a) turns -X into (-cos a, sin a), which equals dir for a = atan2(x, z) + pi/2
                Yaw = (float)(Math.Atan2(dir.X, dir.Z) + Math.PI / 2)
            };
            for (int i = 0; i < count; i++)
            {
                int rank = (i + 1) / 2; int s = i == 0 ? 0 : (i % 2 == 0 ? 1 : -1);
                var off = new Vec3d(-dir.X * rank * 3.5 + perp.X * s * rank * 3, rank * 0.2 + (rand.NextDouble() - 0.5), -dir.Z * rank * 3.5 + perp.Z * s * rank * 3);
                off.Add((rand.NextDouble() - 0.5) * 1.2, 0, (rand.NextDouble() - 0.5) * 1.2);
                f.Birds.Add(new Bird { Offset = off, Phase = (float)rand.NextDouble() * 3, SoarCycle = 4 + (float)rand.NextDouble() * 4 });
            }
            flocks.Add(f);
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (flocks.Count == 0 || !meshes.Ok) return;
            if (capi.IsGamePaused) dt = 0; // render frames keep coming while paused; freeze the flock
            var rapi = capi.Render; var cam = capi.World.Player.Entity.CameraPos;
            var prog = rapi.PreparedStandardShader((int)cam.X, (int)cam.Y + 80, (int)cam.Z);
            prog.ViewMatrix = rapi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;
            for (int fi = flocks.Count - 1; fi >= 0; fi--)
            {
                var f = flocks[fi];
                f.Age += dt; f.Travel = f.Age * f.Speed;
                if (f.Travel > f.Length) { flocks.RemoveAt(fi); continue; }
                for (int i = 0; i < f.Birds.Count; i++)
                {
                    var b = f.Birds[i];
                    float t = (float)f.Age + b.Phase;
                    double x = f.Start.X + f.Dir.X * f.Travel + b.Offset.X + Math.Sin(t * 0.7) * 0.4;
                    double y = f.Start.Y + b.Offset.Y + Math.Sin(t * 0.9) * 0.3;
                    double z = f.Start.Z + f.Dir.Z * f.Travel + b.Offset.Z + Math.Cos(t * 0.6) * 0.4;
                    bool soaring = (t % b.SoarCycle) > b.SoarCycle * 0.55f;
                    var mesh = soaring ? meshes.Soar : meshes.Flap[(int)(t * 30f / 30f * BirdMeshes.FlapFrames) % BirdMeshes.FlapFrames];
                    prog.ModelMatrix = mat.Identity().Translate(x - cam.X, y - cam.Y, z - cam.Z).RotateY(f.Yaw).Scale(BirdScale, BirdScale, BirdScale).Values;
                    rapi.RenderMultiTextureMesh(mesh, "tex");
                }
            }
            prog.Stop();
        }

        public void Dispose() { }
    }
}
