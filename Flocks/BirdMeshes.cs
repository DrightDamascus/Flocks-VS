using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// Bakes a bird shape's flap / soar poses into static meshes so no animator or entity is needed: a full flap cycle,
    /// a full soar cycle, and blend poses from flap frame 0 to soar frame 0 so birds ease between flapping and gliding.
    /// Keyframes are joined with a cyclic Catmull-Rom spline instead of straight lines, so wings do not snap at the
    /// top and bottom of a stroke (the hawk flap has only three keyframes).
    /// </summary>
    public class BirdMeshes : ITexPositionSource
    {
        public const int FlapFrames = 48, SoarFrames = 32, BlendFrames = 12;

        private readonly ICoreClientAPI capi;
        private readonly Dictionary<string, AssetLocation> textures = new();
        public MultiTextureMeshRef[] Flap, Soar, Blend;
        public bool Ok => Flap != null && Soar != null && Blend != null;

        /// <summary>A shape to try; texOverride replaces a texture path the shape gets wrong.</summary>
        public record Candidate(string Name, AssetLocation Shape, string Flap, string Soar, string TexOverride = null);

        private readonly string species;
        private readonly Candidate[] candidates;

        public BirdMeshes(ICoreClientAPI capi, string species, params Candidate[] candidates)
        {
            this.capi = capi; this.species = species; this.candidates = candidates;
        }

        public Size2i AtlasSize => capi.EntityTextureAtlas.Size;

        public TextureAtlasPosition this[string code]
        {
            get
            {
                if (!textures.TryGetValue(code, out var loc)) foreach (var v in textures.Values) { loc = v; break; }
                if (loc == null) return capi.EntityTextureAtlas.UnknownTexturePosition;
                capi.EntityTextureAtlas.GetOrInsertTexture(loc, out _, out var pos);
                return pos ?? capi.EntityTextureAtlas.UnknownTexturePosition;
            }
        }

        private AssetLocation shapeLoc;
        private Shape shape;
        private readonly Dictionary<string, ShapeElement> byName = new();
        private readonly Dictionary<string, Rot> baseRot = new();
        public string Loaded { get; private set; }
        /// <summary>Largest width of any baked pose (wingspan, or body length if larger), in blocks before BirdScale.</summary>
        public float Span { get; private set; }

        private struct Rot
        {
            public double X, Y, Z;
            public Rot(double x, double y, double z) { X = x; Y = y; Z = z; }
        }

        public void Load()
        {
            if (Ok) return;
            foreach (var c in candidates)
            {
                try { if (TryLoad(c.Name, c.Shape, c.Flap, c.Soar, c.TexOverride)) return; }
                catch (Exception ex) { capi.Logger.Error("[Flocks] {0} mesh bake threw: {1}", c.Name, ex); Dispose(); Flap = null; Soar = null; Blend = null; }
            }
            capi.Logger.Error("[Flocks] no shape could be loaded for {0} (tried {1}); that species is disabled",
                species, string.Join(", ", Array.ConvertAll(candidates, c => c.Name)));
        }

        private bool TryLoad(string name, AssetLocation path, string flapCode, string soarCode, string texOverride)
        {
            shapeLoc = path;
            Shape baseShape = Shape.TryGet(capi, shapeLoc);
            if (baseShape == null) { capi.Logger.Warning("[Flocks] {0}: shape not found at {1}", name, shapeLoc); return false; }
            textures.Clear();
            if (baseShape.Textures == null || baseShape.Textures.Count == 0) { capi.Logger.Warning("[Flocks] {0}: shape has no textures", name); return false; }
            foreach (var kv in baseShape.Textures)
            {
                // the atlas adds "textures/" and ".png" itself, so keep the bare path for it and only check the full one
                var loc = kv.Value.Clone();
                if (!capi.Assets.Exists(TexFile(loc)) && texOverride != null) loc = new AssetLocation("game", texOverride);
                if (!capi.Assets.Exists(TexFile(loc))) { capi.Logger.Warning("[Flocks] {0}: texture '{1}' missing at {2}", name, kv.Key, TexFile(loc)); return false; }
                textures[kv.Key] = loc;
            }
            // one shape object for every pose: each bake sets absolute rotations (base + animation), so nothing accumulates
            shape = baseShape;
            byName.Clear(); baseRot.Clear(); Span = 0;
            Index(shape.Elements, byName);
            foreach (var kv in byName) baseRot[kv.Key] = new Rot(kv.Value.RotationX, kv.Value.RotationY, kv.Value.RotationZ);
            Animation flap = Find(baseShape, flapCode);
            Animation soar = Find(baseShape, soarCode);
            Flap = BakeCycle(flap, FlapFrames);
            Soar = BakeCycle(soar, SoarFrames);
            var from = Pose(flap, 0); var to = Pose(soar, 0);
            Blend = new MultiTextureMeshRef[BlendFrames];
            for (int i = 0; i < BlendFrames; i++) Blend[i] = Bake(Mix(from, to, i / (float)(BlendFrames - 1)));
            Loaded = name;
            capi.Logger.Notification("[Flocks] baked {0} meshes from {1}: {2} flap, {3} soar, {4} blend frames, flap anim {5}, soar anim {6}, textures {7}",
                name, shapeLoc, FlapFrames, SoarFrames, BlendFrames, flap != null, soar != null, string.Join(",", textures.Values));
            return true;
        }

        private static AssetLocation TexFile(AssetLocation loc) => loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");

        private static Animation Find(Shape s, string code)
        {
            if (s.Animations == null) return null;
            foreach (var a in s.Animations) if (a.Code == code) return a;
            return null;
        }

        private MultiTextureMeshRef[] BakeCycle(Animation anim, int frames)
        {
            var meshes = new MultiTextureMeshRef[frames];
            int n = anim != null && anim.QuantityFrames > 0 ? anim.QuantityFrames : 30;
            for (int i = 0; i < frames; i++) meshes[i] = Bake(Pose(anim, i * n / (float)frames));
            return meshes;
        }

        /// <summary>Animation rotation per element at a (fractional) frame; elements the animation never touches are left out.</summary>
        private Dictionary<string, Rot> Pose(Animation anim, float frame)
        {
            var pose = new Dictionary<string, Rot>();
            if (anim?.KeyFrames == null || anim.KeyFrames.Length == 0) return pose;
            foreach (var name in byName.Keys)
                if (Sample(anim, name, frame, out var r)) pose[name] = r;
            return pose;
        }

        private static Dictionary<string, Rot> Mix(Dictionary<string, Rot> a, Dictionary<string, Rot> b, float w)
        {
            var pose = new Dictionary<string, Rot>();
            var names = new HashSet<string>(a.Keys); names.UnionWith(b.Keys);
            foreach (var k in names)
            {
                a.TryGetValue(k, out var p); b.TryGetValue(k, out var q);
                pose[k] = new Rot(p.X + (q.X - p.X) * w, p.Y + (q.Y - p.Y) * w, p.Z + (q.Z - p.Z) * w);
            }
            return pose;
        }

        private MultiTextureMeshRef Bake(Dictionary<string, Rot> pose)
        {
            foreach (var kv in byName)
            {
                var r = baseRot[kv.Key];
                if (pose.TryGetValue(kv.Key, out var d)) r = new Rot(r.X + d.X, r.Y + d.Y, r.Z + d.Z);
                kv.Value.RotationX = r.X; kv.Value.RotationY = r.Y; kv.Value.RotationZ = r.Z;
            }
            capi.Tesselator.TesselateShape("flocks-" + species, shape, out MeshData mesh, this);
            mesh.Translate(-0.5f, 0, -0.5f);
            Span = Math.Max(Span, Extent(mesh, 0)); Span = Math.Max(Span, Extent(mesh, 2));
            return capi.Render.UploadMultiTextureMesh(mesh);
        }

        /// <summary>Size of the mesh along one axis (0 = x, 2 = z), in blocks.</summary>
        private static float Extent(MeshData mesh, int axis)
        {
            float min = float.MaxValue, max = float.MinValue;
            for (int i = axis; i < mesh.XyzCount; i += 3) { min = Math.Min(min, mesh.xyz[i]); max = Math.Max(max, mesh.xyz[i]); }
            return max > min ? max - min : 0;
        }

        private static void Index(ShapeElement[] els, Dictionary<string, ShapeElement> d)
        {
            if (els == null) return;
            foreach (var e in els) { if (e.Name != null) d[e.Name] = e; Index(e.Children, d); }
        }

        /// <summary>Cyclic Catmull-Rom through the keyframes that mention this element, so motion eases through each key.</summary>
        private static bool Sample(Animation anim, string name, float frame, out Rot r)
        {
            r = default;
            var keys = new List<(float f, AnimationKeyFrameElement e)>();
            foreach (var kf in anim.KeyFrames)
                if (kf.Elements != null && kf.Elements.TryGetValue(name, out var e) && e != null && (e.RotationX != null || e.RotationY != null || e.RotationZ != null))
                    keys.Add((kf.Frame, e));
            if (keys.Count == 0) return false;
            keys.Sort((p, q) => p.f.CompareTo(q.f));
            int n = anim.QuantityFrames > 0 ? anim.QuantityFrames : 30;
            int c = keys.Count;
            // segment i runs from keys[i] to keys[i+1] (wrapping); frames before the first key belong to the last segment
            int i = c - 1;
            for (int k = 0; k < c; k++) if (keys[k].f <= frame) i = k;
            var k0 = keys[(i - 1 + c) % c].e; var k1 = keys[i].e; var k2 = keys[(i + 1) % c].e; var k3 = keys[(i + 2) % c].e;
            float span = keys[(i + 1) % c].f - keys[i].f; if (span <= 0) span += n;
            float t = frame - keys[i].f; if (t < 0) t += n;
            float w = GameMath.Clamp(t / span, 0, 1);
            r = new Rot(Spline(k0.RotationX, k1.RotationX, k2.RotationX, k3.RotationX, w),
                        Spline(k0.RotationY, k1.RotationY, k2.RotationY, k3.RotationY, w),
                        Spline(k0.RotationZ, k1.RotationZ, k2.RotationZ, k3.RotationZ, w));
            return true;
        }

        private static double Spline(double? a, double? b, double? c, double? d, float t)
        {
            double p0 = a ?? 0, p1 = b ?? 0, p2 = c ?? 0, p3 = d ?? 0;
            return 0.5 * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (3 * p1 - p0 - 3 * p2 + p3) * t * t * t);
        }

        public void Dispose()
        {
            foreach (var set in new[] { Flap, Soar, Blend }) if (set != null) foreach (var m in set) m?.Dispose();
        }
    }
}
