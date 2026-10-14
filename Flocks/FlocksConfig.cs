namespace Flocks
{
    /// <summary>Client config, ModConfig/flocks.json. Written with defaults on first run.</summary>
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
        /// <summary>A flock held back by a storm or another flock still in the sky gives up after this many game hours.</summary>
        public float MaxDelayHours = 3f;
        /// <summary>Testing: every day is a peak autumn migration day for every species, whatever the season.</summary>
        public bool ForceMigration = false;
        /// <summary>Write each day's plan to client-main.log.</summary>
        public bool LogSchedule = true;
        /// <summary>Reserved: enables fantasy creatures (the amphithere). Not read anywhere yet; the amphithere ships as
        /// assets only (shapes/entity/amphithere.json) with no entity, spawning or AI.</summary>
        public bool FantasyCreatures = false;
    }
}
