using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Flocks
{
    /// <summary>How one kind of bird looks and moves: its baked meshes, size, speed and flap/glide rhythm.</summary>
    public class Species
    {
        public string Code;
        public BirdMeshes Meshes;
        public float Scale = 3f;
        public double SpeedMin = 18, SpeedMax = 24;
        /// <summary>One wingbeat, one soar loop, one flap&lt;-&gt;glide ease (seconds).</summary>
        public float FlapSeconds = 0.8f, SoarSeconds = 1.6f, BlendSeconds = 0.4f;
        /// <summary>Each bird flaps FlapBeats (min..max inclusive) then glides SoarLoops, and repeats.</summary>
        public int FlapBeatsMin = 3, FlapBeatsMax = 7, SoarLoopsMin = 1, SoarLoopsMax = 3;

        /// <summary>Migration windows as seasonRel (0 = start of spring, hemisphere-local); weight scales each window.</summary>
        public float SpringStart = 0.05f, SpringEnd = 0.25f, AutumnStart = 0.55f, AutumnEnd = 0.80f;
        public float SpringWeight = 1f, AutumnWeight = 1f;
        /// <summary>Game hours they fly, as start/end pairs.</summary>
        public float[] Hours = { 7, 10, 15, 19 };
        /// <summary>Flocks on a peak migration day (fractions round randomly); fewer toward the edges of a window.</summary>
        public float MaxFlocksPerDay = 2f;
        public int CountMin = 7, CountMax = 25;
        /// <summary>Cruising height above the player's ground, blocks (picked per flock).</summary>
        public double AltitudeMin = 60, AltitudeMax = 120;
        /// <summary>V formation, or a loose scattered group.</summary>
        public bool Loose;

        /// <summary>Wingspan in blocks as drawn; formation spacing scales from it so wingtips never overlap.</summary>
        public double SpanBlocks => (Meshes.Span > 0 ? Meshes.Span : 1) * Scale;

        public static Dictionary<string, Species> Create(ICoreClientAPI capi) => new()
        {
            // Our own model (tools/make_greylag.py). Geese flap steadily with short glides.
            ["goose"] = new Species
            {
                Code = "goose", Scale = 2.5f, SpeedMin = 16, SpeedMax = 21,
                FlapSeconds = 0.5f, SoarSeconds = 2f, BlendSeconds = 0.35f,
                FlapBeatsMin = 8, FlapBeatsMax = 16, SoarLoopsMin = 1, SoarLoopsMax = 1,
                // geese move late in autumn and early in spring, mornings and late afternoons, in V's of 7-25
                SpringStart = 0.03f, SpringEnd = 0.22f, AutumnStart = 0.62f, AutumnEnd = 0.85f,
                Hours = new float[] { 6.5f, 10, 15.5f, 19 }, MaxFlocksPerDay = 3, CountMin = 7, CountMax = 25,
                Meshes = new BirdMeshes(capi, "goose", new BirdMeshes.Candidate("greylag", new AssetLocation("flocks", "shapes/entity/bird/greylag.json"), "fly-flap", "fly-soar")),
            },
            // Our own model (tools/make_crane.py). Cranes beat slower and deeper than geese, glide more, and fly higher.
            ["crane"] = new Species
            {
                Code = "crane", Scale = 2.5f, SpeedMin = 15, SpeedMax = 19,
                FlapSeconds = 0.8f, SoarSeconds = 2.4f, BlendSeconds = 0.45f,
                FlapBeatsMin = 6, FlapBeatsMax = 12, SoarLoopsMin = 1, SoarLoopsMax = 2,
                // cranes move earlier than geese in autumn and early in spring, through the day, in V's of 7-25
                SpringStart = 0.02f, SpringEnd = 0.18f, AutumnStart = 0.55f, AutumnEnd = 0.75f,
                Hours = new float[] { 8, 17 }, MaxFlocksPerDay = 2, CountMin = 7, CountMax = 25,
                AltitudeMin = 75, AltitudeMax = 140,
                Meshes = new BirdMeshes(capi, "crane", new BirdMeshes.Candidate("crane", new AssetLocation("flocks", "shapes/entity/bird/crane.json"), "fly-flap", "fly-soar")),
            },
            // Vanilla's unused hawk shape, with sparrow and chicken fallbacks in case an update removes it.
            ["hawk"] = new Species
            {
                Code = "hawk",
                // raptors ride midday thermals; the big passage is autumn, spring is lighter. Loose groups of 3-10.
                SpringStart = 0.08f, SpringEnd = 0.22f, SpringWeight = 0.5f, AutumnStart = 0.55f, AutumnEnd = 0.72f,
                Hours = new float[] { 10, 15 }, MaxFlocksPerDay = 2, CountMin = 3, CountMax = 10, Loose = true,
                Meshes = new BirdMeshes(capi, "hawk",
                    new BirdMeshes.Candidate("hawk", new AssetLocation("game", "shapes/entity/animal/bird/raptor/hawk/hawk.json"), "fly-flap", "fly-soar"),
                    // the sparrow shape points at bird/sparrow/..., but the file ships in bird/passerine/
                    new BirdMeshes.Candidate("treesparrow", new AssetLocation("game", "shapes/entity/animal/bird/passerine/treesparrow-adult-male.json"), "flap", "soar", "entity/animal/bird/passerine/treesparrow-adult-male"),
                    new BirdMeshes.Candidate("chicken", new AssetLocation("game", "shapes/entity/animal/bird/chicken/chicken-hen.json"), "fly", "fly")),
            },
        };
    }
}
