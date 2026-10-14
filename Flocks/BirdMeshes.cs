using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>Bakes hawk fly-flap / fly-soar poses into static meshes (one per frame) so no animator or entity is needed.</summary>
    public class BirdMeshes : ITexPositionSource
    {
        public static readonly AssetLocation HawkShape = new AssetLocation("game", "shapes/entity/animal/bird/raptor/hawk/hawk.json");
        public const int FlapFrames = 10;

        private readonly ICoreClientAPI capi;
        private readonly Dictionary<string, AssetLocation> textures = new();
        public MultiTextureMeshRef[] Flap;
        public MultiTextureMeshRef Soar;
        public bool Ok => Flap != null && Soar != null;

        public BirdMeshes(ICoreClientAPI capi) { this.capi = capi; }

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
        public string Loaded { get; private set; }

        private static readonly (string name, string shape, string flap, string soar, string texOverride)[] Candidates =
        {
            ("hawk", "shapes/entity/animal/bird/raptor/hawk/hawk.json", "fly-flap", "fly-soar", null),
            // the sparrow shape points at bird/sparrow/..., but the file ships in bird/passerine/
            ("treesparrow", "shapes/entity/animal/bird/passerine/treesparrow-adult-male.json", "flap", "soar", "entity/animal/bird/passerine/treesparrow-adult-male"),
            ("chicken", "shapes/entity/animal/bird/chicken/chicken-hen.json", "fly", "fly", null),
        };

        public void Load()
        {
            if (Ok) return;
            foreach (var c in Candidates)
            {
                try { if (TryLoad(c.name, c.shape, c.flap, c.soar, c.texOverride)) return; }
                catch (Exception ex) { capi.Logger.Error("[Flocks] {0} mesh bake threw: {1}", c.name, ex); Dispose(); Flap = null; Soar = null; }
            }
            capi.Logger.Error("[Flocks] no bird shape could be loaded (tried hawk, treesparrow, chicken); /flocks send disabled");
        }

        private bool TryLoad(string name, string path, string flapCode, string soarCode, string texOverride)
        {
            shapeLoc = new AssetLocation("game", path);
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
            Animation flap = Find(baseShape, flapCode);
            Animation soar = Find(baseShape, soarCode);
            Flap = new MultiTextureMeshRef[FlapFrames];
            for (int i = 0; i < FlapFrames; i++)
                Flap[i] = Bake(flap, flap == null ? 0 : i * flap.QuantityFrames / (float)FlapFrames);
            Soar = Bake(soar, 0);
            Loaded = name;
            capi.Logger.Notification("[Flocks] baked {0} meshes from {1}: {2} flap frames, flap anim {3}, soar anim {4}, textures {5}",
                name, shapeLoc, FlapFrames, flap != null, soar != null, string.Join(",", textures.Values));
            return true;
        }
        private static AssetLocation TexFile(AssetLocation loc) => loc.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");

        private static Animation Find(Shape s, string code)
        {
            if (s.Animations == null) return null;
            foreach (var a in s.Animations) if (a.Code == code) return a;
            return null;
        }

        private MultiTextureMeshRef Bake(Animation anim, float frame)
        {
            Shape shape = Shape.TryGet(capi, shapeLoc); // fresh copy per pose
            if (anim?.KeyFrames != null && anim.KeyFrames.Length > 0)
            {
                var byName = new Dictionary<string, ShapeElement>();
                Index(shape.Elements, byName);
                foreach (var name in byName.Keys)
                {
                    if (!Sample(anim, name, frame, out double rx, out double ry, out double rz)) continue;
                    var el = byName[name];
                    el.RotationX += rx; el.RotationY += ry; el.RotationZ += rz;
                }
            }
            capi.Tesselator.TesselateShape("flocks-hawk", shape, out MeshData mesh, this);
            mesh.Translate(-0.5f, 0, -0.5f);
            return capi.Render.UploadMultiTextureMesh(mesh);
        }

        private static void Index(ShapeElement[] els, Dictionary<string, ShapeElement> d)
        {
            if (els == null) return;
            foreach (var e in els) { if (e.Name != null) d[e.Name] = e; Index(e.Children, d); }
        }

        /// <summary>Linear interpolation of rotation between the keyframes that mention this element (wrapping).</summary>
        private static bool Sample(Animation anim, string name, float frame, out double rx, out double ry, out double rz)
        {
            rx = ry = rz = 0;
            var keys = new List<(float f, AnimationKeyFrameElement e)>();
            foreach (var kf in anim.KeyFrames)
                if (kf.Elements != null && kf.Elements.TryGetValue(name, out var e) && e != null && (e.RotationX != null || e.RotationY != null || e.RotationZ != null))
                    keys.Add((kf.Frame, e));
            if (keys.Count == 0) return false;
            int n = anim.QuantityFrames > 0 ? anim.QuantityFrames : 30;
            var a = keys[keys.Count - 1]; var b = keys[0];
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].f <= frame) a = keys[i];
                if (keys[i].f > frame) { b = keys[i]; break; }
                if (i == keys.Count - 1) b = keys[0];
            }
            float span = b.f - a.f; if (span <= 0) span += n;
            float t = frame - a.f; if (t < 0) t += n;
            float w = span <= 0 ? 0 : GameMath.Clamp(t / span, 0, 1);
            rx = Lerp(a.e.RotationX, b.e.RotationX, w); ry = Lerp(a.e.RotationY, b.e.RotationY, w); rz = Lerp(a.e.RotationZ, b.e.RotationZ, w);
            return true;
        }

        private static double Lerp(double? a, double? b, float w) => (a ?? 0) + ((b ?? 0) - (a ?? 0)) * w;

        public void Dispose()
        {
            if (Flap != null) foreach (var m in Flap) m?.Dispose();
            Soar?.Dispose();
        }
    }
}


