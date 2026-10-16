using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Flocks
{
    /// <summary>
    /// One kind of bird, loaded from a data file in config/flocks-species/ (any mod's domain, so other mods can add
    /// birds): how its drawn flocks look and move, when it migrates, and, for birds that land, which entity its landed
    /// groups are and how big a landing flock is. The landed bird's behaviour tuning (flee and takeoff distances, cruise
    /// speed, sounds) lives in its entity file, as with vanilla animals.
    /// </summary>
    public class Species
    {
        public string Code;
        /// <summary>Plural name for messages, e.g. "greylag geese".</summary>
        public string Name;

        // ---- drawn flocks (client)
        /// <summary>Shapes to try, in order, for the drawn flocks; the first that loads is used.</summary>
        public SkyShape[] Shapes;
        public class SkyShape
        {
            public string Name, Shape, Flap = "fly-flap", Soar = "fly-soar";
            /// <summary>A texture path (game domain) to use when the shape's own texture path is wrong.</summary>
            public string TexOverride;
        }
        /// <summary>Drawing scale, one fixed size at every distance (1 block = 1 m). Set directly, from Wingspan once the
        /// meshes are baked, or from SizeFromEntity's client size so drawn and real birds match.</summary>
        public float Scale = 1f;
        /// <summary>Real wingspan in blocks; 0 = Scale is set directly.</summary>
        public float Wingspan;
        public string SizeFromEntity;
        public double SpeedMin = 18, SpeedMax = 24;
        /// <summary>One wingbeat, one soar loop, one flap&lt;-&gt;glide ease (seconds).</summary>
        public float FlapSeconds = 0.8f, SoarSeconds = 1.6f, BlendSeconds = 0.4f;
        /// <summary>Each bird flaps FlapBeats (min..max inclusive) then glides SoarLoops, and repeats.</summary>
        public int FlapBeatsMin = 3, FlapBeatsMax = 7, SoarLoopsMin = 1, SoarLoopsMax = 3;
        /// <summary>Cruising height above the player's ground, blocks (picked per flock).</summary>
        public double AltitudeMin = 60, AltitudeMax = 120;
        /// <summary>V formation, or a loose scattered group.</summary>
        public bool Loose;
        /// <summary>Distant calls from the flying flock: sound path prefix in the flocks domain (variants 1..CallVariants),
        /// and the gap between calls in seconds. Null = silent.</summary>
        public string CallSound; public int CallVariants;
        public float CallGapMin = 6, CallGapMax = 15;

        // ---- migration (both sides)
        /// <summary>Migration windows as seasonRel (0 = start of spring, hemisphere-local); weight scales each window.
        /// Birds that land are resident from SpringStart to AutumnStart (MigrationScheduler.ResidentSeason).</summary>
        public float SpringStart = 0.05f, SpringEnd = 0.25f, AutumnStart = 0.55f, AutumnEnd = 0.80f;
        public float SpringWeight = 1f, AutumnWeight = 1f;
        /// <summary>Game hours they fly, as start/end pairs.</summary>
        public float[] Hours = { 7, 10, 15, 19 };
        /// <summary>Drawn flocks on a peak migration day (fractions round randomly); fewer toward the edges of a window.</summary>
        public float MaxFlocksPerDay = 2f;
        /// <summary>Birds in a drawn (passing) flock.</summary>
        public int CountMin = 7, CountMax = 25;

        // ---- landing (server)
        /// <summary>The entity its landed groups are made of (with the flocks.waterfowl behaviour); null = it only passes over.</summary>
        public string LandedEntity;
        /// <summary>Birds in a landing flock (a new one; a returning flock comes back with whoever survived).</summary>
        public int LandingMin = 7, LandingMax = 12;
        /// <summary>Smallest lake (surface blocks of its liquid) it lands on or spawns on; 0 = the server's MinLakeSize.
        /// Ducks take smaller ponds than geese.</summary>
        public int MinLakeSize;

        public int MinLake(int serverDefault) => MinLakeSize > 0 ? MinLakeSize : serverDefault;
        /// <summary>"water": floats on open fresh water (geese, ducks). "sea": the same on salt water, the sea along the
        /// coast (gulls). "wade": stands on the bottom of water one block deep (herons): rests, stalks and strikes there,
        /// and lands and spawns only in such water. "air": a bird of prey that perches and soars over open high ground
        /// (eagles, flocks.raptor), resident all year, no water needed. "ground": walks and feeds on open ground and
        /// flies up into the trees (crows, flocks.corvid). "wood": a woodland bird of the night that roosts in trees and
        /// hunts from branches (owls, flocks.owl).</summary>
        public string Habitat = "water";
        public bool Wades => Habitat == "wade";
        public bool Soars => Habitat == "air";
        public bool Sea => Habitat == "sea";
        /// <summary>"ground": a land bird (crows, flocks.corvid) that forages on open ground and perches in trees.</summary>
        public bool Ground => Habitat == "ground";
        /// <summary>"wood": a woodland bird (owls, flocks.owl) that roosts in trees by day and hunts from them at night.</summary>
        public bool Woodland => Habitat == "wood";
        /// <summary>The liquid it floats on, lands on and spawns on (a block's LiquidCode).</summary>
        public string Liquid => Sea ? "saltwater" : "water";
        /// <summary>Resident all year: no resident season, no autumn departure, and its drawn flocks (if any) pass on any
        /// day, in no particular direction, instead of in migration windows.</summary>
        public bool YearRound;
        /// <summary>Scales how much a flush or a kill disturbs its flock (lake wariness): bold birds like gulls get used
        /// to people and take longer to give up a place.</summary>
        public double WarinessFactor = 1;
        /// <summary>Whether landing flocks of it arrive on spring migration days (herons don't; they spawn on their own).</summary>
        public bool ArriveInFlocks = true;

        /// <summary>Realms it lives in, for the Biomes mod (BiomesCompat): its landing flocks only come down, and its passing
        /// flocks are only drawn, where Biomes puts one of these realms. Null = its landed entity's biorealm attribute
        /// (patches/biomes.json); for a bird that only passes over, set it here (the patch does, for cranes).</summary>
        public string[] Biorealm;

        public bool Lands => !string.IsNullOrEmpty(LandedEntity);
        public AssetLocation LandedEntityCode => Lands ? new AssetLocation(LandedEntity) : null;

        // ---- set up at load
        /// <summary>Baked meshes for the drawn flocks (client only; null on the server).</summary>
        [Newtonsoft.Json.JsonIgnore] public BirdMeshes Meshes;

        /// <summary>True wingspan in blocks as drawn up close; formation spacing scales from it.</summary>
        public double SpanBlocks => (Meshes != null && Meshes.Span > 0 ? Meshes.Span : 1) * Scale;

        public void FitScale() { if (Wingspan > 0 && Meshes != null && Meshes.Span > 0) Scale = Wingspan / Meshes.Span; }

        public const string Folder = "config/flocks-species/";

        /// <summary>Every species data file, keyed by code. Each side keeps its own copy (client and server share a process
        /// in singleplayer, and only the client has meshes).</summary>
        public static Dictionary<string, Species> LoadAll(ICoreAPI api)
        {
            var all = new Dictionary<string, Species>();
            foreach (var asset in api.Assets.GetManyInCategory("config", Folder.Substring("config/".Length), null, true))
            {
                try
                {
                    var sp = asset.ToObject<Species>();
                    if (string.IsNullOrEmpty(sp?.Code)) { api.Logger.Warning("[Flocks] species file {0} has no code; skipped", asset.Location); continue; }
                    all[sp.Code] = sp;
                }
                catch (Exception ex) { api.Logger.Error("[Flocks] species file {0} is unreadable: {1}", asset.Location, ex.Message); }
            }
            api.Logger.Notification("[Flocks] {0} species: {1}", all.Count, string.Join(", ", all.Values.Select(s => s.Code + (s.Lands ? " (lands as " + s.LandedEntity + ")" : ""))));
            return all;
        }

        /// <summary>Client: the drawn-flock meshes, from the shape candidates (baked later, at LevelFinalize).</summary>
        public void CreateMeshes(ICoreClientAPI capi)
        {
            var candidates = (Shapes ?? Array.Empty<SkyShape>()).Select(s =>
                new BirdMeshes.Candidate(s.Name ?? s.Shape, new AssetLocation(s.Shape), s.Flap, s.Soar, s.TexOverride)).ToArray();
            Meshes = new BirdMeshes(capi, Code, candidates);
        }
    }
}
