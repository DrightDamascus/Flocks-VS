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
        /// <summary>Drawing scale, one fixed size at every distance (1 block = 1 m). Set directly, or from Wingspan by
        /// FitScale() once the meshes are baked; the goose takes the landed entity's size (FlocksModSystem).</summary>
        public float Scale = 1f;
        /// <summary>Real wingspan in blocks; 0 = Scale is set directly.</summary>
        public float Wingspan;
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

        /// <summary>Distant calls from the flying flock: sound path prefix in the flocks domain (variants 1..CallVariants),
        /// and the gap between calls in seconds. Null = silent (the hawk).</summary>
        public string CallSound; public int CallVariants;
        public float CallGapMin = 6, CallGapMax = 15;

        /// <summary>True wingspan in blocks as drawn up close; formation spacing scales from it.</summary>
        public double SpanBlocks => (Meshes.Span > 0 ? Meshes.Span : 1) * Scale;

        public void FitScale() { if (Wingspan > 0 && Meshes.Span > 0) Scale = Wingspan / Meshes.Span; }

        /// <summary>Goose window edges, shared with the server's resident geese (Residents.cs).</summary>
        public const float GooseSpringStart = 0.03f, GooseAutumnStart = 0.62f;

        public static Dictionary<string, Species> Create(ICoreClientAPI capi) => new()
        {
            // Our own model (tools/make_greylag.py). Geese flap steadily with short glides.
            ["goose"] = new Species
            {
                // the landed entity's size, read from entities/greylag.json client.size at load (0.55: 1.4 blocks
                // across; a real greylag is 1.5-1.8 m), so drawn and real geese always match
                Code = "goose", Scale = (float)FlockRenderer.EntityScale, SpeedMin = 16, SpeedMax = 21,
                FlapSeconds = 0.5f, SoarSeconds = 2f, BlendSeconds = 0.35f,
                FlapBeatsMin = 8, FlapBeatsMax = 16, SoarLoopsMin = 1, SoarLoopsMax = 1,
                // geese move late in autumn and early in spring, mornings and late afternoons, in V's of 7-25
                SpringStart = GooseSpringStart, SpringEnd = 0.22f, AutumnStart = GooseAutumnStart, AutumnEnd = 0.85f,
                Hours = new float[] { 6.5f, 10, 15.5f, 19 }, MaxFlocksPerDay = 3, CountMin = 7, CountMax = 25,
                CallSound = "goose/flight", CallVariants = 3, CallGapMin = 6, CallGapMax = 15,
                Meshes = new BirdMeshes(capi, "goose", new BirdMeshes.Candidate("greylag", new AssetLocation("flocks", "shapes/entity/bird/greylag.json"), "fly-flap", "fly-soar")),
            },
            // Our own model (tools/make_crane.py). Cranes beat slower and deeper than geese, glide more, and fly higher.
            ["crane"] = new Species
            {
                // common crane: 2.0-2.4 m wingspan
                Code = "crane", Wingspan = 2.2f, SpeedMin = 15, SpeedMax = 19,
                FlapSeconds = 0.8f, SoarSeconds = 2.4f, BlendSeconds = 0.45f,
                FlapBeatsMin = 6, FlapBeatsMax = 12, SoarLoopsMin = 1, SoarLoopsMax = 2,
                // cranes move earlier than geese in autumn and early in spring, through the day, in V's of 7-25
                SpringStart = 0.02f, SpringEnd = 0.18f, AutumnStart = 0.55f, AutumnEnd = 0.75f,
                Hours = new float[] { 8, 17 }, MaxFlocksPerDay = 2, CountMin = 7, CountMax = 25,
                AltitudeMin = 75, AltitudeMax = 140,
                CallSound = "crane/call", CallVariants = 6, CallGapMin = 8, CallGapMax = 20,
                Meshes = new BirdMeshes(capi, "crane", new BirdMeshes.Candidate("crane", new AssetLocation("flocks", "shapes/entity/bird/crane.json"), "fly-flap", "fly-soar")),
            },
            // Vanilla's unused hawk shape, with sparrow and chicken fallbacks in case an update removes it.
            ["hawk"] = new Species
            {
                // buzzard-sized: about 1.2 m wingspan (also applied to the sparrow and chicken fallback shapes)
                Code = "hawk", Wingspan = 1.2f,
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
