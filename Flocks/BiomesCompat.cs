using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Flocks
{
    /// <summary>
    /// Support for the Biomes mod (modid biomes, server only), which splits the world into realms and lets each animal
    /// spawn only in its own. Biomes vets vanilla's spawning itself, so resident birds already follow it (their realms
    /// come from patches/biomes.json). What it can't see are the flocks this mod places itself: landing flocks, and the
    /// passing flocks each client draws in the sky. Those ask here.
    /// Biomes is optional, so its public API (Biomes.Api.ExternalData.EntityIsValid, EntityBiomeData) is reached by
    /// reflection; without Biomes, or if its API ever changes, everything is allowed (and the change is logged once).
    /// A species' realms: its species file's biorealm list, else its landed entity's biorealm attribute. A species with
    /// neither is allowed everywhere.
    /// </summary>
    public static class BiomesCompat
    {
        private static bool resolved, available, warned;
        private static MethodInfo entityIsValid, toBiomeData;
        private static Type entityDataType;
        private static PropertyInfo validRealms;
        /// <summary>Biomes' realm data per species code (null: the species has no realms, so it is allowed everywhere).</summary>
        private static readonly Dictionary<string, object> dataBySpecies = new();

        /// <summary>Whether Biomes is running on this side and its API was found.</summary>
        public static bool Active(ICoreAPI api)
        {
            if (resolved) return available;
            resolved = true;
            if (api.Side != EnumAppSide.Server || !api.ModLoader.IsModEnabled("biomes")) return false;
            try
            {
                var asm = api.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "Biomes.BiomesModSystem")?.GetType().Assembly;
                var data = asm?.GetType("Biomes.Api.ExternalData");
                var registry = asm?.GetType("Biomes.Api.ExternalRegistry");
                entityDataType = asm?.GetType("Biomes.Api.EntityBiomeData");
                entityIsValid = data?.GetMethod("EntityIsValid", BindingFlags.Public | BindingFlags.Static);
                toBiomeData = entityDataType?.GetMethod("ToBiomeData");
                validRealms = registry?.GetProperty("ValidRealms", BindingFlags.Public | BindingFlags.Static);
                available = entityIsValid != null && toBiomeData != null && validRealms != null;
                if (!available) api.Logger.Warning("[Flocks] Biomes is installed, but its API isn't the one Flocks knows; flocks ignore realms.");
                else api.Logger.Notification("[Flocks] Biomes found: landing flocks and passing flocks follow its realms.");
            }
            catch (Exception ex)
            {
                api.Logger.Warning("[Flocks] Biomes is installed, but its API couldn't be reached ({0}); flocks ignore realms.", ex.Message);
                available = false;
            }
            return available;
        }

        /// <summary>Whether this species may be at pos: always true without Biomes. Seasons are left to this mod.</summary>
        public static bool Allows(ICoreAPI api, Species sp, BlockPos pos)
        {
            if (sp == null || !Active(api)) return true;
            try
            {
                if (!dataBySpecies.TryGetValue(sp.Code, out var data)) dataBySpecies[sp.Code] = data = Build(api, sp);
                return data == null || (bool)entityIsValid.Invoke(null, new[] { pos, data });
            }
            catch (Exception ex)
            {
                if (!warned) { warned = true; api.Logger.Warning("[Flocks] asking Biomes about {0} failed ({1}); flocks ignore realms.", sp.Code, (ex.InnerException ?? ex).Message); }
                return true;
            }
        }

        /// <summary>The realm names this species lists, from its species file or its landed entity (null if none).</summary>
        public static string[] Realms(ICoreAPI api, Species sp)
        {
            if (sp.Biorealm != null) return sp.Biorealm;
            if (!sp.Lands) return null;
            var attr = api.World.GetEntityType(sp.LandedEntityCode)?.Attributes?["biorealm"];
            return attr != null && attr.Exists ? attr.AsArray<string>() : null;
        }

        /// <summary>Biomes' own realm data for the species (an EntityBiomeData turned into BiomeData), or null if it lists
        /// no realms. Realm names Biomes doesn't know are left out, as Biomes does for animals.</summary>
        private static object Build(ICoreAPI api, Species sp)
        {
            var realms = Realms(api, sp);
            if (realms == null) return null;
            var known = new HashSet<string>(((IEnumerable)validRealms.GetValue(null)).Cast<string>());
            var unknown = realms.Where(r => !known.Contains(r)).ToList();
            if (unknown.Count > 0) api.Logger.Warning("[Flocks] Biomes doesn't know the realm(s) {0} listed for {1}; left out.", string.Join(", ", unknown), sp.Code);
            object d = Activator.CreateInstance(entityDataType);
            foreach (var f in entityDataType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.Name == "realms") f.SetValue(d, realms.Where(known.Contains).ToList());
                else if (f.Name == "river") f.SetValue(d, Enum.Parse(f.FieldType, "Both"));     // rivers or not, either
                else if (f.Name == "seasons") f.SetValue(d, Activator.CreateInstance(f.FieldType));   // empty: every season
            }
            return toBiomeData.Invoke(d, null);
        }
    }
}
