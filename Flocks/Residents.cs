using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Flocks
{
    /// <summary>Server config, ModConfig/flocks-server.json. Written with defaults on first run.</summary>
    public class FlocksServerConfig
    {
        /// <summary>Greylag geese settle on lakes near players from spring migration until autumn migration.</summary>
        public bool ResidentGeese = true;
        /// <summary>Share of candidate lakes (0..1) that get a resident group; picked from world seed + lake position.</summary>
        public float LakeChance = 0.35f;
        public int MinPerLake = 4, MaxPerLake = 8;
        /// <summary>Most resident geese alive at once across the server.</summary>
        public int MaxResidents = 40;
        /// <summary>Game days a lake stays empty after its group was killed or scared off.</summary>
        public float RespawnDelayDays = 3f;
        /// <summary>Lake check interval, real seconds.</summary>
        public int CheckSeconds = 20;
        /// <summary>How far around each player lakes are looked for (blocks).</summary>
        public int ScanRadius = 96;
        /// <summary>Smallest lake (fresh-water surface blocks) that can hold residents.</summary>
        public int MinLakeSize = 36;
        /// <summary>New residents fly in and land instead of appearing on the water (when the approach is loaded).</summary>
        public bool ArriveByAir = true;
        /// <summary>Residents are never placed closer than this to a player (blocks).</summary>
        public int MinPlayerDistance = 24;
    }

    [ProtoContract]
    public class ResidentSave
    {
        [ProtoMember(1)] public Dictionary<long, double> CooldownUntilDays = new();
    }

    /// <summary>
    /// Keeps small resident groups of flocks:greylag on some lakes near players during the goose's resident season.
    /// Lake sites are chunk-centre columns (every 32 blocks, world-aligned) that are fresh water, pass a seeded chance,
    /// sit in a lake of MinLakeSize+ surface blocks, and are the lowest-keyed passing centre of that lake (so one site
    /// per lake, or a few on a huge one). A site's geese carry its key; a check counts them where they are.
    /// </summary>
    public class ResidentManager
    {
        public const int Grid = 32, MaxFill = 800;
        private readonly ICoreServerAPI sapi;
        public readonly FlocksServerConfig Cfg;
        private ResidentSave save = new();
        private readonly Dictionary<long, Site> sites = new();
        private readonly HashSet<long> notSites = new();
        private long checks;
        private EntityProperties type;

        public class Site
        {
            public long Key; public BlockPos Centre; public List<BlockPos> Water; public int Size;
            public int Count; public long SeenCheck = -10; public int SeenStreak; public string Status = "new";
        }

        public ResidentManager(ICoreServerAPI sapi, FlocksServerConfig cfg)
        {
            this.sapi = sapi; Cfg = cfg;
            sapi.Event.SaveGameLoaded += () =>
            {
                try { var b = sapi.WorldManager.SaveGame.GetData("flocks-residents"); if (b != null) save = SerializerUtil.Deserialize<ResidentSave>(b) ?? new(); }
                catch (Exception ex) { sapi.Logger.Warning("[Flocks] resident cooldowns unreadable, starting fresh: {0}", ex.Message); save = new(); }
            };
            sapi.Event.GameWorldSave += () => sapi.WorldManager.SaveGame.StoreData("flocks-residents", SerializerUtil.Serialize(save));
            sapi.Event.RegisterGameTickListener(_ => { try { Check(); } catch (Exception ex) { sapi.Logger.Error("[Flocks] resident check failed: {0}", ex); } },
                Math.Max(5, Cfg.CheckSeconds) * 1000);
        }

        private static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        private double Chance(int cx, int cz)
        {
            unchecked
            {
                uint h = 2166136261;
                void Mix(int v) { for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (8 * i)); h *= 16777619; } }
                Mix(sapi.World.Seed); Mix(cx); Mix(cz); Mix(0x6007e);
                return (h & 0xffffff) / (double)0x1000000;
            }
        }

        public void Check()
        {
            if (!Cfg.ResidentGeese) return;
            checks++;
            type ??= sapi.World.GetEntityType(new AssetLocation("flocks", "greylag"));
            if (type == null) return;
            var ba = sapi.World.BlockAccessor;
            var players = sapi.World.AllOnlinePlayers.Select(p => p.Entity).Where(e => e != null).ToList();
            var near = new HashSet<long>();
            foreach (var pe in players)
            {
                int r = Cfg.ScanRadius / Grid + 1, pcx = (int)Math.Floor(pe.Pos.X / Grid), pcz = (int)Math.Floor(pe.Pos.Z / Grid);
                for (int cx = pcx - r; cx <= pcx + r; cx++)
                    for (int cz = pcz - r; cz <= pcz + r; cz++)
                    {
                        long k = Key(cx, cz);
                        if (notSites.Contains(k)) continue;
                        if (!sites.ContainsKey(k) && !TryMakeSite(ba, cx, cz)) continue;
                        if (sites.ContainsKey(k)) near.Add(k);
                    }
            }

            int total = sapi.World.LoadedEntities.Values.Count(e => e.Alive && e.Code == type.Code && e.WatchedAttributes.HasAttribute(EntityBehaviorWaterfowl.SiteKey));
            double today = sapi.World.Calendar.TotalDays;
            foreach (var k in near)
            {
                var s = sites[k];
                if (ba.GetChunkAtBlockPos(s.Centre) == null) { s.SeenStreak = 0; s.Status = "unloaded"; continue; }
                int prev = s.Count; bool consecutive = s.SeenCheck == checks - 1;
                s.Count = sapi.World.GetEntitiesAround(s.Centre.ToVec3d(), 64, 16,
                    e => e.Alive && e.Code == type.Code && e.WatchedAttributes.GetLong(EntityBehaviorWaterfowl.SiteKey, long.MinValue) == k).Length;
                s.SeenStreak = consecutive ? s.SeenStreak + 1 : 1; s.SeenCheck = checks;
                // a group that was here a check ago and is now gone was killed or scared off: rest the lake
                if (consecutive && prev > 0 && s.Count == 0) save.CooldownUntilDays[k] = today + Cfg.RespawnDelayDays;

                var cal = sapi.World.Calendar;
                bool season = MigrationScheduler.ResidentSeason(Species.GooseSpringStart, Species.GooseAutumnStart, cal.GetSeasonRel(s.Centre), cal.OnGetLatitude(s.Centre.Z));
                if (!season) { s.Status = s.Count > 0 ? "leaving (out of season)" : "out of season"; continue; }
                if (save.CooldownUntilDays.TryGetValue(k, out double until) && today < until) { s.Status = $"resting {until - today:0.0} more days"; continue; }
                if (s.Count > 0) { s.Status = $"resident x{s.Count}"; continue; }
                // wait one more check after the lake loads, so geese saved in the chunk have loaded before we count
                if (s.SeenStreak < 2) { s.Status = "waiting for chunk entities"; continue; }
                if (total >= Cfg.MaxResidents) { s.Status = "server cap reached"; continue; }
                double nearest = players.Count == 0 ? 1e9 : players.Min(p => p.Pos.HorDistanceTo(s.Centre.ToVec3d()));
                if (nearest < Cfg.MinPlayerDistance) { s.Status = "player too close"; continue; }
                int n = Math.Min(Cfg.MaxResidents - total, Cfg.MinPerLake + sapi.World.Rand.Next(Math.Max(1, Cfg.MaxPerLake - Cfg.MinPerLake + 1)));
                bool air = Spawn(s, n);
                s.Count = n; total += n;
                s.Status = $"resident x{n} (arrived {(air ? "by air" : "on the water")})";
                sapi.Logger.Notification("[Flocks] {0} resident geese {1} at {2}", n, air ? "flying in to" : "placed on", s.Centre);
            }
        }

        private bool TryMakeSite(IBlockAccessor ba, int cx, int cz)
        {
            long k = Key(cx, cz);
            int x = cx * Grid + Grid / 2, z = cz * Grid + Grid / 2;
            if (Chance(cx, cz) >= Cfg.LakeChance) { notSites.Add(k); return false; }
            if (!Water(ba, x, z, out int y, out bool loaded)) { if (loaded) notSites.Add(k); return false; }
            var water = Fill(ba, x, y, z);
            // one site per lake: another passing centre in the same water with a lower key owns it
            foreach (var c in water)
            {
                if (GameMath.Mod(c.X, Grid) != Grid / 2 || GameMath.Mod(c.Z, Grid) != Grid / 2) continue;
                int ox = (int)Math.Floor(c.X / (double)Grid), oz = (int)Math.Floor(c.Z / (double)Grid);
                long ok = Key(ox, oz);
                if (ok < k && Chance(ox, oz) < Cfg.LakeChance) { notSites.Add(k); return false; }
            }
            if (water.Count < Cfg.MinLakeSize) { notSites.Add(k); return false; }
            sites[k] = new Site { Key = k, Centre = new BlockPos(x, y, z), Water = water, Size = water.Count };
            return true;
        }

        private static bool Water(IBlockAccessor ba, int x, int z, out int y, out bool loaded)
        {
            y = ba.GetRainMapHeightAt(x, z);
            loaded = y > 0 && ba.GetChunkAtBlockPos(new BlockPos(x, y, z)) != null;
            if (!loaded) return false;
            var b = ba.GetBlock(new BlockPos(x, y, z), BlockLayersAccess.Fluid);
            return b != null && b.IsLiquid() && b.LiquidCode == "water";
        }

        private static List<BlockPos> Fill(IBlockAccessor ba, int sx, int y, int sz)
        {
            var seen = new HashSet<long> { ((long)sx << 32) ^ (uint)sz };
            var q = new Queue<(int, int)>(); q.Enqueue((sx, sz));
            var res = new List<BlockPos>();
            while (q.Count > 0 && res.Count < MaxFill)
            {
                var (x, z) = q.Dequeue();
                res.Add(new BlockPos(x, y, z));
                foreach (var (nx, nz) in new[] { (x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1) })
                    if (seen.Add(((long)nx << 32) ^ (uint)nz) && Water(ba, nx, nz, out int ny, out _) && ny == y) q.Enqueue((nx, nz));
            }
            return res;
        }

        /// <summary>Spawns n geese for a site, gliding in from ~70 blocks out when that approach is loaded.</summary>
        private bool Spawn(Site s, int n)
        {
            var rand = sapi.World.Rand; var ba = sapi.World.BlockAccessor;
            double lat = sapi.World.Calendar.OnGetLatitude(s.Centre.Z);
            // in spring they come up from the equator side; any other time from a random side
            double from = (lat >= 0 ? 0 : Math.PI) + (rand.NextDouble() - 0.5) * 1.2;
            var startCol = new BlockPos((int)(s.Centre.X + Math.Sin(from) * 70), s.Centre.Y, (int)(s.Centre.Z + Math.Cos(from) * 70));
            bool air = Cfg.ArriveByAir && ba.GetChunkAtBlockPos(startCol) != null;
            int pool = Math.Min(s.Water.Count, Math.Max(Cfg.MinLakeSize, n * 4));
            for (int i = 0; i < n; i++)
            {
                var c = s.Water[rand.Next(pool)];
                var to = new Vec3d(c.X + 0.2 + rand.NextDouble() * 0.6, c.Y + 0.75, c.Z + 0.2 + rand.NextDouble() * 0.6);
                var e = sapi.World.ClassRegistry.CreateEntity(type);
                e.WatchedAttributes.SetLong(EntityBehaviorWaterfowl.SiteKey, s.Key);
                Vec3d start = to;
                if (air)
                {
                    int ground = Math.Max(s.Centre.Y, ba.GetRainMapHeightAt(startCol.X, startCol.Z));
                    start = new Vec3d(startCol.X + (rand.NextDouble() - 0.5) * 10, ground + 30 + rand.NextDouble() * 6, startCol.Z + (rand.NextDouble() - 0.5) * 10);
                }
                e.Pos.SetPos(start);
                e.Pos.Yaw = (float)(rand.NextDouble() * Math.PI * 2);
                sapi.World.SpawnEntity(e);
                if (air) e.GetBehavior<EntityBehaviorWaterfowl>()?.Arrive(start, to, 9f + (float)rand.NextDouble() * 3);
            }
            return air;
        }

        public string Report()
        {
            var cal = sapi.World.Calendar; double today = cal.TotalDays;
            var sb = new StringBuilder($"Resident geese {(Cfg.ResidentGeese ? "on" : "OFF")}: {sites.Count} lake site(s) known, {notSites.Count} grid points rejected, check every {Cfg.CheckSeconds}s, cap {Cfg.MaxResidents}.");
            foreach (var s in sites.Values.OrderBy(s => s.Centre.X))
            {
                bool season = MigrationScheduler.ResidentSeason(Species.GooseSpringStart, Species.GooseAutumnStart, cal.GetSeasonRel(s.Centre), cal.OnGetLatitude(s.Centre.Z));
                string cd = save.CooldownUntilDays.TryGetValue(s.Key, out double u) && u > today ? $", cooldown {u - today:0.0} d" : "";
                sb.Append($"\n  {s.Centre.X},{s.Centre.Y},{s.Centre.Z} size {s.Size}: {s.Status}; geese {s.Count}, {(season ? "in season" : "out of season")}{cd}");
            }
            return sb.ToString();
        }
    }
}
