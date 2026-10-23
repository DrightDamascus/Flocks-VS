using System.Collections.Generic;

namespace Flocks
{
    /// <summary>
    /// ModConfig/flocks.json: the passing flocks. Written with defaults on first run. The server plans and flies them
    /// (MigrationScheduler, SkyFlocks), so everything here but FlockCallVolume is read from the server's copy: in single
    /// player that is this same file; on a multiplayer server, the server's, and a player's own copy only sets how loud
    /// the flocks call for them.
    /// </summary>
    public class FlocksConfig
    {
        /// <summary>Flocks fly over on their own during the migration seasons. /flocks send works either way.</summary>
        public bool AutoMigration = true;
        /// <summary>Scales how many flocks a migration day gets (1 = default, 0.5 = half, 2 = double).</summary>
        public float FlocksPerDayMultiplier = 1f;
        /// <summary>Wind speed at the player counted as a storm. Vanilla winds: strong breeze ~0.6, storm ~1.0.</summary>
        public float StormWind = 0.85f;
        /// <summary>Precipitation intensity (0..1) counted as a storm even without storm winds.</summary>
        public float StormPrecipitation = 0.8f;
        /// <summary>A flock held back by a storm gives up after this many game hours.</summary>
        public float MaxDelayHours = 3f;
        /// <summary>Testing: every day is a peak autumn migration day for every species, whatever the season.</summary>
        public bool ForceMigration = false;
        /// <summary>Write each player's plan for the day, and what came of each flock, to server-main.log.</summary>
        public bool LogSchedule = true;
        /// <summary>How loud the passing flocks' calls are (1 = as made, 0 = never heard); each player's own setting.
        /// Birds on the ground nearby are entities and follow the game's own sound settings instead.</summary>
        public float FlockCallVolume = 1f;
        /// <summary>Switches for the geese and cranes flying over on their own (off: never planned; /flocks send still
        /// sends one).</summary>
        public bool Geese = true, Cranes = true;
        /// <summary>Deprecated, not read: the hawk was removed in 0.19.1 (it was drawn with the game's own unused hawk
        /// shape, and real hawks rarely pass over in groups). Kept so older flocks.json files still load as they are.</summary>
        public bool Hawks = false;
        /// <summary>Kept for a fantasy creature that may come later (the amphithere, whose model ships unused in
        /// shapes/entity/amphithere.json). Nothing reads it yet.</summary>
        public bool FantasyCreatures = false;

        /// <summary>Species codes (config/flocks-species/) that never migrate on their own, for birds without a switch
        /// above, e.g. ["gull"]. /flocks send still works.</summary>
        public List<string> DisabledSpecies = new();

        /// <summary>Whether the species flies over on its own: the goose and the crane by their switches above, any
        /// other unless DisabledSpecies lists it.</summary>
        public bool SpeciesEnabled(string code)
        {
            if (code == "goose") return Geese;
            if (code == "crane") return Cranes;
            return DisabledSpecies?.Contains(code) != true;
        }
    }
}
